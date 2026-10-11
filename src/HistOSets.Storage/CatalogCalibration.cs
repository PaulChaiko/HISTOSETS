using HistOSets.Core;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

public sealed partial class CatalogStore
{
    private static ImageCalibration? ReadCalibration(SqliteConnection connection, Guid materialImageId, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, """
            SELECT pixel_size_x_m,pixel_size_y_m,source_kind,source_note,input_width,input_height,input_unit
            FROM image_calibrations WHERE material_image_id=$image AND deleted_utc IS NULL;
            """,transaction,("$image",materialImageId.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? CalibrationFromReader(reader) : null;
    }

    internal static ImageCalibration CalibrationFromReader(SqliteDataReader reader)
    {
        var calibration = new ImageCalibration(reader.GetDouble(0),reader.GetDouble(1),reader.GetString(2),reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetDouble(4),reader.IsDBNull(5) ? null : reader.GetDouble(5),reader.IsDBNull(6) ? null : reader.GetString(6));
        try { calibration.Validate(); }
        catch(ArgumentException ex) { throw new CatalogStorageException("Некорректная физическая калибровка изображения.",ex); }
        return calibration;
    }

    private static void SaveCalibration(SqliteConnection connection, SqliteTransaction transaction, MaterialImageDraft image,
        ImageMetadata metadata, string now, string origin)
    {
        if(image.Calibration is not { } calibration)
        {
            Execute(connection,"""
                UPDATE image_calibrations SET deleted_utc=$now,modified_utc=$now,revision=revision+1
                WHERE material_image_id=$image AND deleted_utc IS NULL;
                """,transaction,("$image",image.Id.ToString()),("$now",now));
            return;
        }
        // The source triple describes the whole original, never the on-screen preview or DPI.
        if(calibration.InputWidth is { } width && calibration.InputHeight is { } height && calibration.InputUnit is { } unit)
        {
            var expected = ImageCalibration.FromDimensions(width,height,unit,metadata.Width,metadata.Height);
            static bool SameScale(double actual,double expected) => Math.Abs(actual-expected) <= Math.Abs(expected)*1e-12;
            if(!SameScale(calibration.PixelSizeXMetres,expected.PixelSizeXMetres) || !SameScale(calibration.PixelSizeYMetres,expected.PixelSizeYMetres))
                throw new CatalogStorageException("Калибровка не соответствует размерам исходного изображения. Задайте физические размеры заново.");
            calibration = calibration with { InputUnit=expected.InputUnit };
        }
        var existing = Scalar(connection,"""
            SELECT id FROM image_calibrations WHERE material_image_id=$image
            ORDER BY deleted_utc IS NULL DESC,modified_utc DESC,revision DESC,id LIMIT 1;
            """,transaction,("$image",image.Id.ToString())) as string;
        var id = existing is null ? Guid.Parse(StorageFiles.Id("image-calibration/"+image.Id)) : Guid.Parse(existing);
        SaveEditorRow(connection,transaction,"image_calibrations",id,now,origin,
            [("material_image_id",image.Id.ToString()),("pixel_size_x_m",calibration.PixelSizeXMetres),("pixel_size_y_m",calibration.PixelSizeYMetres),
             ("source_kind",calibration.SourceKind),("source_note",calibration.SourceNote),("input_width",calibration.InputWidth),
             ("input_height",calibration.InputHeight),("input_unit",calibration.InputUnit)]);
    }
}

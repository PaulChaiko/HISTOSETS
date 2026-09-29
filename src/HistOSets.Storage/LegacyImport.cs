using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HistOSets.Core;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

public sealed partial class CatalogStore
{
    private sealed record PreparedImage(string OriginalPath, string StagedPath, string Key, string Hash, long Size, ImageMetadata Metadata);
    private sealed record LegacyRecord(int Index, string MaterialKey, string Locale, int GeometryIndex);

    public ImportResult ImportLegacy(string xmlPath, string sourceDirectory, Func<string, ImageMetadata> readMetadata)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var stage = Path.Combine(RootDirectory, ".import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                if (new FileInfo(xmlPath).Length > 16 * 1024 * 1024)
                    throw new CatalogStorageException("XML превышает предел 16 МБ.");
                var xmlCopy = Path.Combine(stage, "source.xml");
                File.Copy(xmlPath, xmlCopy);
                var xmlHash = StorageFiles.Hash(xmlCopy);
                var legacy = AtlasLoader.LoadFile(xmlCopy, sourceDirectory);
                if (legacy.Warnings.Count > 0)
                    throw new CatalogStorageException("Импорт отменён: сначала исправьте исходные данные.\n" + string.Join("\n", legacy.Warnings));
                var mapping = MapRecords(legacy, xmlHash);
                var prepared = new Dictionary<string, PreparedImage>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var specimen in legacy.Specimens)
                {
                    if (prepared.ContainsKey(specimen.ImagePath)) continue;
                    var relative = Path.GetRelativePath(sourceDirectory, specimen.ImagePath).Replace('\\', '/');
                    StorageFiles.Resolve(sourceDirectory, relative);
                    var staged = Path.Combine(stage, "image-" + prepared.Count);
                    using (var input = new FileStream(specimen.ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                    var metadata = readMetadata(staged);
                    if (metadata.Width <= 0 || metadata.Height <= 0 || metadata.Orientation != 1)
                        throw new CatalogStorageException("Для импорта нужен оригинал с нормальной ориентацией и положительными размерами: " + relative);
                    metadata = metadata with { DpiX = ImageCoordinates.EffectiveDpi(metadata.DpiX), DpiY = ImageCoordinates.EffectiveDpi(metadata.DpiY) };
                    var extension = metadata.MimeType switch
                    {
                        "image/jpeg" => ".jpg", "image/png" => ".png", "image/tiff" => ".tif",
                        "image/bmp" => ".bmp", "image/gif" => ".gif",
                        _ => throw new CatalogStorageException("Формат изображения пока не поддерживается: " + metadata.MimeType)
                    };
                    var hash = StorageFiles.Hash(staged);
                    prepared.Add(specimen.ImagePath, new(specimen.ImagePath, staged, "SPECIMENS/" + hash + extension, hash,
                        new FileInfo(staged).Length, metadata));
                }
                var signature = xmlHash + "\n" + string.Join("\n", prepared.Values
                    .Select(p => Path.GetRelativePath(sourceDirectory, p.OriginalPath).Replace('\\', '/') + ":" + p.Hash)
                    .Order(StringComparer.Ordinal));
                var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
                using var connection = Open(DatabasePath, SqliteOpenMode.ReadWrite);
                Validate(connection);
                var previous = Scalar(connection, "SELECT dataset_sha256 FROM import_batches LIMIT 1;") as string;
                if (previous == fingerprint) return new(true, Statistics(connection));
                if (previous is not null || Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM materials;")) > 0)
                    throw new CatalogStorageException("В каталоге уже есть другие данные. В update2 изменённый XML импортируется в новый пустой каталог кнопкой «Создать из XML». Текущие материалы не изменены.");

                // Immutable files are installed first. If SQL fails, the catalog still references its previous complete snapshot.
                foreach (var asset in prepared.Values.DistinctBy(p => p.Key)) Install(asset.StagedPath, asset.Key, asset.Hash);
                var sourceKey = "SOURCES/" + xmlHash + ".xml";
                Install(xmlCopy, sourceKey, xmlHash);
                var batchId = StorageFiles.Id("legacy-batch/" + fingerprint);
                var now = StorageFiles.UtcNow();
                var origin = (string)Scalar(connection, "SELECT catalog_id FROM catalog_metadata;")!;
                using (var transaction = connection.BeginTransaction())
                {
                    Execute(connection, """
                        INSERT INTO import_batches(id, source_name, xml_sha256, dataset_sha256, source_key, mapping_kind, imported_utc)
                        VALUES ($id, $name, $xml, $data, $key, $mapping, $now);
                        """, transaction, ("$id", batchId), ("$name", Path.GetFileName(xmlPath)), ("$xml", xmlHash),
                        ("$data", fingerprint), ("$key", sourceKey), ("$mapping", mapping.Count != mapping.Select(m => m.MaterialKey).Distinct().Count() ? "baseline-explicit-v1" : "independent-records-v1"), ("$now", now));
                    void Insert(string table, string id, params (string Column, object? Value)[] fields)
                    {
                        var all = fields.Concat(new (string, object?)[]
                        {
                            ("id", id), ("created_utc", now), ("modified_utc", now), ("origin_catalog_id", origin), ("source_batch_id", batchId)
                        }).ToArray();
                        // Table and column names are compile-time constants at every call site; values are always parameters.
                        Execute(connection, $"INSERT INTO {table} ({string.Join(',', all.Select(f => f.Item1))}) VALUES ({string.Join(',', all.Select((_, i) => "$p" + i))});",
                            transaction, all.Select((f, i) => ("$p" + i, f.Item2)).ToArray());
                    }
                    string Id(string key) => StorageFiles.Id(batchId + "/" + key);
                    foreach (var asset in prepared.Values.DistinctBy(p => p.Hash))
                        Insert("images", StorageFiles.Id("image/" + asset.Hash), ("storage_key", asset.Key), ("sha256", asset.Hash), ("byte_size", asset.Size),
                            ("pixel_width", asset.Metadata.Width), ("pixel_height", asset.Metadata.Height), ("dpi_x", asset.Metadata.DpiX), ("dpi_y", asset.Metadata.DpiY),
                            ("mime_type", asset.Metadata.MimeType), ("orientation", 1));
                    foreach (var group in mapping.GroupBy(m => m.MaterialKey))
                    {
                        var first = group.First();
                        var specimen = legacy.Specimens[first.GeometryIndex];
                        var asset = prepared[specimen.ImagePath];
                        var materialId = Id("material/" + group.Key);
                        var materialImage = Id("material-image/" + group.Key);
                        Insert("materials", materialId, ("kind", "image"));
                        Insert("material_images", materialImage, ("material_id", materialId), ("image_id", StorageFiles.Id("image/" + asset.Hash)), ("sort_order", 0));
                        for (var e = 0; e < specimen.Elements.Count; e++)
                        {
                            var element = specimen.Elements[e];
                            var elementId = Id("element/" + group.Key + "/" + e);
                            Insert("elements", elementId, ("material_id", materialId), ("sort_order", e));
                            for (var p = 0; p < element.Polygons.Count; p++)
                            {
                                var regionId = Id("region/" + group.Key + "/" + e + "/" + p);
                                Insert("regions", regionId, ("material_id", materialId), ("material_image_id", materialImage), ("element_id", elementId),
                                    ("geometry_type", "polygon"), ("coordinate_space", "pixels"), ("sort_order", p));
                                for (var vertex = 0; vertex < element.Polygons[p].Points.Count; vertex++)
                                {
                                    var point = ImageCoordinates.ToPixels(element.Polygons[p].Points[vertex], specimen.CoordinateSpace, asset.Metadata.DpiX, asset.Metadata.DpiY);
                                    Execute(connection, "INSERT INTO region_points VALUES ($region, $ordinal, $x, $y);", transaction,
                                        ("$region", regionId), ("$ordinal", vertex), ("$x", point.X), ("$y", point.Y));
                                }
                            }
                        }
                        foreach (var record in group)
                        {
                            var localized = legacy.Specimens[record.Index];
                            Insert("material_translations", Id("material-text/" + record.Index), ("material_id", materialId), ("locale", record.Locale),
                                ("name", localized.Name), ("summary", localized.Summary), ("description", localized.Description), ("sort_order", record.Index));
                            for (var e = 0; e < localized.Elements.Count; e++)
                            {
                                var element = localized.Elements[e];
                                Insert("element_translations", Id("element-text/" + record.Index + "/" + e), ("element_id", Id("element/" + group.Key + "/" + e)),
                                    ("locale", record.Locale), ("name", element.Name), ("summary", element.Summary), ("description", element.Description));
                            }
                        }
                    }
                    transaction.Commit();
                }
                Validate(connection);
                return new(false, Statistics(connection));
            }
            catch (Exception ex) when (ex is AtlasLoadException or SqliteException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new CatalogStorageException("Не удалось импортировать XML.\n" + ex.Message, ex);
            }
            finally { Directory.Delete(stage, recursive: true); }
        }
    }

    private void Install(string source, string key, string expectedHash)
    {
        var destination = StorageFiles.Resolve(RootDirectory, key);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            if (StorageFiles.Hash(destination) != expectedHash)
                throw new CatalogStorageException("Повреждён файл существующего каталога: " + key);
        }
        else File.Move(source, destination);
    }

    private static List<LegacyRecord> MapRecords(AtlasCatalog catalog, string xmlHash)
    {
        using var map = JsonDocument.Parse(Resource("baseline-map.json"));
        if (xmlHash != map.RootElement.GetProperty("xmlSha256").GetString())
            return catalog.Specimens.Select((_, i) => new LegacyRecord(i, "record-" + i, "und", i)).ToList();
        var records = new List<LegacyRecord>();
        foreach (var pair in map.RootElement.GetProperty("pairs").EnumerateArray())
        {
            var en = pair.GetProperty("en").GetInt32();
            var ru = pair.GetProperty("ru").GetInt32();
            var key = pair.GetProperty("key").GetString()!;
            var a = catalog.Specimens[en];
            var b = catalog.Specimens[ru];
            if (a.ImagePath != b.ImagePath || a.CoordinateSpace != b.CoordinateSpace || a.Elements.Count != b.Elements.Count ||
                a.Elements.Where((e, i) => e.Polygons.Count != b.Elements[i].Polygons.Count ||
                    e.Polygons.Where((p, j) => !p.Points.SequenceEqual(b.Elements[i].Polygons[j].Points)).Any()).Any())
                throw new CatalogStorageException("Не подтверждено соответствие геометрии переводов исходного атласа.");
            records.Add(new(en, key, "en", en));
            records.Add(new(ru, key, "ru", en));
        }
        if (records.Select(r => r.Index).Distinct().Count() != catalog.Specimens.Count)
            throw new CatalogStorageException("Карта переводов не покрывает все исходные записи.");
        return records.OrderBy(r => r.Index).ToList();
    }
}

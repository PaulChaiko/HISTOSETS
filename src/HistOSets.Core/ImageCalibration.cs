namespace HistOSets.Core;

/// <summary>Physical calibration of original image pixels; file DPI is never a substitute.</summary>
public sealed record ImageCalibration(
    double PixelSizeXMetres,
    double PixelSizeYMetres,
    string SourceKind = "manual_dimensions",
    string SourceNote = "",
    double? InputWidth = null,
    double? InputHeight = null,
    string? InputUnit = null)
{
    public static ImageCalibration FromDimensions(double width, double height, string unit, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Image dimensions must be positive.");
        var factor = UnitInMetres(unit);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentException("Physical dimensions must be positive finite numbers.");
        var result = new ImageCalibration(width / pixelWidth * factor, height / pixelHeight * factor,
            InputWidth: width, InputHeight: height, InputUnit: unit is "µm" or "μm" ? "um" : unit);
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (!double.IsFinite(PixelSizeXMetres) || !double.IsFinite(PixelSizeYMetres)
            || PixelSizeXMetres <= 0 || PixelSizeYMetres <= 0 || PixelSizeXMetres > 1e12 || PixelSizeYMetres > 1e12)
            throw new ArgumentException("Pixel sizes must be positive finite metres per pixel, at most 1e12.");
        if (SourceKind is not ("manual_dimensions" or "image_metadata" or "scale_bar") || SourceNote is null)
            throw new ArgumentException("Unsupported calibration source.");
        var supplied = (InputWidth.HasValue ? 1 : 0) + (InputHeight.HasValue ? 1 : 0) + (InputUnit is not null ? 1 : 0);
        if (supplied is not (0 or 3)) throw new ArgumentException("Calibration input width, height and unit must be supplied together.");
        if (supplied == 3)
        {
            if (!double.IsFinite(InputWidth!.Value) || !double.IsFinite(InputHeight!.Value) || InputWidth <= 0 || InputHeight <= 0)
                throw new ArgumentException("Calibration input dimensions must be positive finite numbers.");
            _ = UnitInMetres(InputUnit!);
        }
    }

    public static double UnitInMetres(string unit) => unit switch
    {
        "m" => 1, "cm" => 1e-2, "mm" => 1e-3, "um" or "µm" or "μm" => 1e-6, "nm" => 1e-9,
        _ => throw new ArgumentException("Unsupported physical unit.", nameof(unit))
    };
}

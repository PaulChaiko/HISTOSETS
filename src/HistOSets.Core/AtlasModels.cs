namespace HistOSets.Core;

public enum CoordinateSpace { LegacyDip, Pixels }

public readonly record struct AtlasPoint(double X, double Y);
public sealed record AtlasPolygon(IReadOnlyList<AtlasPoint> Points);
public sealed record AtlasElement(string Name, string Summary, string Description,
    IReadOnlyList<AtlasPolygon> Polygons);
public sealed record AtlasSpecimen(string Name, string ImagePath, string Summary,
    string Description, CoordinateSpace CoordinateSpace, IReadOnlyList<AtlasElement> Elements);
public sealed record AtlasCatalog(IReadOnlyList<AtlasSpecimen> Specimens,
    IReadOnlyList<string> Warnings);

public sealed class AtlasLoadException(string message, Exception? inner = null)
    : Exception(message, inner);

public static class ImageCoordinates
{
    // The legacy editor recorded WPF DIPs, not necessarily source pixels.
    // A missing/invalid DPI is interpreted as 96, matching the baseline assets.
    public static double EffectiveDpi(double dpi) => double.IsFinite(dpi) && dpi > 0 ? dpi : 96;

    public static AtlasPoint ToPixels(AtlasPoint point, CoordinateSpace space, double dpiX, double dpiY)
        => space == CoordinateSpace.Pixels ? point
            : new(point.X * EffectiveDpi(dpiX) / 96, point.Y * EffectiveDpi(dpiY) / 96);
}

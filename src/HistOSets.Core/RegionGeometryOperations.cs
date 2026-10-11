using Clipper2Lib;

namespace HistOSets.Core;

public readonly record struct RegionBounds(double X, double Y, double Width, double Height);

/// <summary>
/// Vector paint operations in original image pixels. Clipper2 resolves even-odd contours,
/// using four decimal places (0.0001 px grid). Curves are flattened with at most 0.05 px
/// chord error. Only the paint stroke is clipped to the image; existing overshoot is retained.
/// </summary>
public static class RegionGeometryOperations
{
    public const int Precision = 4;
    public const double ArcTolerance = 0.05;
    public const double MaxCoordinate = 1e9;
    public const int MaxStrokePoints = 20000;

    public static RegionGeometry? ApplyStroke(RegionGeometry? source, IReadOnlyList<AtlasPoint> stroke,
        double radius, double imageWidth, double imageHeight, bool erase = false)
    {
        try { return ApplyStrokeCore(source, stroke, radius, imageWidth, imageHeight, erase); }
        catch (ClipperLibException ex) { throw new ArgumentException("The vector operation could not be completed.", ex); }
    }

    private static RegionGeometry? ApplyStrokeCore(RegionGeometry? source, IReadOnlyList<AtlasPoint> stroke,
        double radius, double imageWidth, double imageHeight, bool erase)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        if (!double.IsFinite(radius) || radius < 0.01 || radius > MaxCoordinate
            || !double.IsFinite(imageWidth) || !double.IsFinite(imageHeight)
            || imageWidth <= 0 || imageHeight <= 0 || imageWidth > MaxCoordinate || imageHeight > MaxCoordinate)
            throw new ArgumentOutOfRangeException(nameof(radius), "Stroke radius and image bounds exceed the supported range.");
        if (stroke.Count > MaxStrokePoints) throw new ArgumentException("A stroke has too many points.", nameof(stroke));
        if (stroke.Count == 0 || (erase && source is null)) return source;
        ValidateCoordinates(stroke);
        var path = new PathD();
        foreach (var point in stroke)
            if (path.Count == 0 || path[^1].x != point.X || path[^1].y != point.Y)
                path.Add(new(point.X, point.Y));
        if ((long)path.Count * CurveSegments(radius) > 400000)
            throw new ArgumentException("The stroke is too complex for the selected radius.");
        PathsD paint;
        if (path.Count == 1)
        {
            var p = path[0];
            paint = [EllipsePath(p.x - radius, p.y - radius, radius * 2, radius * 2)];
        }
        else
            paint = Clipper.InflatePaths([path], radius, JoinType.Round, EndType.Round, 2, Precision, ArcTolerance);
        paint = Boolean(ClipType.Intersection, paint, [RectanglePath(0, 0, imageWidth, imageHeight)], FillRule.NonZero);
        if (paint.Count == 0) return source;
        var original = source is null ? new PathsD() : NormalizedPaths(source);
        // Keep v1 bytes and history intact when the operation changes no filled area.
        var changed = erase
            ? Boolean(ClipType.Intersection, original, paint, FillRule.NonZero)
            : Boolean(ClipType.Difference, paint, original, FillRule.NonZero);
        if (changed.Count == 0) return source;
        var result = erase
            ? Boolean(ClipType.Difference, original, paint, FillRule.NonZero)
            : Boolean(ClipType.Union, original, paint, FillRule.NonZero);
        if (result.Count == 0) return null;
        return FromPaths(result);
    }

    public static RegionBounds GetBounds(RegionGeometry geometry)
    {
        RegionGeometryCodec.Validate(geometry);
        if (geometry.Kind is "rectangle" or "ellipse") return new(geometry.X, geometry.Y, geometry.Width, geometry.Height);
        var points = geometry.Kind == "compound" ? geometry.Rings.SelectMany(r => r) : geometry.Points;
        var minX = double.PositiveInfinity; var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity; var maxY = double.NegativeInfinity;
        foreach (var point in points)
        {
            minX = Math.Min(minX, point.X); minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X); maxY = Math.Max(maxY, point.Y);
        }
        return new(minX, minY, maxX - minX, maxY - minY);
    }

    public static RegionGeometry Translate(RegionGeometry geometry, double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) throw new ArgumentException("Translation must be finite.");
        if (dx == 0 && dy == 0) return geometry;
        AtlasPoint Move(AtlasPoint point) => new(point.X + dx, point.Y + dy);
        return geometry.Kind switch
        {
            "polygon" => RegionGeometry.Polygon(geometry.Points.Select(Move)),
            "compound" => RegionGeometry.Compound(geometry.Rings.Select(r => r.Select(Move))),
            "rectangle" => RegionGeometry.Rectangle(geometry.X + dx, geometry.Y + dy, geometry.Width, geometry.Height),
            "ellipse" => RegionGeometry.Ellipse(geometry.X + dx, geometry.Y + dy, geometry.Width, geometry.Height),
            _ => throw new ArgumentException("Unsupported region kind.")
        };
    }

    public static bool Contains(RegionGeometry geometry, AtlasPoint point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
        if (geometry.Kind is "rectangle" or "ellipse")
        {
            var nx = (point.X - geometry.X) / geometry.Width;
            var ny = (point.Y - geometry.Y) / geometry.Height;
            return geometry.Kind == "rectangle" ? nx is >= 0 and <= 1 && ny is >= 0 and <= 1
                : Math.Pow(nx * 2 - 1, 2) + Math.Pow(ny * 2 - 1, 2) <= 1;
        }
        var inside = false;
        foreach (var ring in RegionGeometryCodec.ToRings(geometry))
        {
            var hit = Clipper.PointInPolygon(new PointD(point.X, point.Y), ToPath(ring), Precision);
            if (hit == PointInPolygonResult.IsOn) return true;
            if (hit == PointInPolygonResult.IsInside) inside = !inside;
        }
        return inside;
    }

    internal static PathsD NormalizedPaths(RegionGeometry geometry)
    {
        RegionGeometryCodec.Validate(geometry);
        PathsD paths = geometry.Kind switch
        {
            "rectangle" => [RectanglePath(geometry.X, geometry.Y, geometry.Width, geometry.Height)],
            "ellipse" => [EllipsePath(geometry.X, geometry.Y, geometry.Width, geometry.Height)],
            "polygon" => [ToPath(geometry.Points)],
            "compound" => new(geometry.Rings.Select(ToPath)),
            _ => throw new ArgumentException("Unsupported region kind.")
        };
        if (paths.Sum(p => (long)p.Count) > 200000)
            throw new ArgumentException("The region is too complex for vector operations.");
        foreach (var path in paths)
            ValidateCoordinates(path.Select(p => new AtlasPoint(p.x, p.y)));
        return Boolean(ClipType.Union, paths, new PathsD(), FillRule.EvenOdd);
    }

    private static RegionGeometry FromPaths(PathsD paths)
    {
        // Deterministic contour rotation and ordering make repeated JSON saves stable.
        var rings = paths.Select(path =>
        {
            var points = path.Select(p => new AtlasPoint(p.x == 0 ? 0 : p.x, p.y == 0 ? 0 : p.y)).ToArray();
            var start = 0;
            for (var i = 1; i < points.Length; i++)
                if (points[i].X < points[start].X || (points[i].X == points[start].X && points[i].Y < points[start].Y)) start = i;
            return Enumerable.Range(0, points.Length).Select(i => points[(start + i) % points.Length]).ToArray();
        }).OrderBy(r => r[0].X).ThenBy(r => r[0].Y).ThenBy(r => r.Length).ToArray();
        return RegionGeometry.Compound(rings);
    }

    private static PathD ToPath(IEnumerable<AtlasPoint> points) => new(points.Select(p => new PointD(p.X, p.Y)));
    private static PathD RectanglePath(double x, double y, double width, double height)
        => [new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)];
    private static PathD EllipsePath(double x, double y, double width, double height)
    {
        var radius = Math.Max(width, height) / 2;
        var count = CurveSegments(radius);
        return new(RegionGeometryCodec.ToPolygon(RegionGeometry.Ellipse(x, y, width, height), count).Points.Select(p => new PointD(p.X, p.Y)));
    }
    private static int CurveSegments(double radius)
    {
        var count = radius <= ArcTolerance ? 24 : Math.Max(24, Math.Ceiling(Math.PI / Math.Acos(1 - ArcTolerance / radius)));
        if (!double.IsFinite(count) || count > 65536) throw new ArgumentException("Curve exceeds the supported flattening precision.");
        return (int)count;
    }

    private static PathsD Boolean(ClipType operation, PathsD subject, PathsD clip, FillRule fill)
    {
        var engine = new ClipperD(Precision);
        engine.AddSubject(subject);
        engine.AddClip(clip);
        var result = new PathsD();
        if (!engine.Execute(operation, fill, result))
            throw new InvalidOperationException("The vector operation failed; the original geometry is unchanged.");
        return result;
    }

    private static void ValidateCoordinates(IEnumerable<AtlasPoint> points)
    {
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > MaxCoordinate || Math.Abs(p.Y) > MaxCoordinate))
            throw new ArgumentException("Vector operations support finite coordinates within ±1e9 original image pixels.");
    }
}

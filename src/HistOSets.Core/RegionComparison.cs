using Clipper2Lib;

namespace HistOSets.Core;

/// <summary>Comparison of filled areas in original image pixels; fractions range from zero to one.</summary>
public sealed record RegionComparisonResult
{
    public double ReferenceArea { get; }
    public double DrawnArea { get; }
    public double IntersectionArea { get; }
    public double MissedArea { get; }
    public double ExcessArea { get; }
    public double Coverage { get; }
    public double? Precision { get; }
    public double? ExcessFraction { get; }

    internal RegionComparisonResult(double referenceArea, double drawnArea, double intersectionArea,
        double missedArea, double excessArea, double coverage, double? precision, double? excessFraction)
    {
        ReferenceArea = referenceArea;
        DrawnArea = drawnArea;
        IntersectionArea = intersectionArea;
        MissedArea = missedArea;
        ExcessArea = excessArea;
        Coverage = coverage;
        Precision = precision;
        ExcessFraction = excessFraction;
    }
}

/// <summary>
/// Compares the union of reference regions with the union of a learner's regions, without
/// clipping to an image or applying a pass threshold. Each region retains its even-odd fill;
/// separately stored regions are combined as a union, so overlaps never cancel or count twice.
/// Uses the editor's 0.0001 px grid and at most 0.05 px chord error for ellipse flattening.
/// </summary>
public static class RegionComparison
{
    /// <summary>Maximum number of regions on each side of the comparison.</summary>
    public const int MaxRegions = 10000;
    /// <summary>Maximum source/flattened vertices per side and vertices in each Boolean result.</summary>
    public const int MaxVertices = 400000;
    private const double GridScale = 10000;
    private const double AreaScale = 2 * GridScale * GridScale;

    public static RegionComparisonResult Compare(IEnumerable<RegionGeometry> reference, IEnumerable<RegionGeometry> drawn)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(drawn);
        try
        {
            // Independent budgets allow an accepted reference to be reproduced in full;
            // a reference at its limit must not consume the learner's drawing capacity.
            var referencePaths = Union(reference, new ComplexityBudget(), nameof(reference));
            var referenceArea = TwiceGridArea(referencePaths);
            if (referenceArea <= 0)
                throw new ArgumentException("The reference must have a positive filled area on the supported coordinate grid.", nameof(reference));
            var drawnPaths = Union(drawn, new ComplexityBudget(), nameof(drawn));
            var drawnArea = TwiceGridArea(drawnPaths);
            var intersectionArea = drawnArea == 0 ? 0 : TwiceGridArea(Boolean(ClipType.Intersection, referencePaths, drawnPaths));
            // Intersection vertices are rounded to the same grid. Clamp any rounding overshoot
            // before deriving the two area differences; neither difference can become negative.
            intersectionArea = Int128.Clamp(intersectionArea, 0, Int128.Min(referenceArea, drawnArea));
            var missedArea = referenceArea - intersectionArea;
            var excessArea = drawnArea - intersectionArea;
            return new((double)referenceArea / AreaScale, (double)drawnArea / AreaScale,
                (double)intersectionArea / AreaScale, (double)missedArea / AreaScale,
                (double)excessArea / AreaScale, Fraction(intersectionArea, referenceArea),
                drawnArea == 0 ? null : Fraction(intersectionArea, drawnArea),
                drawnArea == 0 ? null : Fraction(excessArea, drawnArea));
        }
        catch (ClipperLibException ex)
        {
            throw new ArgumentException("The regions could not be compared at the supported coordinate precision.", ex);
        }
    }

    private static PathsD Union(IEnumerable<RegionGeometry> geometries, ComplexityBudget budget, string parameter)
    {
        var paths = new PathsD();
        foreach (var geometry in geometries)
        {
            if (geometry is null) throw new ArgumentException("A region collection cannot contain null.", parameter);
            budget.AddSource(geometry);
            // Normalize individually: applying EvenOdd to the combined collection would turn
            // an overlap between two independent regions into an unintended hole.
            var normalized = RegionGeometryOperations.NormalizedPaths(geometry);
            budget.AddNormalized(normalized);
            paths.AddRange(normalized);
        }
        return paths.Count == 0 ? paths : Boolean(ClipType.Union, paths, []);
    }

    private static PathsD Boolean(ClipType operation, PathsD subject, PathsD clip)
    {
        var engine = new ClipperD(RegionGeometryOperations.Precision);
        engine.AddSubject(subject);
        engine.AddClip(clip);
        var result = new PathsD();
        if (!engine.Execute(operation, FillRule.NonZero, result))
            throw new ArgumentException("The region comparison could not be completed.");
        if (result.Sum(path => (long)path.Count) > MaxVertices)
            throw new ArgumentException("The combined region boundary is too complex for comparison.");
        return result;
    }

    private static Int128 TwiceGridArea(PathsD paths)
    {
        // Boolean results lie on a four-decimal-place integer grid. Accumulate exact products
        // in Int128 so a small region near ±1e9 does not lose its area to double cancellation.
        // At most 400000 vertices and ±1e13 grid coordinates fit comfortably in Int128.
        Int128 sum = 0;
        foreach (var path in paths)
        {
            if (path.Count < 3) continue;
            var previousX = GridCoordinate(path[^1].x);
            var previousY = GridCoordinate(path[^1].y);
            foreach (var point in path)
            {
                var x = GridCoordinate(point.x);
                var y = GridCoordinate(point.y);
                sum += (Int128)previousX * y - (Int128)x * previousY;
                previousX = x;
                previousY = y;
            }
        }
        return Int128.Abs(sum);
    }

    private static long GridCoordinate(double coordinate)
    {
        if (!double.IsFinite(coordinate) || Math.Abs(coordinate) > RegionGeometryOperations.MaxCoordinate)
            throw new ArgumentException("Region comparison supports finite coordinates within ±1e9 original image pixels.");
        return (long)Math.Round(coordinate * GridScale, MidpointRounding.AwayFromZero);
    }

    private static double Fraction(Int128 numerator, Int128 denominator)
        => Math.Clamp((double)numerator / (double)denominator, 0, 1);

    private sealed class ComplexityBudget
    {
        private int regions;
        private long sourceVertices;
        private long normalizedVertices;

        public void AddSource(RegionGeometry geometry)
        {
            if (++regions > MaxRegions)
                throw new ArgumentException("Too many regions on one side of the comparison.");
            sourceVertices += geometry.Kind switch
            {
                "polygon" => geometry.Points.Count,
                "compound" => geometry.Rings.Sum(ring => (long)ring.Count),
                _ => 4
            };
            if (sourceVertices > MaxVertices)
                throw new ArgumentException("The source regions are too complex for comparison.");
        }

        public void AddNormalized(PathsD paths)
        {
            normalizedVertices += paths.Sum(path => (long)path.Count);
            if (normalizedVertices > MaxVertices)
                throw new ArgumentException("The flattened regions are too complex for comparison.");
        }
    }
}

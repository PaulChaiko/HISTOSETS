using HistOSets.Core;

internal static class RegionComparisonChecks
{
    internal static void Run(Action<bool, string> check)
    {
        void Near(double actual, double expected, double tolerance, string message)
            => check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, "Comparison: " + message);
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (ArgumentException) { check(true, "Comparison: " + message); return; }
            throw new Exception("Expected invalid comparison: " + message);
        }
        void Areas(RegionComparisonResult result, double reference, double drawn, double intersection, string message)
            => check(result.ReferenceArea == reference && result.DrawnArea == drawn && result.IntersectionArea == intersection
                && result.MissedArea == reference - intersection && result.ExcessArea == drawn - intersection,
                "Comparison: " + message);
        static AtlasPoint[] Box(double x, double y, double width, double height)
            => [new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)];

        var target = RegionGeometry.Rectangle(0, 0, 10, 10);
        var identical = RegionComparison.Compare([target], [target]);
        Areas(identical, 100, 100, 100, "identical answer has full overlap and no missing or excess area");
        check(identical.Coverage == 1 && identical.Precision == 1 && identical.ExcessFraction == 0,
            "Comparison: exact tracing yields full coverage and precision");

        var inside = RegionComparison.Compare([target], [RegionGeometry.Rectangle(0, 0, 5, 10)]);
        Areas(inside, 100, 50, 50, "a contained answer identifies the missed half");
        check(inside.Coverage == .5 && inside.Precision == 1 && inside.ExcessFraction == 0,
            "Comparison: incomplete tracing can have perfect precision without full coverage");

        var outside = RegionComparison.Compare([target], [RegionGeometry.Rectangle(-5, -5, 20, 20)]);
        Areas(outside, 100, 400, 100, "an oversized answer keeps all excess area, including negative coordinates");
        check(outside.Coverage == 1 && outside.Precision == .25 && outside.ExcessFraction == .75,
            "Comparison: full coverage does not conceal three quarters excess in the learner's area");

        var shifted = RegionComparison.Compare([target], [RegionGeometry.Rectangle(5, 0, 10, 10)]);
        Areas(shifted, 100, 100, 50, "partial overlap separates missed and excess halves");
        check(shifted.Coverage == .5 && shifted.Precision == .5 && shifted.ExcessFraction == .5,
            "Comparison: shifted tracing reports both kinds of error independently");

        var disjoint = RegionComparison.Compare([target], [RegionGeometry.Rectangle(20, 20, 10, 10)]);
        Areas(disjoint, 100, 100, 0, "disjoint answer has no intersection");
        check(disjoint.Coverage == 0 && disjoint.Precision == 0 && disjoint.ExcessFraction == 1,
            "Comparison: disjoint nonempty answer has zero precision and all excess");

        var touching = RegionComparison.Compare([target], [RegionGeometry.Rectangle(10, 0, 10, 10)]);
        Areas(touching, 100, 100, 0, "shared boundary alone contributes no area");
        var pointTouch = RegionComparison.Compare([target], [RegionGeometry.Rectangle(10, 10, 10, 10)]);
        Areas(pointTouch, 100, 100, 0, "touching at a single corner contributes no area");

        var empty = RegionComparison.Compare([target], []);
        Areas(empty, 100, 0, 0, "empty answer leaves the entire reference missed");
        check(empty.Coverage == 0 && empty.Precision is null && empty.ExcessFraction is null,
            "Comparison: empty answer has no fabricated zero-denominator precision");

        var hole = RegionGeometry.Compound([Box(0, 0, 10, 10), Box(2, 2, 6, 6)]);
        var filledHole = RegionComparison.Compare([hole], [target]);
        Areas(filledHole, 64, 100, 64, "filling a reference hole counts as excess");
        check(filledHole.Coverage == 1 && filledHole.Precision == .64 && filledHole.ExcessFraction == .36,
            "Comparison: a hole is excluded from the reference denominator");
        Areas(RegionComparison.Compare([target], [hole]), 100, 64, 64,
            "a hole in the answer counts as missed reference area");
        Areas(RegionComparison.Compare([hole], [RegionGeometry.Rectangle(3, 3, 2, 2)]), 64, 4, 0,
            "an answer entirely inside a reference hole is wholly excess");

        var island = RegionGeometry.Compound([Box(0, 0, 10, 10), Box(2, 2, 6, 6), Box(4, 4, 2, 2), Box(20, 0, 2, 2)]);
        Areas(RegionComparison.Compare([island], [island]), 72, 72, 72,
            "nested and disconnected islands survive the comparison");
        var reversed = RegionGeometry.Compound(island.Rings.Reverse().Select(ring => ring.Reverse()));
        check(RegionComparison.Compare([island], [reversed]) == RegionComparison.Compare([island], [island]),
            "Comparison: ring order and winding do not change filled area");

        var second = RegionGeometry.Rectangle(5, 0, 10, 10);
        var union = RegionComparison.Compare([target, second, target], [target, second, second]);
        Areas(union, 150, 150, 150, "overlapping and duplicate independent regions form a union, never XOR or double counting");
        var compoundOverlap = RegionGeometry.Compound([Box(0, 0, 10, 10), Box(5, 0, 10, 10)]);
        Areas(RegionComparison.Compare([compoundOverlap], [target, second]), 100, 150, 100,
            "overlap inside one imported compound retains its own even-odd meaning");
        Areas(RegionComparison.Compare([hole, RegionGeometry.Rectangle(2, 2, 6, 6)], [target]), 100, 100, 100,
            "a separate region can fill another region's hole without canceling its boundary");
        var bowTie = RegionGeometry.Polygon([new(0, 0), new(10, 10), new(0, 10), new(10, 0)]);
        Areas(RegionComparison.Compare([bowTie], [target]), 50, 100, 50,
            "self-intersecting imported polygon is normalized before its area is measured");

        var circle = RegionGeometry.Ellipse(0, 0, 100, 100);
        var ellipseResult = RegionComparison.Compare([circle], [circle]);
        Near(ellipseResult.ReferenceArea, Math.PI * 2500, 11, "ellipse flattening stays within the editor's chord tolerance");
        check(ellipseResult.Coverage == 1 && ellipseResult.Precision == 1 && ellipseResult.MissedArea == 0 && ellipseResult.ExcessArea == 0,
            "Comparison: repeated evaluation of the same curve is identical");
        var halfCircle = RegionComparison.Compare([circle], [RegionGeometry.Rectangle(0, 0, 50, 100)]);
        Near(halfCircle.Coverage, .5, .0001, "half-plane covers half of a symmetric ellipse within flattening precision");
        var elongated = RegionGeometry.Ellipse(1, 2, 200, 20);
        var elongatedResult = RegionComparison.Compare([elongated], [RegionGeometry.Rectangle(1, 2, 200, 20)]);
        Near(elongatedResult.ReferenceArea, Math.PI * 1000, 3, "unequal ellipse axes preserve original pixel scale");
        check(elongatedResult.Coverage == 1 && elongatedResult.ExcessArea > 800,
            "Comparison: bounding rectangle does not stand in for the filled ellipse");

        var upperLimit = RegionGeometry.Rectangle(1e9 - 10, 1e9 - 10, 10, 10);
        Areas(RegionComparison.Compare([upperLimit], [upperLimit]), 100, 100, 100,
            "small area at the upper coordinate limit avoids floating-point cancellation");
        var lowerLimit = RegionGeometry.Rectangle(-1e9, -1e9, 10, 10);
        Areas(RegionComparison.Compare([lowerLimit], [lowerLimit]), 100, 100, 100,
            "small area at the lower coordinate limit remains measurable");
        var wide = RegionGeometry.Rectangle(-1e9, -1e9, 2e9, 2e9);
        var wideResult = RegionComparison.Compare([wide], [target]);
        check(wideResult.ReferenceArea == 4e18 && wideResult.IntersectionArea == 100 && wideResult.Precision == 1
            && double.IsFinite(wideResult.Coverage) && wideResult.Coverage > 0,
            "Comparison: supported full coordinate range remains finite");
        var gridSquare = RegionGeometry.Rectangle(0, 0, .0001, .0001);
        Near(RegionComparison.Compare([gridSquare], [gridSquare]).ReferenceArea, 1e-8, 1e-20,
            "one grid cell is a valid positive reference");
        var degenerate = RegionGeometry.Polygon([new(0, 0), new(1, 1), new(2, 2)]);
        Areas(RegionComparison.Compare([target, degenerate], [degenerate]), 100, 0, 0,
            "zero-area components neither inflate reference area nor create a drawn answer");
        Reject(() => RegionComparison.Compare([], [target]), "empty reference is rejected");
        Reject(() => RegionComparison.Compare([degenerate], []), "zero-area reference is rejected");
        Reject(() => RegionComparison.Compare([RegionGeometry.Rectangle(0, 0, .000001, .000001)], []),
            "a reference collapsed by supported grid precision is rejected");
        Reject(() => RegionComparison.Compare(null!, []), "null reference collection is rejected");
        Reject(() => RegionComparison.Compare([target], null!), "null drawn collection is rejected");
        Reject(() => RegionComparison.Compare([null!], []), "null reference entry is rejected");
        Reject(() => RegionComparison.Compare([target], [null!]), "null drawn entry is rejected");
        Reject(() => RegionComparison.Compare([RegionGeometry.Rectangle(1e9, 0, 1, 1)], []),
            "reference extending past the coordinate range is rejected");
        Reject(() => RegionComparison.Compare([target], [RegionGeometry.Rectangle(-1e9 - 1, 0, 1, 1)]),
            "out-of-range drawn geometry cannot silently affect the result");
        Reject(() => RegionComparison.Compare([RegionGeometry.Ellipse(0, 0, 1e9, 1e9)], []),
            "a curve exceeding supported flattening complexity is rejected");
        Areas(RegionComparison.Compare(Enumerable.Repeat(target, RegionComparison.MaxRegions), [target]), 100, 100, 100,
            "a reference at its region limit retains capacity for a correct drawn answer");
        Reject(() => RegionComparison.Compare(Enumerable.Repeat(target, RegionComparison.MaxRegions + 1), [target]),
            "reference region count is bounded independently");
        Reject(() => RegionComparison.Compare([target], Enumerable.Repeat(target, RegionComparison.MaxRegions + 1)),
            "drawn region count is bounded independently");
        var excessivePoints = Enumerable.Range(0, RegionComparison.MaxVertices + 1)
            .Select(i => new AtlasPoint(i % 3, (i % 3) == 1 ? 1 : 0));
        Reject(() => RegionComparison.Compare([target], [RegionGeometry.Polygon(excessivePoints)]),
            "source point limit per side is checked before normalization");

        var inputs = new List<RegionGeometry> { island, target, circle, reversed };
        var before = inputs.Select(RegionGeometryCodec.Serialize).ToArray();
        _ = RegionComparison.Compare(inputs, inputs.AsEnumerable().Reverse());
        check(before.SequenceEqual(inputs.Select(RegionGeometryCodec.Serialize)) && inputs.Count == 4,
            "Comparison: normalization leaves all input geometries, ordering and serialized bytes unchanged");
        check(new[] { identical, inside, outside, shifted, disjoint, touching, empty, filledHole, union, ellipseResult, halfCircle, wideResult }
            .All(result => result.Coverage is >= 0 and <= 1 && (result.Precision is null or >= 0 and <= 1)
                && (result.ExcessFraction is null or >= 0 and <= 1) && result.MissedArea >= 0 && result.ExcessArea >= 0),
            "Comparison: every reported fraction is bounded and every area difference is nonnegative");
    }
}

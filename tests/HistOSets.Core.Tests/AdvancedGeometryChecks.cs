using HistOSets.Core;

internal static class AdvancedGeometryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        void Near(double actual, double expected, double tolerance, string name)
            => check(Math.Abs(actual - expected) <= tolerance, name + $" ({actual:G12})");
        void Reject(Action operation, string name)
        {
            try { operation(); } catch (Exception e) when (e is ArgumentException or FormatException) { check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        static AtlasPoint[] Box(double x, double y, double w, double h) => [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];
        var outer = Box(0, 0, 100, 100);
        var hole = Box(20, 20, 60, 60);
        var island = Box(40, 40, 20, 20);
        var second = Box(120, 0, 10, 10);
        var compound = RegionGeometry.Compound([outer, hole, island, second]);
        var json = RegionGeometryCodec.Serialize(compound);
        check(compound.Version == 2 && compound.Kind == "compound" && json.Contains("\"fillRule\":\"evenOdd\"", StringComparison.Ordinal), "Compound uses explicitly versioned even-odd JSON");
        check(RegionGeometryCodec.Deserialize(json) == compound && RegionGeometryCodec.Serialize(RegionGeometryCodec.Deserialize(json)) == json,
            "Compound round trip preserves rings, doubles and canonical JSON");
        outer[0] = new(-100, -100);
        check(compound.Rings[0][0] == new AtlasPoint(0, 0), "Compound defensively copies nested rings");
        try { ((IList<AtlasPoint>)compound.Rings[0])[0] = new(1, 1); throw new Exception("Mutable nested ring"); }
        catch (NotSupportedException) { check(true, "Compound nested rings are read-only"); }
        check(RegionGeometryOperations.Contains(compound, new(10, 10)) && !RegionGeometryOperations.Contains(compound, new(30, 30))
            && RegionGeometryOperations.Contains(compound, new(50, 50)) && RegionGeometryOperations.Contains(compound, new(125, 5))
            && !RegionGeometryOperations.Contains(compound, new(110, 5)), "Even-odd hit testing preserves holes, nested islands and disconnected parts");
        var measured = RegionMeasurements.Measure(compound);
        Near(measured.Area, 6900, 1e-8, "Area subtracts hole and adds nested and disconnected islands");
        Near(measured.Perimeter, 760, 1e-8, "Perimeter includes ALL outer and inner boundaries");
        var anisotropic = RegionMeasurements.Measure(compound, 0.5, 2);
        Near(anisotropic.Area, 6900, 1e-8, "Anisotropic compound area uses both pixel axes");
        Near(anisotropic.Perimeter, 950, 1e-8, "Anisotropic compound perimeter scales each boundary segment separately");
        var reversed = RegionGeometry.Compound(compound.Rings.Select(r => r.Reverse()));
        check(RegionMeasurements.Measure(reversed) == measured, "Ring winding is irrelevant under the even-odd fill rule");
        var overlap = RegionGeometry.Compound([Box(0, 0, 10, 10), Box(5, 0, 10, 10)]);
        Near(RegionMeasurements.Measure(overlap).Area, 100, 1e-8, "Overlapping imported rings obey even-odd fill instead of double counting");
        Near(RegionMeasurements.Measure(overlap).Perimeter, 60, 1e-8, "Overlap perimeter follows normalized filled boundary");
        var crossed = RegionGeometry.Polygon([new(0, 0), new(10, 10), new(0, 10), new(10, 0)]);
        Near(RegionMeasurements.Measure(crossed).Area, 50, 1e-8, "Self-intersecting bow-tie area measures filled lobes rather than zero signed area");
        Near(RegionMeasurements.Measure(crossed).Perimeter, 20 + 20 * Math.Sqrt(2), 1e-8, "Self-intersection perimeter follows both resolved lobes");
        check(RegionGeometryOperations.Translate(compound, 5, 7).Rings[1][0] == new AtlasPoint(25, 27), "Compound translation retains all holes and islands");
        check(RegionGeometryOperations.GetBounds(compound) == new RegionBounds(0, 0, 130, 100), "Compound bounds include all disconnected parts");
        Reject(() => RegionGeometryCodec.ToPolygon(compound), "Lossy compound-to-single-polygon conversion is rejected");
        Reject(() => RegionGeometry.Compound([]), "Empty compound is rejected: erased regions use null");
        Reject(() => RegionGeometryCodec.Deserialize(json.Replace("evenOdd", "nonZero", StringComparison.Ordinal)), "Unknown compound fill rule is rejected");
        Reject(() => RegionGeometryCodec.Deserialize(json.Replace("\"version\":2", "\"version\":3", StringComparison.Ordinal)), "Future compound version is rejected");
        Reject(() => RegionGeometryCodec.Deserialize(json.Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal)), "Compound cannot masquerade as legacy version1");

        var rectangle = RegionGeometry.Rectangle(10, 10, 80, 80);
        var unchangedJson = RegionGeometryCodec.Serialize(rectangle);
        var noPaint = RegionGeometryOperations.ApplyStroke(rectangle, [new(50, 50)], 3, 100, 100);
        var noErase = RegionGeometryOperations.ApplyStroke(rectangle, [new(1, 1)], 1, 100, 100, true);
        check(ReferenceEquals(noPaint, rectangle) && ReferenceEquals(noErase, rectangle)
            && RegionGeometryCodec.Serialize(rectangle) == unchangedJson, "No-op brush and eraser preserve original v1 object and exact bytes");
        var punched = RegionGeometryOperations.ApplyStroke(rectangle, [new(50, 50)], 10, 100, 100, true)!;
        check(punched.Kind == "compound" && punched.Rings.Count == 2 && !RegionGeometryOperations.Contains(punched, new(50, 50))
            && RegionGeometryOperations.Contains(punched, new(20, 20)), "Eraser creates a genuine hole with no filled diagonal connection");
        var split = RegionGeometryOperations.ApplyStroke(rectangle, [new(50, 0), new(50, 100)], 4, 100, 100, true)!;
        check(split.Rings.Count == 2 && RegionGeometryOperations.Contains(split, new(20, 50))
            && RegionGeometryOperations.Contains(split, new(80, 50)) && !RegionGeometryOperations.Contains(split, new(50, 50)), "Eraser can split one region into disconnected parts");
        var erased = RegionGeometryOperations.ApplyStroke(rectangle, [new(50, 50)], 100, 100, 100, true);
        check(erased is null, "Full erasure removes region instead of persisting invalid empty geometry");
        var stroke = RegionGeometryOperations.ApplyStroke(null, [new(10, 50), new(990, 50)], 3, 1000, 100)!;
        check(Enumerable.Range(10, 981).All(x => RegionGeometryOperations.Contains(stroke, new(x, 50))), "Fast 980px pointer movement paints a continuous swept stroke");
        Near(RegionMeasurements.Measure(stroke).Area, 980 * 6 + Math.PI * 9, 1, "Continuous stroke area matches capsule within 0.05px arc flattening");
        var corner = RegionGeometryOperations.ApplyStroke(null, [new(0, 0)], 20, 100, 100)!;
        check(corner.Rings.SelectMany(r => r).All(p => p.X >= 0 && p.Y >= 0 && p.X <= 100 && p.Y <= 100), "New brush geometry is clipped to original image bounds");
        var legacy = RegionGeometry.Rectangle(-10, -10, 30, 30);
        var legacyEdit = RegionGeometryOperations.ApplyStroke(legacy, [new(30, 30)], 3, 100, 100)!;
        check(RegionGeometryOperations.Contains(legacyEdit, new(-5, -5)), "Brush clips new paint without clipping legacy image overshoot");
        var islandPaint = RegionGeometryOperations.ApplyStroke(punched, [new(50, 50)], 2, 100, 100)!;
        check(islandPaint.Rings.Count == 3 && RegionGeometryOperations.Contains(islandPaint, new(50, 50))
            && !RegionGeometryOperations.Contains(islandPaint, new(55, 50)), "Brush adds an island inside an erased hole without filling the hole");
        var incremental = RegionGeometryOperations.ApplyStroke(null, [new(10, 50), new(500, 50)], 3, 1000, 100)!;
        incremental = RegionGeometryOperations.ApplyStroke(incremental, [new(500, 50), new(990, 50)], 3, 1000, 100)!;
        Near(RegionMeasurements.Measure(incremental).Area, RegionMeasurements.Measure(stroke).Area, .1, "Incremental gestures preserve gapless coverage and capsule area");
        Reject(() => RegionGeometryOperations.ApplyStroke(null, [new(double.NaN, 5)], 3, 100, 100), "Nonfinite paint coordinates are rejected");
        Reject(() => RegionGeometryOperations.ApplyStroke(null, [new(5, 5)], 0, 100, 100), "Zero brush radius is rejected");
        Reject(() => RegionGeometryOperations.ApplyStroke(null, [new(1e12, 5)], 3, 100, 100), "Coordinates beyond robust clipping range are rejected");
        Reject(() => RegionGeometryOperations.ApplyStroke(null, Enumerable.Repeat(new AtlasPoint(1, 1), 20001).ToArray(), 3, 100, 100), "Excessive gesture point count is rejected");

        var dimensions = ImageCalibration.FromDimensions(500, 200, "µm", 1000, 400);
        Near(dimensions.PixelSizeXMetres, 5e-7, 1e-20, "Calibration converts real image width to metres per original pixel");
        check(dimensions.PixelSizeXMetres == dimensions.PixelSizeYMetres && dimensions.InputUnit == "um", "Calibration canonicalizes micrometre symbol and retains physical input");
        foreach (var unit in new[] { "m", "cm", "mm", "um", "µm", "nm" })
        {
            var c = ImageCalibration.FromDimensions(100, 50, unit, 100, 100);
            Near(c.PixelSizeXMetres, ImageCalibration.UnitInMetres(unit), 1e-15, "Calibration supports " + unit);
        }
        var rectangleMetrics = RegionMeasurements.Measure(RegionGeometry.Rectangle(0, 0, 300, 200), .5, 1);
        Near(rectangleMetrics.Area, 30000, 1e-10, "Known anisotropic rectangle has exact physical area");
        Near(rectangleMetrics.Perimeter, 700, 1e-10, "Known anisotropic rectangle has exact physical perimeter");
        var circle = RegionMeasurements.Measure(RegionGeometry.Ellipse(0, 0, 20, 20));
        Near(circle.Area, Math.PI * 100, 1e-10, "Ellipse analytic area matches circle");
        Near(circle.Perimeter, Math.PI * 20, 1e-10, "Ellipse numerical perimeter matches exact circle");
        var ellipse = RegionMeasurements.Measure(RegionGeometry.Ellipse(0, 0, 10, 20), 2, 1);
        Near(ellipse.Area, circle.Area, 1e-10, "Anisotropic calibration transforms ellipse axes before area");
        Near(ellipse.Perimeter, circle.Perimeter, 1e-10, "Anisotropic calibration transforms ellipse axes before perimeter");
        var eccentric = RegionMeasurements.Measure(RegionGeometry.Ellipse(0, 0, 20, .000002));
        Near(eccentric.Perimeter, 40, 1e-9, "Highly eccentric ellipse converges to four times major semiaxis");
        Reject(() => ImageCalibration.FromDimensions(0, 1, "um", 100, 100), "Zero physical width is rejected");
        Reject(() => ImageCalibration.FromDimensions(1, 1, "inch", 100, 100), "Unsupported calibration units are rejected");
        Reject(() => new ImageCalibration(1, 1, InputWidth: 1).Validate(), "Incomplete input provenance is rejected");
        Reject(() => new ImageCalibration(double.PositiveInfinity, 1).Validate(), "Nonfinite calibration is rejected");
        Reject(() => RegionMeasurements.Measure(rectangle, double.NaN, 1), "Nonfinite measurement scale is rejected");
        Reject(() => RegionMeasurements.Measure(rectangle, double.MaxValue, 1), "Overflowed physical dimensions are rejected");
        Reject(() => RegionMeasurements.Measure(compound, 1e-200, 1e-200), "Positive compound area cannot silently underflow to zero");
        check(RegionGeometryCodec.Serialize(rectangle) == unchangedJson, "Measurement and paint previews never mutate source v1 geometry");
    }
}

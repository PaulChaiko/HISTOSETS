namespace HistOSets.Core;

/// <summary>Area is in squared selected units; perimeter includes outer and hole boundaries.</summary>
public readonly record struct RegionMeasurement(double Area, double Perimeter);

public static class RegionMeasurements
{
    /// <summary>
    /// The defaults return px² and px. Pass metres/pixel to obtain m² and m, or
    /// micrometres/pixel to obtain µm² and µm. Independent axes support anisotropic pixels.
    /// Polygon intersections are resolved by even-odd filling on a 0.0001 px grid;
    /// persisted coordinates are never modified by measuring. Ellipse area is analytic,
    /// and its perimeter is integrated numerically (adaptive Simpson, relative target 1e-12).
    /// </summary>
    public static RegionMeasurement Measure(RegionGeometry geometry, double unitPerPixelX = 1, double unitPerPixelY = 1)
    {
        RegionGeometryCodec.Validate(geometry);
        if (!double.IsFinite(unitPerPixelX) || !double.IsFinite(unitPerPixelY) || unitPerPixelX <= 0 || unitPerPixelY <= 0)
            throw new ArgumentOutOfRangeException(nameof(unitPerPixelX), "Measurement scales must be positive finite numbers.");
        double area; double perimeter;
        if (geometry.Kind is "rectangle" or "ellipse")
        {
            var width = geometry.Width * unitPerPixelX;
            var height = geometry.Height * unitPerPixelY;
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
                throw new ArgumentException("Scaled dimensions cannot be represented.");
            area = width * height;
            if (geometry.Kind == "rectangle") perimeter = 2 * (width + height);
            else
            {
                area *= Math.PI / 4;
                var a = Math.Max(width, height) / 2;
                var b = Math.Min(width, height) / 2;
                var ratio = b / a;
                double F(double t) => double.Hypot(Math.Sin(t), ratio * Math.Cos(t));
                var l = 0d; var r = Math.PI / 2; var m = (l + r) / 2;
                var whole = (r - l) / 6 * (F(l) + 4 * F(m) + F(r));
                perimeter = 4 * a * Integrate(F, l, r, F(l), F(m), F(r), whole, 1e-12, 24);
            }
            if (area <= 0) throw new ArgumentException("Scaled area cannot be represented.");
        }
        else
        {
            var normalized = RegionGeometryOperations.NormalizedPaths(geometry);
            area = 0; perimeter = 0;
            var pixelArea = 0d; var pixelPerimeter = 0d;
            foreach (var path in normalized)
            {
                if (path.Count < 3) continue;
                // Translate first to reduce catastrophic cancellation at large image origins.
                var origin = path[0]; var twiceArea = 0d; var twicePixelArea = 0d;
                for (var i = 0; i < path.Count; i++)
                {
                    var p = path[i]; var q = path[(i + 1) % path.Count];
                    var px = (p.x - origin.x) * unitPerPixelX;
                    var py = (p.y - origin.y) * unitPerPixelY;
                    var qx = (q.x - origin.x) * unitPerPixelX;
                    var qy = (q.y - origin.y) * unitPerPixelY;
                    twiceArea += px * qy - qx * py;
                    twicePixelArea += (p.x - origin.x) * (q.y - origin.y) - (q.x - origin.x) * (p.y - origin.y);
                    pixelPerimeter += double.Hypot(q.x - p.x, q.y - p.y);
                    perimeter += double.Hypot((q.x - p.x) * unitPerPixelX, (q.y - p.y) * unitPerPixelY);
                }
                area += twiceArea / 2;
                pixelArea += twicePixelArea / 2;
            }
            area = Math.Abs(area); // Clipper's normalized hole rings have opposite winding.
            if ((Math.Abs(pixelArea) > 0 && area == 0) || (pixelPerimeter > 0 && perimeter == 0))
                throw new ArgumentException("Scaled measurement cannot be represented.");
        }
        if (!double.IsFinite(area) || !double.IsFinite(perimeter))
            throw new ArgumentException("Measurement exceeds the supported numerical range.");
        return new(area, perimeter);
    }

    private static double Integrate(Func<double, double> f, double left, double right, double fl, double fm, double fr,
        double whole, double tolerance, int depth)
    {
        var middle = (left + right) / 2;
        var fml = f((left + middle) / 2); var fmr = f((middle + right) / 2);
        var first = (middle - left) / 6 * (fl + 4 * fml + fm);
        var second = (right - middle) / 6 * (fm + 4 * fmr + fr);
        var delta = first + second - whole;
        if (depth == 0 || Math.Abs(delta) <= 15 * tolerance) return first + second + delta / 15;
        return Integrate(f, left, middle, fl, fml, fm, first, tolerance / 2, depth - 1)
            + Integrate(f, middle, right, fm, fmr, fr, second, tolerance / 2, depth - 1);
    }
}

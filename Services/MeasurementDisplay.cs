using System.Globalization;

namespace HistOSets;

internal static class MeasurementDisplay
{
    internal static string Area(double area, CultureInfo culture)
    {
        // Finite doubles at or above 2^52 already have no fractional part.
        if (area == 0 || area >= 4_503_599_627_370_496d) return area.ToString("N2", culture);
        // The decimal conversion removes floating-point noise introduced by calibration
        // before ceiling on the decimal hundredth (e.g. 0.07 should not become 0.08).
        var rounded = Math.Max(0.01m, decimal.Ceiling((decimal)area * 100) / 100);
        return rounded.ToString("N2", culture);
    }
}

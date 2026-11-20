using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HistOSets.Core;

namespace HistOSets.Controls;

/// <summary>Both drawing views adjust the same source-pixel radius using the same controls.</summary>
internal static class RadiusRepeatButtons
{
    internal const double Minimum = 0.01;
    internal const double Maximum = RegionGeometryOperations.MaxCoordinate;

    internal static void Configure(RepeatButton decrease, RepeatButton increase, Func<double> getValue, Action<double> setValue)
    {
        void Bind(RepeatButton button, bool increasing)
        {
            button.ClickMode = ClickMode.Press;
            button.Delay = 350;
            button.Interval = 75;
            button.Click += (_, _) =>
            {
                var current = getValue();
                // Relative steps let the same control handle subpixel shapes and broad strokes.
                var step = Math.Max(Minimum, Math.Round(current * 0.1, 2, MidpointRounding.AwayFromZero));
                setValue(Math.Clamp(Math.Round(current + (increasing ? step : -step), 2), Minimum, Maximum));
            };
        }
        Bind(decrease, false);
        Bind(increase, true);
    }

    internal static void Localize(RepeatButton decrease, RepeatButton increase, Func<string, string, string> translate)
    {
        var down = translate("Уменьшить радиус кисти и ластика", "Decrease brush and eraser radius");
        var up = translate("Увеличить радиус кисти и ластика", "Increase brush and eraser radius");
        var hold = translate("Удерживайте для повторения.", "Hold to repeat.");
        decrease.ToolTip = down + ". " + hold;
        increase.ToolTip = up + ". " + hold;
        AutomationProperties.SetName(decrease, down);
        AutomationProperties.SetName(increase, up);
    }
}

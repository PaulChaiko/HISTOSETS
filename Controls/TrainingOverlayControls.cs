using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace HistOSets.Controls;

/// <summary>Display-only controls for one self-test outline layer.</summary>
internal sealed class TrainingOverlayControls : StackPanel
{
    public CheckBox Toggle { get; } = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    public Slider Hue { get; }

    private readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
    private readonly Border swatch = new() { Width = 12, Height = 12, Margin = new Thickness(4, 0, 0, 0) };

    public TrainingOverlayControls(double hue, byte brightness, byte fillAlpha, Action<bool> setVisible,
        Action<Brush, Brush> setBrushes)
    {
        Orientation = Orientation.Horizontal;
        Margin = new Thickness(0, 0, 14, 3);
        Hue = new() { Minimum = 0, Maximum = 360, Value = hue, Width = 65, VerticalAlignment = VerticalAlignment.Center };
        Children.Add(Toggle);
        Children.Add(label);
        Children.Add(Hue);
        Children.Add(swatch);
        Toggle.Checked += (_, _) => setVisible(true);
        Toggle.Unchecked += (_, _) => setVisible(false);
        Hue.ValueChanged += (_, _) => UpdateBrushes();
        UpdateBrushes();

        void UpdateBrushes()
        {
            var angle = Hue.Value % 360;
            var x = 1 - Math.Abs(angle / 60 % 2 - 1);
            var (r, g, b) = angle switch
            {
                < 60 => (1d, x, 0d), < 120 => (x, 1d, 0d), < 180 => (0d, 1d, x),
                < 240 => (0d, x, 1d), < 300 => (x, 0d, 1d), _ => (1d, 0d, x)
            };
            var color = Color.FromRgb((byte)Math.Round(r * brightness), (byte)Math.Round(g * brightness), (byte)Math.Round(b * brightness));
            var stroke = MakeBrush(color);
            swatch.Background = stroke;
            color.A = fillAlpha;
            setBrushes(MakeBrush(color), stroke);
        }
    }

    public void SetLabels(string title, string hueLabel, string accessibleHueLabel)
    {
        Toggle.Content = title;
        label.Text = hueLabel;
        Hue.ToolTip = accessibleHueLabel;
        AutomationProperties.SetName(Hue, accessibleHueLabel);
    }

    private static Brush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

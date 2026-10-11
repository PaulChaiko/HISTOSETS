using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using HistOSets.Core;
using HistOSets.Services;

namespace HistOSets.Controls;

/// <summary>A learner-facing image view, with no catalog labels and no editing commands.</summary>
public sealed class TrainingImageView : UserControl
{
    private readonly Button zoomOut = new() { Content = "−" };
    private readonly Button zoomIn = new() { Content = "+" };
    private readonly Button fit = new();
    private readonly Button actualSize = new() { Content = "1:1" };
    private bool subscribed;
    private string? locale;

    public RegionEditorSurface Surface { get; } = new()
    {
        CanEdit = false,
        Tool = RegionEditorTool.Pan,
        MinHeight = 100,
        MinWidth = 100
    };

    public TrainingImageView()
    {
        Focusable = false;
        var root = new DockPanel();
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        foreach (var button in new[] { zoomOut, zoomIn, fit, actualSize })
        {
            button.Padding = new Thickness(8, 3, 8, 3);
            button.Margin = new Thickness(0, 0, 5, 0);
            button.MinWidth = 32;
            toolbar.Children.Add(button);
        }
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(98, 82, 125)),
            BorderThickness = new Thickness(1),
            Child = Surface
        });
        Content = root;
        zoomOut.Click += (_, _) => Surface.ZoomBy(0.8);
        zoomIn.Click += (_, _) => Surface.ZoomBy(1.25);
        fit.Click += (_, _) => Surface.FitToView();
        actualSize.Click += (_, _) => Surface.ActualSize();
        Loaded += (_, _) =>
        {
            if (!subscribed) { UiLanguage.Changed += LanguageChanged; subscribed = true; }
            ApplyLanguage();
        };
        Unloaded += (_, _) =>
        {
            if (subscribed) { UiLanguage.Changed -= LanguageChanged; subscribed = false; }
        };
        ApplyLanguage();
        SetControlsEnabled(false);
    }

    public void ShowImage(LoadedImage image, IEnumerable<TrainingRegion>? regions = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        Surface.CanEdit = false;
        Surface.Tool = RegionEditorTool.Pan;
        Surface.SelectedRegionId = null;
        Surface.Regions = (regions ?? []).Select(region => new AtlasRegion(region.Id, region.Geometry)).ToArray();
        Surface.ImageWidth = image.PixelWidth;
        Surface.ImageHeight = image.PixelHeight;
        Surface.ImageSource = image.Bitmap;
        SetControlsEnabled(true);
    }

    public void Clear()
    {
        Surface.CanEdit = false;
        Surface.Tool = RegionEditorTool.Pan;
        Surface.Regions = [];
        Surface.SelectedRegionId = null;
        Surface.ImageSource = null;
        Surface.ImageWidth = 0;
        Surface.ImageHeight = 0;
        SetControlsEnabled(false);
    }

    /// <summary>Keep the toolbar in the prepared session's language for its whole lifetime.</summary>
    public void SetLocale(string value)
    {
        if (value is not ("ru" or "en")) throw new ArgumentOutOfRangeException(nameof(value));
        locale = value;
        ApplyLanguage();
    }

    private void LanguageChanged(object? sender, EventArgs e) => ApplyLanguage();

    private void ApplyLanguage()
    {
        fit.Content = T("Вписать", "Fit");
        SetLabel(zoomOut, T("Уменьшить", "Zoom out"));
        SetLabel(zoomIn, T("Увеличить", "Zoom in"));
        SetLabel(fit, T("Вписать изображение в окно", "Fit image to view"));
        SetLabel(actualSize, T("Один пиксель изображения на единицу экрана", "One image pixel per screen unit"));
        AutomationProperties.SetName(Surface, T("Изображение задания", "Question image"));
    }

    private string T(string russian, string english) => (locale ?? UiLanguage.Locale) == "en" ? english : russian;

    private static void SetLabel(Button button, string label)
    {
        button.ToolTip = label;
        AutomationProperties.SetName(button, label);
    }

    private void SetControlsEnabled(bool enabled)
    {
        zoomOut.IsEnabled = zoomIn.IsEnabled = fit.IsEnabled = actualSize.IsEnabled = enabled;
    }
}

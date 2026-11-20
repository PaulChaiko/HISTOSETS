using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HistOSets.Core;
using HistOSets.Services;

namespace HistOSets.Controls;

/// <summary>
/// An in-memory learner canvas. Reference regions belong to a separate display-only
/// layer and are supplied only after submission. No operation writes to the catalog.
/// </summary>
public sealed class TrainingDrawingView : UserControl
{
    private const int HistoryLimit = 80;
    private sealed record DrawingState(IReadOnlyList<AtlasRegion> Regions, Guid? SelectedId);
    private readonly List<DrawingState> undo = [];
    private readonly List<DrawingState> redo = [];
    private readonly string toolGroupName = "DrawingTools" + Guid.NewGuid().ToString("N");
    private IReadOnlyList<AtlasRegion> regions = Array.AsReadOnly(Array.Empty<AtlasRegion>());
    private readonly Dictionary<RegionEditorTool, RadioButton> toolButtons = [];
    private readonly WrapPanel tools = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly WrapPanel commands = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly StackPanel radiusPanel = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel legend = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBlock radiusLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock help = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5), FontSize = 12 };
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5), FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromRgb(135, 38, 44)), Visibility = Visibility.Collapsed
    };
    private readonly TextBox radiusBox = new() { Width = 64, Text = "16", Margin = new Thickness(5, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly RepeatButton radiusDecrease = new() { Content = "−", MinWidth = 30, Margin = new Thickness(5, 0, 0, 3) };
    private readonly RepeatButton radiusIncrease = new() { Content = "+", MinWidth = 30, Margin = new Thickness(0, 0, 4, 3) };
    private readonly Button undoButton = new(), redoButton = new(), deleteButton = new(), clearButton = new();
    private readonly Button finishButton = new(), cancelButton = new(), newBrushButton = new();
    private readonly Button zoomOut = new() { Content = "−" }, zoomIn = new() { Content = "+" }, fitButton = new();
    private readonly Button actualSizeButton = new() { Content = "1:1" };
    private readonly TrainingOverlayControls learnerOverlay;
    private readonly TrainingOverlayControls referenceOverlay;
    private bool hasImage, readOnly, comparison, changing, subscribed;
    private string? locale;

    public RegionEditorSurface Surface { get; } = new() { MinWidth = 100, MinHeight = 100, CanEdit = false };
    public IReadOnlyList<AtlasRegion> Regions => regions;
    public bool HasPendingOperation => Surface.HasPendingOperation;
    public bool CanSubmit => hasImage && !readOnly && regions.Count > 0 && !HasPendingOperation;
    public bool IsReadOnly => readOnly;
    public event EventHandler? DrawingChanged;

    public TrainingDrawingView()
    {
        Focusable = false;
        NameScope.SetNameScope(this, new NameScope());
        learnerOverlay = new(204, 188, 42, visible => Surface.ShowRegions = visible, (fill, stroke) =>
        {
            Surface.SelectedFillBrush = Surface.RegionFillBrush = fill;
            Surface.SelectedStrokeBrush = Surface.RegionStrokeBrush = stroke;
        });
        referenceOverlay = new(31, 218, 35, visible => Surface.ShowReferenceRegions = visible, (fill, stroke) =>
        {
            Surface.ReferenceFillBrush = fill;
            Surface.ReferenceStrokeBrush = stroke;
        });
        var root = new DockPanel();
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        top.Children.Add(tools);
        top.Children.Add(commands);
        var navigation = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(navigation);
        top.Children.Add(help);
        top.Children.Add(legend);
        top.Children.Add(status);
        root.Children.Add(new Border
        {
            BorderBrush = Brush(Color.FromRgb(98, 82, 125)), BorderThickness = new Thickness(1), Child = Surface
        });
        Content = root;

        foreach (var tool in Enum.GetValues<RegionEditorTool>())
        {
            var button = new RadioButton { GroupName = toolGroupName, Margin = new Thickness(0, 0, 9, 3), VerticalContentAlignment = VerticalAlignment.Center };
            Register(button, "Drawing" + tool + "Button");
            button.Checked += (_, _) =>
            {
                if (changing) return;
                if (!hasImage || readOnly || HasPendingOperation) { UpdateState(); return; }
                Surface.Tool = tool;
                ClearStatus();
                UpdateState();
                Surface.Focus();
            };
            toolButtons.Add(tool, button);
            tools.Children.Add(button);
        }
        AddButton(commands, undoButton, "DrawingUndoButton", () => Undo());
        AddButton(commands, redoButton, "DrawingRedoButton", () => Redo());
        AddButton(commands, deleteButton, "DrawingDeleteButton", () => DeleteSelected());
        AddButton(commands, clearButton, "DrawingClearButton", () => ClearDrawing());
        AddButton(commands, newBrushButton, "DrawingNewBrushRegionButton", () =>
        {
            if (hasImage && !readOnly && !HasPendingOperation) { Surface.BeginNewBrushRegion(); ClearStatus(); UpdateState(); }
        });
        AddButton(commands, finishButton, "DrawingFinishPolygonButton", () => TryFinishPendingDrawing());
        AddButton(commands, cancelButton, "DrawingCancelButton", () => CancelPendingDrawing());
        AddButton(navigation, zoomOut, "DrawingZoomOutButton", () => Surface.ZoomBy(0.8));
        AddButton(navigation, zoomIn, "DrawingZoomInButton", () => Surface.ZoomBy(1.25));
        AddButton(navigation, fitButton, "DrawingFitButton", () => Surface.FitToView());
        AddButton(navigation, actualSizeButton, "DrawingActualSizeButton", () => Surface.ActualSize());
        radiusLabel.Margin = new Thickness(0, 0, 3, 3);
        navigation.Children.Add(radiusLabel);
        Register(radiusDecrease, "DrawingDecreaseRadiusButton");
        radiusPanel.Children.Add(radiusDecrease);
        Register(radiusBox, "DrawingBrushRadiusBox");
        radiusPanel.Children.Add(radiusBox);
        Register(radiusIncrease, "DrawingIncreaseRadiusButton");
        radiusPanel.Children.Add(radiusIncrease);
        radiusPanel.Children.Add(new TextBlock { Text = "px", VerticalAlignment = VerticalAlignment.Center });
        navigation.Children.Add(radiusPanel);
        RadiusRepeatButtons.Configure(radiusDecrease, radiusIncrease, () => Surface.BrushRadius, SetRadius);
        Register(learnerOverlay.Toggle, "DrawingLearnerOverlayCheckBox");
        Register(referenceOverlay.Toggle, "DrawingReferenceOverlayCheckBox");
        Register(learnerOverlay.Hue, "DrawingLearnerHueSlider");
        Register(referenceOverlay.Hue, "DrawingReferenceHueSlider");
        legend.Children.Add(learnerOverlay);
        legend.Children.Add(referenceOverlay);
        radiusBox.LostKeyboardFocus += (_, _) => ApplyRadius();
        Surface.RegionsChanged += (_, args) =>
        {
            if (changing || readOnly) return;
            PushUndo();
            regions = Snapshot(args.Regions);
            redo.Clear();
            ClearStatus();
            NotifyChanged();
        };
        Surface.SelectionChanged += (_, _) => UpdateState();
        Surface.PendingOperationChanged += (_, _) => { if (!changing) NotifyChanged(); };
        Surface.OperationFailed += (_, _) => SetStatus(
            "Не удалось применить мазок. Исходная область сохранена; попробуйте более короткий мазок или меньший радиус.",
            "The stroke could not be applied. The original region is preserved; try a shorter stroke or a smaller radius.");
        PreviewKeyDown += HandleKeys;
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
        UpdateState();
    }

    /// <summary>Start a fresh response; no reference geometry is accepted by this method.</summary>
    public void ShowImage(LoadedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Reset(image);
        readOnly = false;
        Surface.CanEdit = true;
        Surface.Tool = RegionEditorTool.Polygon;
        NotifyChanged();
    }

    public IReadOnlyList<RegionGeometry> GetGeometries() => Array.AsReadOnly(regions.Select(region => region.Geometry).ToArray());

    /// <summary>Compare immutable learner geometry and a separate, unselectable reference layer.</summary>
    public void ShowComparison(LoadedImage image, IEnumerable<RegionGeometry> drawn, IEnumerable<TrainingRegion> reference)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(drawn);
        ArgumentNullException.ThrowIfNull(reference);
        // Materialize both before clearing the old view, so caller snapshots remain independent.
        var learnerRegions = Snapshot(drawn.Select(geometry => new AtlasRegion(Guid.NewGuid(), geometry)));
        var referenceRegions = reference.Select(region => new AtlasRegion(region.Id, region.Geometry)).ToArray();
        Reset(image);
        changing = true;
        try
        {
            regions = learnerRegions;
            Surface.Regions = regions;
            Surface.ReferenceRegions = referenceRegions;
            Surface.SelectedRegionId = null;
            Surface.Tool = RegionEditorTool.Pan;
            comparison = readOnly = true;
            Surface.SetEditingLock(true);
        }
        finally { changing = false; }
        NotifyChanged();
    }

    /// <summary>Temporarily disable drawing. A displayed comparison cannot be unlocked.</summary>
    public void SetReadOnly(bool value = true)
    {
        if (comparison && !value) return;
        readOnly = value || !hasImage;
        Surface.SetEditingLock(readOnly);
        Surface.CanEdit = !readOnly;
        if (readOnly) { Surface.SelectedRegionId = null; Surface.Tool = RegionEditorTool.Pan; }
        NotifyChanged();
    }

    public void Clear()
    {
        Reset(null);
        NotifyChanged();
    }

    public void SetLocale(string value)
    {
        if (value is not ("ru" or "en")) throw new ArgumentOutOfRangeException(nameof(value));
        locale = value;
        ApplyLanguage();
    }

    /// <summary>Never silently discard unfinished geometry when accepting a response.</summary>
    public bool TryFinishPendingDrawing()
    {
        if (!HasPendingOperation) return true;
        if (!readOnly && Surface.HasPendingPolygon && Surface.FinishPolygon())
        {
            ClearStatus();
            NotifyChanged();
            return true;
        }
        SetStatus("Завершите полигон: нужны как минимум три точки, образующие область. Или отмените незавершённое действие.",
            "Finish the polygon: at least three points enclosing an area are required. Or cancel the unfinished action.");
        return false;
    }

    public bool Undo()
    {
        if (!hasImage || readOnly) return false;
        if (HasPendingOperation) { CancelPendingDrawing(); return true; }
        if (undo.Count == 0) return false;
        redo.Add(CurrentState());
        var previous = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        Restore(previous);
        return true;
    }

    public bool Redo()
    {
        if (!hasImage || readOnly || HasPendingOperation || redo.Count == 0) return false;
        undo.Add(CurrentState());
        var next = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        Restore(next);
        return true;
    }

    public bool DeleteSelected()
    {
        if (!hasImage || readOnly || HasPendingOperation) return false;
        return Surface.DeleteSelected();
    }

    public bool ClearDrawing()
    {
        if (!hasImage || readOnly || (regions.Count == 0 && !HasPendingOperation)) return false;
        Surface.CancelPendingOperation();
        if (regions.Count == 0) { ClearStatus(); NotifyChanged(); return true; }
        PushUndo();
        redo.Clear();
        Restore(new DrawingState(Snapshot([]), null));
        return true;
    }

    public void CancelPendingDrawing()
    {
        if (readOnly) return;
        Surface.CancelPendingOperation();
        ClearStatus();
        NotifyChanged();
    }

    private void Reset(LoadedImage? image)
    {
        changing = true;
        try
        {
            hasImage = image is not null;
            readOnly = true;
            comparison = false;
            Surface.SetEditingLock(false);
            Surface.CanEdit = false;
            Surface.Tool = RegionEditorTool.Pan;
            Surface.Regions = regions = Snapshot([]);
            Surface.ReferenceRegions = [];
            Surface.SelectedRegionId = null;
            Surface.ShowRegions = Surface.ShowReferenceRegions = true;
            learnerOverlay.Toggle.IsChecked = referenceOverlay.Toggle.IsChecked = true;
            Surface.ImageWidth = image?.PixelWidth ?? 0;
            Surface.ImageHeight = image?.PixelHeight ?? 0;
            Surface.ImageSource = image?.Bitmap;
            Surface.FitToView();
            Surface.BrushRadius = 16;
            radiusBox.Text = "16";
            undo.Clear();
            redo.Clear();
            ClearStatus();
        }
        finally { changing = false; }
    }

    private void PushUndo()
    {
        undo.Add(CurrentState());
        if (undo.Count > HistoryLimit) undo.RemoveAt(0);
    }

    private DrawingState CurrentState() => new(regions,
        regions.Any(region => region.Id == Surface.SelectedRegionId) ? Surface.SelectedRegionId : null);

    private void Restore(DrawingState state)
    {
        changing = true;
        try
        {
            regions = state.Regions;
            Surface.Regions = regions;
            Surface.SelectedRegionId = state.SelectedId;
        }
        finally { changing = false; }
        ClearStatus();
        NotifyChanged();
    }

    private CultureInfo DisplayCulture => CultureInfo.GetCultureInfo((locale ?? UiLanguage.Locale) == "en" ? "en-US" : "ru-RU");

    private void SetRadius(double value)
    {
        Surface.BrushRadius = value;
        radiusBox.Text = value.ToString("G", DisplayCulture);
        ClearStatus();
    }

    private void ApplyRadius()
    {
        if (!hasImage || readOnly || HasPendingOperation) return;
        var culture = DisplayCulture;
        var parsed = double.TryParse(radiusBox.Text, NumberStyles.Float, culture, out var value)
            || double.TryParse(radiusBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        if (parsed && double.IsFinite(value) && value >= RadiusRepeatButtons.Minimum && value <= RadiusRepeatButtons.Maximum)
            SetRadius(value);
        else
            SetStatus("Радиус должен быть положительным числом от 0,01 до 1 000 000 000 пикселей исходного изображения.",
                "The radius must be a positive number from 0.01 to 1,000,000,000 original image pixels.");
        radiusBox.Text = Surface.BrushRadius.ToString("G", culture);
    }

    private void HandleKeys(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
        {
            if (e.Key == Key.Enter) { ApplyRadius(); Surface.Focus(); e.Handled = true; }
            return;
        }
        if (readOnly || !hasImage) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (e.Key == Key.Enter && HasPendingOperation) { TryFinishPendingDrawing(); e.Handled = true; }
        else if (e.Key == Key.Escape && HasPendingOperation) { CancelPendingDrawing(); e.Handled = true; }
        else if (e.Key == Key.Delete && !HasPendingOperation) { DeleteSelected(); e.Handled = true; }
    }

    private void UpdateState()
    {
        if (changing) return;
        changing = true;
        try
        {
            var editable = hasImage && !readOnly;
            foreach (var (tool, button) in toolButtons)
            {
                button.IsChecked = Surface.Tool == tool;
                button.IsEnabled = editable && !HasPendingOperation;
            }
            tools.Visibility = commands.Visibility = radiusLabel.Visibility = radiusPanel.Visibility = comparison ? Visibility.Collapsed : Visibility.Visible;
            help.Visibility = comparison ? Visibility.Collapsed : Visibility.Visible;
            legend.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
            learnerOverlay.Visibility = comparison && regions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            referenceOverlay.Visibility = comparison ? Visibility.Visible : Visibility.Collapsed;
            undoButton.IsEnabled = editable && (undo.Count > 0 || HasPendingOperation);
            redoButton.IsEnabled = editable && !HasPendingOperation && redo.Count > 0;
            deleteButton.IsEnabled = editable && !HasPendingOperation && Surface.SelectedRegionId is not null;
            clearButton.IsEnabled = editable && (regions.Count > 0 || HasPendingOperation);
            newBrushButton.IsEnabled = editable && !HasPendingOperation;
            finishButton.IsEnabled = editable && Surface.HasPendingPolygon;
            cancelButton.IsEnabled = editable && HasPendingOperation;
            radiusDecrease.IsEnabled = radiusIncrease.IsEnabled = radiusBox.IsEnabled = editable && !HasPendingOperation;
            zoomOut.IsEnabled = zoomIn.IsEnabled = fitButton.IsEnabled = actualSizeButton.IsEnabled = hasImage && !HasPendingOperation;
        }
        finally { changing = false; }
    }

    private void NotifyChanged()
    {
        UpdateState();
        DrawingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyLanguage()
    {
        foreach (var (tool, button) in toolButtons)
        {
            button.Content = tool switch
            {
                RegionEditorTool.Pan => T("Панорама", "Pan"),
                RegionEditorTool.Select => T("Выбор", "Select"),
                RegionEditorTool.Rectangle => T("Прямоугольник", "Rectangle"),
                RegionEditorTool.Ellipse => T("Эллипс", "Ellipse"),
                RegionEditorTool.Polygon => T("Полигон", "Polygon"),
                RegionEditorTool.Brush => T("Кисть", "Brush"),
                RegionEditorTool.Eraser => T("Ластик", "Eraser"),
                _ => string.Empty
            };
            AutomationProperties.SetName(button, button.Content.ToString()!);
        }
        Label(undoButton, T("Отменить", "Undo"), "Ctrl+Z");
        Label(redoButton, T("Повторить", "Redo"), "Ctrl+Y");
        Label(deleteButton, T("Удалить область", "Delete region"), "Delete");
        Label(clearButton, T("Очистить рисунок", "Clear drawing"));
        Label(newBrushButton, T("Новая область кистью", "New brush region"));
        Label(finishButton, T("Завершить полигон", "Finish polygon"), "Enter");
        Label(cancelButton, T("Отменить действие", "Cancel action"), "Esc");
        Label(fitButton, T("Вписать", "Fit"));
        AutomationProperties.SetName(zoomOut, T("Уменьшить", "Zoom out"));
        AutomationProperties.SetName(zoomIn, T("Увеличить", "Zoom in"));
        AutomationProperties.SetName(actualSizeButton, T("Один пиксель изображения на единицу экрана", "One image pixel per screen unit"));
        radiusLabel.Text = T("Радиус кисти / ластика:", "Brush / eraser radius:");
        AutomationProperties.SetName(radiusBox, radiusLabel.Text);
        RadiusRepeatButtons.Localize(radiusDecrease, radiusIncrease, T);
        help.Text = T("Нарисуйте все области элемента. Полигон: щелчки, Enter — завершить; Esc — отменить. Колесо — масштаб; правая кнопка мыши — перемещение.",
            "Draw all regions of the element. Polygon: click vertices; Enter to finish, Esc to cancel. Wheel: zoom; right drag: pan.");
        var hue = T("Оттенок", "Hue");
        learnerOverlay.SetLabels(T("Ответ (сплошная линия)", "Drawing (solid line)"), hue, T("Оттенок ответа", "Drawing hue"));
        referenceOverlay.SetLabels(T("Эталон (пунктир)", "Reference (dashed line)"), hue, T("Оттенок эталона", "Reference hue"));
        AutomationProperties.SetName(Surface, T("Области ответа", "Answer regions"));
    }

    private void LanguageChanged(object? sender, EventArgs e) => ApplyLanguage();
    private string T(string russian, string english) => (locale ?? UiLanguage.Locale) == "en" ? english : russian;
    private void SetStatus(string russian, string english) { status.Text = T(russian, english); status.Visibility = Visibility.Visible; }
    private void ClearStatus() { status.Text = string.Empty; status.Visibility = Visibility.Collapsed; }
    private static IReadOnlyList<AtlasRegion> Snapshot(IEnumerable<AtlasRegion> source) => Array.AsReadOnly(source.ToArray());
    private static Brush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    private void Register(FrameworkElement element, string name)
    {
        element.Name = name;
        RegisterName(name, element);
    }

    private void AddButton(Panel parent, Button button, string name, Action action)
    {
        Register(button, name);
        button.Padding = new Thickness(6, 3, 6, 3);
        button.Margin = new Thickness(0, 0, 5, 3);
        button.FontSize = 12;
        button.Click += (_, _) => action();
        parent.Children.Add(button);
    }

    private static void Label(Button button, string label, string? shortcut = null)
    {
        button.Content = label;
        button.ToolTip = shortcut is null ? label : label + " (" + shortcut + ")";
        AutomationProperties.SetName(button, label);
    }
}

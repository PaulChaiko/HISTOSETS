using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;
using Microsoft.Data.Sqlite;
using Path = System.IO.Path;

namespace HistOSets;

public partial class MainWindow : HistOSets.Controls.WorkAreaWindow
{
    private readonly bool ownsStore;
    private readonly CatalogStore store;
    private AtlasCatalog? catalog;
    private CatalogTaxonomy taxonomy = CatalogTaxonomy.Empty;
    private readonly HashSet<Guid> selectedTags = [];
    private LoadedImage? image;
    private LoadedImage? cachedImage;
    private string? cachedPath;
    private string? displayedPath;
    private bool updating;
    private bool closed;
    private Exception? catalogError;
    private bool fitImage;
    private Point? panStart;
    private Point panOffset;
    private readonly SemaphoreSlim imageGate = new(1, 1);
    private CancellationTokenSource? imageRequest;
    private AtlasSpecimen? CurrentSpecimen => ListOfSpecimens.SelectedItem as AtlasSpecimen;
    private AtlasElement? CurrentElement => ListOfElements.SelectedItem as AtlasElement;
    private string Locale => UiLanguage.Locale;
    private static string T(string ru, string en) => UiLanguage.T(ru, en);

    public MainWindow(CatalogStore store, bool ownsStore = false)
    {
        this.store = store;
        this.ownsStore = ownsStore;
        InitializeComponent();
        UiLanguage.Changed += LanguageChanged;
        Closed += (_, _) => { closed = true; UiLanguage.Changed -= LanguageChanged; imageRequest?.Cancel(); if (this.ownsStore) store?.Dispose(); };
        Localize();
        LoadCatalog();
    }

    private void Localize()
    {
        updating = true;
        Title = T("HISTOSETS — просмотр", "HISTOSETS — browse");
        HomeButton.Content = T("На главную", "Home");
        ReloadButton.Content = T("Обновить", "Reload");
        WarningsButton.Content = T("Замечания", "Data issues");
        SearchLabel.Text = T("Поиск по названиям объектов и аналогов", "Search object and analog group names");
        SearchBox.ToolTip = T("Поиск на всех языках. Несколько слов; регистр и е/ё не различаются.", "Search all languages. Multiple words; case and е/ё are ignored.");
        LanguageLabel.Text = T("Язык", "Language");
        LanguageLabel.ToolTip = T("Язык интерфейса и данных", "Interface and content language");
        LanguageSelector.SelectedIndex = Locale == "en" ? 1 : 0;
        UpdateTagFilterButton();
        ResetFiltersButton.Content = T("Сбросить фильтры", "Reset filters");
        MaterialsLabel.Text = T("Объекты", "Objects");
        ElementsLabel.Text = T("Элементы объекта", "Object elements");
        MeasurementsLabel.Text = T("Измерения областей", "Region measurements");
        SInfo.Content = T("Об объекте", "About object");
        MaterialTagsButton.Content = T("Метаданные", "Metadata");
        EInfo.Content = T("Об элементе", "About element");
        FitButton.Content = T("Вписать", "Fit");
        ZoomInButton.ToolTip = T("Приблизить", "Zoom in");
        ZoomOutButton.ToolTip = T("Отдалить", "Zoom out");
        ActualSizeButton.ToolTip = T("Исходный размер: пиксель изображения равен одной единице WPF", "Original size: one image pixel per WPF display unit");
        OutlineToggle.Content = T("Области", "Regions");
        OverviewToggle.Content = T("Миникарта", "Overview");
        OverviewToggle.ToolTip = T("Показывать миникарту при увеличении", "Show the overview when zoomed in");
        OpacityLabel.Text = T("Видимость", "Opacity");
        HueLabel.Text = T("Оттенок", "Hue");
        updating = false;
    }

    private void LanguageChanged(object? sender, EventArgs e)
    {
        if (closed) return;
        var material = CurrentSpecimen?.MaterialId;
        var element = CurrentElement?.Id;
        Localize();
        if (catalog is not null) ApplyFilters(material, element);
        else
        {
            ResetSpecimenView();
            if (catalogError is not null) ShowCatalogError(catalogError);
        }
    }
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || updating) return;
        try { UiLanguage.Set((LanguageSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "ru"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            Localize();
            StatusText.Text = T("Не удалось сохранить язык: ", "The language preference could not be saved: ") + UiLanguage.Error(ex);
        }
    }

    private void LoadCatalog()
    {
        var selected = CurrentSpecimen?.MaterialId;
        var element = CurrentElement?.Id;
        try
        {
            catalog = store.Load();
            catalogError = null;
            taxonomy = store.LoadTaxonomy();
            cachedPath = null;
            cachedImage = null;
            selectedTags.IntersectWith(taxonomy.Tags.Select(t => t.Id));
            WarningsButton.Visibility = catalog.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilters(selected, element);
        }
        catch (Exception ex) when (ex is CatalogStorageException or SqliteException or IOException or InvalidDataException or FileFormatException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ErrorLog.Write(ex);
            catalogError = ex;
            catalog = null;
            taxonomy = CatalogTaxonomy.Empty;
            updating = true;
            ListOfSpecimens.ItemsSource = null;
            updating = false;
            ResetSpecimenView();
            selectedTags.Clear();
            UpdateTagFilterButton();
            WarningsButton.Visibility = Visibility.Collapsed;
            ShowCatalogError(ex);
        }
    }

    private void ShowCatalogError(Exception ex)
    {
        CatalogCount.Text = T("Каталог недоступен", "Catalog unavailable");
        WelcomeLogo.Visibility = Visibility.Collapsed;
        ImageMessage.Text = T("Не удалось загрузить каталог", "The catalog could not be loaded");
        ImageMessage.Visibility = Visibility.Visible;
        StatusText.Text = T("Откройте другой каталог или восстановите резервную копию.\n", "Open another catalog or restore a backup.\n") + UiLanguage.Error(ex);
    }

    private void ApplyFilters(Guid? material = null, Guid? element = null)
    {
        if (catalog is null || updating) return;
        material ??= CurrentSpecimen?.MaterialId;
        element ??= CurrentElement?.Id;
        var visible = CatalogBrowser.Filter(catalog, Locale, SearchBox.Text, selectedTags, taxonomy);
        updating = true;
        ListOfSpecimens.ItemsSource = visible;
        ListOfSpecimens.SelectedItem = material is null ? null : visible.FirstOrDefault(s => s.MaterialId == material);
        updating = false;
        var total = catalog.Specimens.Select((s, i) => s.MaterialId?.ToString() ?? "legacy:" + i).Distinct().Count();
        CatalogCount.Text = T($"Объектов: {visible.Count} / {total}", $"Objects: {visible.Count} / {total}");
        UpdateTagFilterButton();
        ShowMaterial(element);
        if (visible.Count == 0)
        {
            WelcomeLogo.Visibility = Visibility.Collapsed;
            ImageMessage.Text = total == 0 ? T("Каталог пока пуст", "The catalog is empty") : T("Объекты не найдены.\nИзмените запрос или сбросьте фильтры.", "No matching objects.\nChange your search or reset filters.");
            ImageMessage.Visibility = Visibility.Visible;
            StatusText.Text = total == 0 ? T("В этом каталоге пока нет изображений объектов.", "This catalog has no object images yet.") : T("Нет объектов, соответствующих поиску и тегам.", "No objects match the search and tags.");
        }
    }

    private void UpdateTagFilterButton()
    {
        TagFiltersButton.IsEnabled = taxonomy.Tags.Count != 0 && catalog is not null;
        TagFiltersButton.Content = selectedTags.Count == 0
            ? T("Фильтры по тегам…", "Tag filters…")
            : T($"Фильтры по тегам ({selectedTags.Count})…", $"Tag filters ({selectedTags.Count})…");
    }

    private void TagFilters_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is null || taxonomy.Tags.Count == 0) return;
        var dialog = new TagFiltersWindow(taxonomy, selectedTags, Locale) { Owner = this };
        if (dialog.ShowDialog() != true || selectedTags.SetEquals(dialog.SelectedTags)) return;
        selectedTags.Clear();
        selectedTags.UnionWith(dialog.SelectedTags);
        ApplyFilters();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized) ApplyFilters(); }
    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        updating = true;
        SearchBox.Clear();
        selectedTags.Clear();
        updating = false;
        ApplyFilters();
    }

    private void ResetSpecimenView()
    {
        imageRequest?.Cancel();
        ListOfElements.ItemsSource = null;
        ClearPolygons();
        Specimen.Source = OverviewImage.Source = null;
        OverviewBorder.Visibility = Visibility.Collapsed;
        OverviewToggle.IsEnabled = false;
        image = null;
        AboutS.Text = AboutE.Text = LanguageStatus.Text = "";
        MeasurementsText.Clear();
        MeasurementsPanel.Visibility = Visibility.Collapsed;
        SInfo.IsEnabled = EInfo.IsEnabled = MaterialTagsButton.IsEnabled = false;
        WelcomeLogo.Visibility = Visibility.Visible;
        ImageMessage.Visibility = Visibility.Collapsed;
        ZoomLabel.Text = "";
        StatusText.Text = T("Выберите объект, затем его элемент. Колесо мыши — масштаб, перетаскивание — перемещение.", "Select an object, then an element. Scroll to zoom; drag to pan.");
    }
    private void Specimen_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListOfElements is null || updating) return;
        ShowMaterial();
    }
    private void ShowMaterial(Guid? element = null)
    {
        ResetSpecimenView();
        if (CurrentSpecimen is { } current) _ = ShowImageAsync(current, element);
    }

    private async Task ShowImageAsync(AtlasSpecimen current, Guid? element)
    {
        var request = new CancellationTokenSource();
        imageRequest = request;
        WelcomeLogo.Visibility = Visibility.Collapsed;
        ImageMessage.Text = T("Загрузка изображения…", "Loading image…");
        ImageMessage.Visibility = Visibility.Visible;
        AboutS.Text = Fallback(current.Summary);
        SInfo.IsEnabled = !string.IsNullOrWhiteSpace(current.Description);
        MaterialTagsButton.IsEnabled = current.MaterialId is not null;
        LanguageStatus.Text = current.Locale == Locale ? "" : T($"Перевода на выбранный язык нет. Показан: {current.LocaleLabel}.", $"Translation unavailable. Showing: {current.LocaleLabel}.");
        // Labels belong to this UI projection; the catalog's original text is unchanged.
        var displayedElements = current.Elements.Select((entry, index) => string.IsNullOrWhiteSpace(entry.Name)
            ? entry with { Name = T($"Элемент {index + 1} без названия", $"Untitled element {index + 1}") }
            : entry).ToArray();
        ListOfElements.ItemsSource = displayedElements;
        ListOfElements.SelectedItem = element is null ? null : displayedElements.FirstOrDefault(e => e.Id == element);
        try
        {
            if (current.ImageIssue is not null)
            {
                ImageMessage.Text = T("Изображение недоступно", "Image unavailable");
                StatusText.Text = UiLanguage.Message(current.ImageIssue);
                return;
            }
            LoadedImage loaded;
            await imageGate.WaitAsync(request.Token);
            try
            {
                request.Token.ThrowIfCancellationRequested();
                loaded = cachedPath == current.ImagePath && cachedImage is not null ? cachedImage : await Task.Run(() => ImageLoader.Load(store, current.ImageId!.Value), request.Token);
            }
            finally { imageGate.Release(); }
            if (request.IsCancellationRequested || closed) return;
            cachedPath = current.ImagePath;
            cachedImage = image = loaded;
            Desk.Width = Specimen.Width = loaded.PixelWidth;
            Desk.Height = Specimen.Height = loaded.PixelHeight;
            Specimen.Source = OverviewImage.Source = loaded.Bitmap;
            OverviewToggle.IsEnabled = true;
            ImageMessage.Visibility = Visibility.Collapsed;
            if (displayedPath != current.ImagePath)
            {
                fitImage = false;
                ImageScale.ScaleX = ImageScale.ScaleY = 1;
                ImageOffset.X = (ImageViewport.ActualWidth - loaded.PixelWidth) / 2;
                ImageOffset.Y = (ImageViewport.ActualHeight - loaded.PixelHeight) / 2;
            }
            displayedPath = current.ImagePath;
            UpdateViewport();
            RenderElement();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or CatalogStorageException or SqliteException or ObjectDisposedException)
        {
            ErrorLog.Write(ex);
            if (request.IsCancellationRequested || closed) return;
            ImageMessage.Text = T("Изображение недоступно", "Image unavailable");
            StatusText.Text = T($"Не удалось открыть «{Path.GetFileName(current.ImagePath)}»: ", $"Could not open “{Path.GetFileName(current.ImagePath)}”: ") + UiLanguage.Error(ex);
        }
        finally { if (ReferenceEquals(imageRequest, request)) imageRequest = null; request.Dispose(); }
    }

    private void Element_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (Desk is not null) RenderElement(); }
    private void RenderElement()
    {
        ClearPolygons();
        var element = CurrentElement;
        AboutE.Text = element is null ? "" : Fallback(element.Summary);
        EInfo.IsEnabled = element is not null && !string.IsNullOrWhiteSpace(element.Description);
        MeasurementsText.Clear();
        MeasurementsPanel.Visibility = Visibility.Collapsed;
        if (image is null || CurrentSpecimen is null) return;
        StatusText.Text = T($"{image.PixelWidth} × {image.PixelHeight} пикселей. Элементов: {CurrentSpecimen.Elements.Count}.", $"{image.PixelWidth} × {image.PixelHeight} pixels. Elements: {CurrentSpecimen.Elements.Count}.");
        if (element is null) return;
        if (element.Regions.Count > 0)
        {
            foreach (var region in element.Regions)
            {
                var geometry = region.Geometry;
                Shape shape = geometry.Kind switch
                {
                    "rectangle" => new Rectangle { Width = geometry.Width, Height = geometry.Height },
                    "ellipse" => new Ellipse { Width = geometry.Width, Height = geometry.Height },
                    "polygon" => new Polygon { Points = new PointCollection(geometry.Points.Select(p => new Point(p.X, p.Y))) },
                    "compound" => new System.Windows.Shapes.Path { Data = CompoundPath(geometry) },
                    _ => throw new InvalidOperationException("Unsupported region geometry kind.")
                };
                if (geometry.Kind is "rectangle" or "ellipse")
                {
                    Canvas.SetLeft(shape, geometry.X);
                    Canvas.SetTop(shape, geometry.Y);
                }
                shape.Tag = region.Id;
                shape.IsHitTestVisible = false;
                Desk.Children.Add(shape);
            }
        }
        else foreach (var polygon in element.Polygons)
        {
            var points = polygon.Points.Select(p => ImageCoordinates.ToPixels(p, CurrentSpecimen.CoordinateSpace, image.DpiX, image.DpiY));
            Desk.Children.Add(new Polygon { Points = new PointCollection(points.Select(p => new Point(p.X, p.Y))), IsHitTestVisible = false });
        }
        UpdateOutlineStyle();
        var count = element.Regions.Count > 0 ? element.Regions.Count : element.Polygons.Count;
        RenderMeasurements(element);
        StatusText.Text = count == 0 ? T("Для этого элемента разметка пока не добавлена.", "This element has no regions yet.") : T($"{element.Name} — областей: {count}.", $"{element.Name} — regions: {count}.");
    }

    private static StreamGeometry CompoundPath(RegionGeometry geometry)
    {
        var path = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var context = path.Open())
            foreach (var ring in geometry.Rings)
            {
                if (ring.Count == 0) continue;
                context.BeginFigure(new Point(ring[0].X, ring[0].Y), true, true);
                context.PolyLineTo(ring.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), true, false);
            }
        path.Freeze();
        return path;
    }

    private void RenderMeasurements(AtlasElement element)
    {
        if (CurrentSpecimen is not { } specimen || image is null) return;
        var geometries = element.Regions.Count > 0 ? element.Regions.Select(region => region.Geometry)
            : element.Polygons.Select(polygon => RegionGeometry.Polygon(polygon.Points.Select(point =>
                ImageCoordinates.ToPixels(point, specimen.CoordinateSpace, image.DpiX, image.DpiY))));
        var calibration = specimen.Calibration;
        var (unitMetres, unitLabel) = calibration?.InputUnit switch
        {
            "m" => (1d, T("м", "m")),
            "cm" => (.01, T("см", "cm")),
            "mm" => (.001, T("мм", "mm")),
            "nm" => (1e-9, T("нм", "nm")),
            _ => (1e-6, T("мкм", "µm"))
        };
        var scaleX = calibration?.PixelSizeXMetres / unitMetres ?? 1;
        var scaleY = calibration?.PixelSizeYMetres / unitMetres ?? 1;
        var lengthUnit = calibration is null ? "px" : unitLabel;
        var areaUnit = lengthUnit + "²";
        var culture = CultureInfo.GetCultureInfo(Locale == "en" ? "en-US" : "ru-RU");
        string Number(double value) => "≈ " + value.ToString("G4", culture);
        var rows = geometries.Select((geometry, index) =>
        {
            var label = T($"Область {index + 1}", $"Region {index + 1}");
            try
            {
                var measurement = RegionMeasurements.Measure(geometry, scaleX, scaleY);
                return label + Environment.NewLine
                    + T("Площадь: ", "Area: ") + "≈ " + MeasurementDisplay.Area(measurement.Area, culture) + " " + areaUnit + Environment.NewLine
                    + T("Периметр: ", "Perimeter: ") + Number(measurement.Perimeter) + " " + lengthUnit;
            }
            catch (ArgumentException)
            {
                // Imported geometry remains viewable even outside the numeric range
                // supported by measurement; never rewrite its original vertices.
                return label + Environment.NewLine + T("Измерение недоступно: превышен числовой диапазон.",
                    "Measurement unavailable: numerical range exceeded.");
            }
        }).ToArray();
        if (rows.Length == 0) return;
        var note = calibration is null
            ? T("Без калибровки: размеры в пикселях.", "Uncalibrated: dimensions are in pixels.")
            : T("По калибровке текущего изображения.", "Using this image's calibration.");
        note += Environment.NewLine + T("Периметр включает границы отверстий; области измеряются отдельно.",
            "Perimeter includes hole boundaries; regions are measured separately.");
        MeasurementsText.Text = string.Join(Environment.NewLine + Environment.NewLine, rows)
            + Environment.NewLine + Environment.NewLine + note;
        MeasurementsPanel.Visibility = Visibility.Visible;
    }
    private void ClearPolygons()
    {
        if (Desk is null) return;
        for (var i = Desk.Children.Count - 1; i >= 0; i--) if (Desk.Children[i] is Shape) Desk.Children.RemoveAt(i);
    }
    private void Outline_Changed(object sender, RoutedEventArgs e) => UpdateOutlineStyle();
    private void Outline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateOutlineStyle();
    private void UpdateOutlineStyle()
    {
        if (Desk is null || OutlineHue is null || OutlineOpacity is null || OutlineToggle is null) return;
        // HSV hue 240° (blue) through green to 0° (red), constant saturation/value.
        var hue = 240 - OutlineHue.Value;
        var x = 1 - Math.Abs(hue / 60 % 2 - 1);
        var (r, g, b) = hue switch { < 60 => (1d, x, 0d), < 120 => (x, 1d, 0d), < 180 => (0d, 1d, x), < 240 => (0d, x, 1d), _ => (0d, 0d, 1d) };
        var brush = new SolidColorBrush(Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
        brush.Freeze();
        foreach (var polygon in Desk.Children.OfType<Shape>())
        {
            polygon.Fill = polygon.Stroke = brush;
            polygon.StrokeThickness = 1.5 / Math.Max(.001, ImageScale.ScaleX);
            polygon.Opacity = OutlineOpacity.Value;
            polygon.Visibility = OutlineToggle.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAt(ImageScale.ScaleX * 1.25, ViewportCenter());
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAt(ImageScale.ScaleX / 1.25, ViewportCenter());
    private void ActualSize_Click(object sender, RoutedEventArgs e) => ZoomAt(1, ViewportCenter());
    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        if (image is null) return;
        fitImage = true;
        UpdateViewport();
    }
    private Point ViewportCenter() => new(ImageViewport.ActualWidth / 2, ImageViewport.ActualHeight / 2);
    private void ZoomAt(double zoom, Point anchor)
    {
        if (image is null) return;
        fitImage = false;
        zoom = Math.Clamp(zoom, .01, 32);
        var ratio = zoom / ImageScale.ScaleX;
        ImageOffset.X = anchor.X - (anchor.X - ImageOffset.X) * ratio;
        ImageOffset.Y = anchor.Y - (anchor.Y - ImageOffset.Y) * ratio;
        ImageScale.ScaleX = ImageScale.ScaleY = zoom;
        UpdateViewport();
    }
    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (image is null) return;
        ZoomAt(ImageScale.ScaleX * Math.Pow(1.2, e.Delta / 120d), e.GetPosition(ImageViewport));
        e.Handled = true;
    }
    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (image is null) return;
        panStart = e.GetPosition(ImageViewport);
        panOffset = new(ImageOffset.X, ImageOffset.Y);
        ImageViewport.CaptureMouse();
        ImageViewport.Cursor = Cursors.Hand;
        e.Handled = true;
    }
    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (panStart is not { } start || image is null) return;
        var current = e.GetPosition(ImageViewport);
        ImageOffset.X = panOffset.X + current.X - start.X;
        ImageOffset.Y = panOffset.Y + current.Y - start.Y;
        UpdateViewport();
    }
    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e) { panStart = null; ImageViewport.ReleaseMouseCapture(); ImageViewport.Cursor = Cursors.Arrow; }
    private void Viewport_LostCapture(object sender, MouseEventArgs e) { panStart = null; ImageViewport.Cursor = Cursors.Arrow; }
    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewport();
    private void Overview_Changed(object sender, RoutedEventArgs e) => UpdateViewport();
    private void UpdateViewport()
    {
        if (image is null || ImageViewport.ActualWidth <= 0 || ImageViewport.ActualHeight <= 0) return;
        var width = ImageViewport.ActualWidth;
        var height = ImageViewport.ActualHeight;
        if (fitImage) ImageScale.ScaleX = ImageScale.ScaleY = Math.Min(width / image.PixelWidth, height / image.PixelHeight);
        var zoom = ImageScale.ScaleX;
        var scaledWidth = image.PixelWidth * zoom;
        var scaledHeight = image.PixelHeight * zoom;
        ImageOffset.X = scaledWidth <= width ? (width - scaledWidth) / 2 : Math.Clamp(ImageOffset.X, width - scaledWidth, 0);
        ImageOffset.Y = scaledHeight <= height ? (height - scaledHeight) / 2 : Math.Clamp(ImageOffset.Y, height - scaledHeight, 0);
        ZoomLabel.Text = $"{zoom:P0}";
        UpdateOutlineStyle();
        var showOverview = OverviewToggle.IsChecked == true && (scaledWidth > width + 1 || scaledHeight > height + 1);
        OverviewBorder.Visibility = showOverview ? Visibility.Visible : Visibility.Collapsed;
        if (!showOverview) return;
        var overviewScale = Math.Min(154d / image.PixelWidth, 110d / image.PixelHeight);
        var overviewWidth = image.PixelWidth * overviewScale;
        var overviewHeight = image.PixelHeight * overviewScale;
        var left = (154 - overviewWidth) / 2;
        var top = (110 - overviewHeight) / 2;
        OverviewImage.Width = overviewWidth;
        OverviewImage.Height = overviewHeight;
        Canvas.SetLeft(OverviewImage, left);
        Canvas.SetTop(OverviewImage, top);
        OverviewRectangle.Width = Math.Min(image.PixelWidth, width / zoom) * overviewScale;
        OverviewRectangle.Height = Math.Min(image.PixelHeight, height / zoom) * overviewScale;
        Canvas.SetLeft(OverviewRectangle, left + Math.Max(0, -ImageOffset.X / zoom) * overviewScale);
        Canvas.SetTop(OverviewRectangle, top + Math.Max(0, -ImageOffset.Y / zoom) * overviewScale);
    }
    private void Overview_Click(object sender, MouseButtonEventArgs e)
    {
        if (image is null) return;
        var point = e.GetPosition(OverviewCanvas);
        var scale = Math.Min(154d / image.PixelWidth, 110d / image.PixelHeight);
        var x = (point.X - (154 - image.PixelWidth * scale) / 2) / scale;
        var y = (point.Y - (110 - image.PixelHeight * scale) / 2) / scale;
        ImageOffset.X = ImageViewport.ActualWidth / 2 - x * ImageScale.ScaleX;
        ImageOffset.Y = ImageViewport.ActualHeight / 2 - y * ImageScale.ScaleY;
        UpdateViewport();
        e.Handled = true;
    }

    private static string Fallback(string text) => string.IsNullOrWhiteSpace(text) ? T("Описание пока не добавлено.", "No description yet.") : text;
    private void Reload_Click(object sender, RoutedEventArgs e) => LoadCatalog();
    private void Home_Click(object sender, RoutedEventArgs e) => Close();
    private void Metadata_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSpecimen is not { } specimen) return;
        var materialId = specimen.MaterialId;
        (string Title, string Description) ReadMetadata()
        {
            var current = FindTranslation(specimen);
            var lines = new List<string> { current.DisplayName };
            if (current.AnalogGroupId is not null) lines.Add(T("Группа аналогов: ", "Analog group: ") + CatalogBrowser.Label(current.AnalogGroupNames, Locale));
            var assigned = materialId is { } id ? taxonomy.MaterialTags.GetValueOrDefault(id) : null;
            if (assigned is null || assigned.Count == 0) lines.Add(T("Теги не назначены.", "No tags assigned."));
            else foreach (var group in taxonomy.Tags.Where(t => assigned.Contains(t.Id)).GroupBy(t => t.GroupId))
            {
                var type = taxonomy.Groups.FirstOrDefault(g => g.Id == group.Key);
                lines.Add("");
                lines.Add(type is null ? T("Без типа", "Untyped") : CatalogBrowser.Label(type.Names, Locale));
                lines.AddRange(group.Select(t => "• " + CatalogBrowser.Label(t.Names, Locale)));
            }
            return (T("Метаданные объекта", "Object metadata"), string.Join("\n", lines));
        }
        var content = ReadMetadata();
        new INFO2(content.Title, content.Description, ReadMetadata) { Owner = this }.Show();
    }
    private AtlasSpecimen FindTranslation(AtlasSpecimen original)
    {
        var matches = catalog?.Specimens.Where(s => s.MaterialId == original.MaterialId && s.ImageId == original.ImageId).ToArray() ?? [];
        return CatalogBrowser.ChooseLanguage(matches, Locale).FirstOrDefault() ?? original;
    }
    private void SInfo_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSpecimen is { } s && SInfo.IsEnabled)
            new INFO2(s.DisplayName, s.Description, () => { var translated = FindTranslation(s); return (translated.DisplayName, translated.Description); }) { Owner = this }.Show();
    }
    private void EInfo_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentElement is { } element && CurrentSpecimen is { } s && EInfo.IsEnabled)
            new INFO2(element.Name, element.Description, () => { var translated = FindTranslation(s).Elements.FirstOrDefault(x => x.Id == element.Id) ?? element; return (translated.Name, translated.Description); }) { Owner = this }.Show();
    }
    private void Warnings_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is not null)
        {
            var warnings = catalog.Warnings;
            new INFO2(T("Замечания к данным", "Data issues"), "", () => (T("Замечания к данным", "Data issues"), string.Join("\n\n", warnings.Select(UiLanguage.Message)))) { Owner = this }.Show();
        }
    }
}

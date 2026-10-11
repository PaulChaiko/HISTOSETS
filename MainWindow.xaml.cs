using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;
using Microsoft.Data.Sqlite;
using Path = System.IO.Path;

namespace HistOSets;

public partial class MainWindow : Window
{
    private readonly string dataDirectory;
    private string catalogDirectory;
    private readonly bool rememberLocation;
    private readonly bool ownsStore;
    private readonly bool autoImport;
    private CatalogStore? store;
    private AtlasCatalog? catalog;
    private CatalogTaxonomy taxonomy = CatalogTaxonomy.Empty;
    private readonly HashSet<Guid> selectedTags = [];
    private LoadedImage? image;
    private LoadedImage? cachedImage;
    private string? cachedPath;
    private string? displayedPath;
    private bool updating;
    private bool updatingTags;
    private bool closed;
    private Exception? catalogError;
    private bool fitImage;
    private Point? panStart;
    private Point panOffset;
    private readonly SemaphoreSlim imageGate = new(1, 1);
    private CancellationTokenSource? imageRequest;
    private AtlasSpecimen? CurrentSpecimen => (ImageSelector.SelectedItem as ImageChoice)?.Specimen ?? ListOfSpecimens.SelectedItem as AtlasSpecimen;
    private AtlasElement? CurrentElement => ListOfElements.SelectedItem as AtlasElement;
    private string Locale => UiLanguage.Locale;
    private static string T(string ru, string en) => UiLanguage.T(ru, en);
    private sealed record ImageChoice(int Number, AtlasSpecimen Specimen)
    {
        public string Label => T("Изображение ", "Image ") + Number + (Specimen.IsPrimaryImage ? T(" (основное)", " (primary)") : "");
    }
    private sealed record TagTypeChoice(Guid? Id, string Label, bool All = false);
    private sealed record TagValueChoice(Guid Id, string Label);
    private TagTypeChoice? selectedTagType;

    public MainWindow() : this(AppContext.BaseDirectory, CatalogLocation.Load(), true) { }
    public MainWindow(string dataDirectory, string? catalogDirectory = null, bool rememberLocation = false)
        : this(dataDirectory, catalogDirectory, rememberLocation, null, true, true) { }
    // Home owns the selected store. Closing the viewer must not release that catalog's lock.
    public MainWindow(CatalogStore store, bool ownsStore = false)
        : this(AppContext.BaseDirectory, store.RootDirectory, false, store, ownsStore, false) { }
    private MainWindow(string dataDirectory, string? catalogDirectory, bool rememberLocation, CatalogStore? selectedStore, bool ownsStore, bool autoImport)
    {
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        this.catalogDirectory = Path.GetFullPath(catalogDirectory ?? Path.Combine(dataDirectory, "Catalog"));
        this.rememberLocation = rememberLocation;
        this.ownsStore = ownsStore;
        this.autoImport = autoImport;
        store = selectedStore;
        InitializeComponent();
        if (selectedStore is not null) { HomeButton.Visibility = Visibility.Visible; DataButton.Visibility = Visibility.Collapsed; }
        TagTypeSelector.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(TagTypeText_Changed));
        TagValueSelector.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(TagValueText_Changed));
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
        DataButton.Content = T("Данные атласа", "Catalog data");
        DictionaryButton.Content = T("Теги…", "Tags…");
        AnalogGroupsButton.Content = T("Аналоги…", "Analogs…");
        PreviewButton.Content = T("Пробный просмотрщик", "Experimental viewer");
        WarningsButton.Content = T("Замечания", "Data issues");
        SearchLabel.Text = T("Поиск по названиям объектов и аналогов", "Search object and analog group names");
        SearchBox.ToolTip = T("Поиск на всех языках. Несколько слов; регистр и е/ё не различаются.", "Search all languages. Multiple words; case and е/ё are ignored.");
        LanguageLabel.Text = T("Язык интерфейса и данных", "Interface and content language");
        LanguageSelector.SelectedIndex = Locale == "en" ? 1 : 0;
        SortLabel.Text = T("Порядок", "Order");
        OriginalOrderItem.Content = T("Исходный порядок", "Original order");
        AlphabeticOrderItem.Content = T("По названию", "By name");
        ResetFiltersButton.Content = T("Сбросить фильтры", "Reset filters");
        MaterialsLabel.Text = T("Объекты", "Objects");
        ElementsLabel.Text = T("Элементы объекта", "Object elements");
        TagFilterExpander.Header = T("Фильтры по тегам", "Tag filters");
        TagTypeLabel.Text = T("Тип тега — введите для поиска", "Tag type — type to search");
        TagValueLabel.Text = T("Значение — выберите для фильтра", "Value — select to filter");
        SInfo.Content = T("Об объекте", "About object");
        MaterialTagsButton.Content = T("Метаданные", "Metadata");
        EInfo.Content = T("Об элементе", "About element");
        FitButton.Content = T("Вписать", "Fit");
        ZoomInButton.ToolTip = T("Приблизить", "Zoom in");
        ZoomOutButton.ToolTip = T("Отдалить", "Zoom out");
        ActualSizeButton.ToolTip = T("Исходный размер: пиксель изображения равен одной единице WPF", "Original size: one image pixel per WPF display unit");
        OutlineToggle.Content = T("Области", "Regions");
        OpacityLabel.Text = T("Видимость", "Opacity");
        HueLabel.Text = T("Оттенок", "Hue");
        updating = false;
    }

    private void LanguageChanged(object? sender, EventArgs e)
    {
        if (closed) return;
        var material = CurrentSpecimen?.MaterialId;
        var element = CurrentElement?.Id;
        var imageId = CurrentSpecimen?.ImageId;
        Localize();
        BuildTagFilters();
        if (catalog is not null) ApplyFilters(material, element, imageId);
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
        var selectedImage = CurrentSpecimen?.ImageId;
        try
        {
            store ??= new CatalogStore(catalogDirectory);
            if (autoImport && store.GetStatistics().Materials == 0)
                store.ImportLegacy(Path.Combine(dataDirectory, "ATLAS", "ATLAS.xml"), dataDirectory, ImageLoader.ReadMetadata);
            catalog = store.Load();
            catalogError = null;
            taxonomy = store.LoadTaxonomy();
            cachedPath = null;
            cachedImage = null;
            DictionaryButton.IsEnabled = AnalogGroupsButton.IsEnabled = true;
            WarningsButton.Visibility = catalog.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BuildTagFilters();
            ApplyFilters(selected, element, selectedImage);
        }
        catch (Exception ex) when (ex is CatalogStorageException or SqliteException or IOException or InvalidDataException or FileFormatException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ErrorLog.Write(ex);
            catalogError = ex;
            catalog = null;
            taxonomy = CatalogTaxonomy.Empty;
            updating = true;
            ListOfSpecimens.ItemsSource = null;
            ImageSelector.ItemsSource = null;
            ImageSelector.Visibility = Visibility.Collapsed;
            updating = false;
            ResetSpecimenView();
            DictionaryButton.IsEnabled = AnalogGroupsButton.IsEnabled = false;
            TagFilters.Children.Clear();
            FilterSummary.Text = "";
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

    private void ApplyFilters(Guid? material = null, Guid? element = null, Guid? selectedImage = null)
    {
        if (catalog is null || updating) return;
        material ??= CurrentSpecimen?.MaterialId;
        element ??= CurrentElement?.Id;
        selectedImage ??= CurrentSpecimen?.ImageId;
        var visible = CatalogBrowser.Filter(catalog, Locale, SearchBox.Text, selectedTags, taxonomy, SortSelector.SelectedIndex == 1);
        updating = true;
        ListOfSpecimens.ItemsSource = visible;
        ListOfSpecimens.SelectedItem = material is null ? null : visible.FirstOrDefault(s => s.MaterialId == material);
        updating = false;
        var total = catalog.Specimens.Select((s, i) => s.MaterialId?.ToString() ?? "legacy:" + i).Distinct().Count();
        CatalogCount.Text = T($"Объектов: {visible.Count} / {total}", $"Objects: {visible.Count} / {total}");
        FilterSummary.Text = selectedTags.Count == 0 ? "" : T("Внутри типа — любой тег; между типами — все.", "Any tag within a type; all selected types must match.");
        BuildSelectedTagChips();
        ShowMaterial(element, selectedImage);
        if (visible.Count == 0)
        {
            WelcomeLogo.Visibility = Visibility.Collapsed;
            ImageMessage.Text = total == 0 ? T("Каталог пока пуст", "The catalog is empty") : T("Объекты не найдены.\nИзмените запрос или сбросьте фильтры.", "No matching objects.\nChange your search or reset filters.");
            ImageMessage.Visibility = Visibility.Visible;
            StatusText.Text = total == 0 ? T("В этом каталоге пока нет изображений объектов.", "This catalog has no object images yet.") : T("Нет объектов, соответствующих поиску и тегам.", "No objects match the search and tags.");
        }
    }

    private TagTypeChoice[] TagTypes() => new[] { new TagTypeChoice(null, T("Все типы", "All types"), true) }
        .Concat(taxonomy.Groups.Select(g => new TagTypeChoice(g.Id, CatalogBrowser.Label(g.Names, Locale))))
        .Concat(taxonomy.Tags.Any(t => t.GroupId is null) ? new[] { new TagTypeChoice(null, T("Без типа", "Untyped")) } : [])
        .ToArray();

    private void BuildTagFilters()
    {
        selectedTags.IntersectWith(taxonomy.Tags.Select(t => t.Id));
        updatingTags = true;
        var types = TagTypes();
        selectedTagType = types.FirstOrDefault(t => t.Id == selectedTagType?.Id && t.All == (selectedTagType?.All ?? true)) ?? types[0];
        TagTypeSelector.ItemsSource = types;
        TagTypeSelector.SelectedItem = selectedTagType;
        TagTypeSelector.Text = selectedTagType.Label;
        TagValueSelector.Text = "";
        TagValueSelector.SelectedItem = null;
        updatingTags = false;
        BuildTagValues();
        BuildSelectedTagChips();
    }

    private void BuildTagValues(string query = "")
    {
        var search = CatalogBrowser.Normalize(query);
        var values = taxonomy.Tags.Where(t => selectedTagType is null || selectedTagType.All || t.GroupId == selectedTagType.Id)
            .Where(t => t.Names.Values.Any(n => CatalogBrowser.Normalize(n).Contains(search, StringComparison.Ordinal)))
            .OrderBy(t => CatalogBrowser.Label(t.Names, Locale), StringComparer.CurrentCultureIgnoreCase).ToArray();
        updatingTags = true;
        TagValueSelector.ItemsSource = values.Select(t => new TagValueChoice(t.Id, CatalogBrowser.Label(t.Names, Locale))).ToArray();
        TagFilters.Children.Clear();
        foreach (var tag in values)
        {
            var count = taxonomy.MaterialTags.Count(p => p.Value.Contains(tag.Id));
            var box = new CheckBox { Content = new TextBlock { Text = $"{CatalogBrowser.Label(tag.Names, Locale)} ({count})", TextWrapping = TextWrapping.Wrap },
                Tag = tag.Id, IsChecked = selectedTags.Contains(tag.Id), Margin = new Thickness(0, 3, 0, 3) };
            box.Checked += TagFilter_Changed;
            box.Unchecked += TagFilter_Changed;
            TagFilters.Children.Add(box);
        }
        if (values.Length == 0) TagFilters.Children.Add(new TextBlock { Text = T("Подходящих тегов нет.", "No matching tags."), TextWrapping = TextWrapping.Wrap });
        updatingTags = false;
    }

    private void BuildSelectedTagChips()
    {
        SelectedFilterTags.Children.Clear();
        foreach (var tag in taxonomy.Tags.Where(t => selectedTags.Contains(t.Id)))
        {
            var button = new Button { Content = CatalogBrowser.Label(tag.Names, Locale) + " ×", Tag = tag.Id, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 4, 4),
                ToolTip = T("Удалить этот фильтр", "Remove this filter"), MaxWidth = 240 };
            button.Click += (_, _) => { selectedTags.Remove(tag.Id); BuildTagValues(); ApplyFilters(); };
            SelectedFilterTags.Children.Add(button);
        }
    }
    private void TagType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || updatingTags || TagValueSelector is null || TagTypeSelector.SelectedItem is not TagTypeChoice choice) return;
        selectedTagType = choice;
        updatingTags = true;
        TagValueSelector.Text = "";
        TagValueSelector.SelectedItem = null;
        updatingTags = false;
        BuildTagValues();
    }
    private void TagValue_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || updatingTags || TagValueSelector.SelectedItem is not TagValueChoice tag) return;
        selectedTags.Add(tag.Id);
        BuildTagValues();
        ApplyFilters();
    }
    private void TagTypeText_Changed(object sender, TextChangedEventArgs e)
    {
        if (updatingTags || !TagTypeSelector.IsKeyboardFocusWithin || e.OriginalSource is not TextBox text) return;
        var value = text.Text;
        if (TagTypeSelector.SelectedItem is TagTypeChoice selected && selected.Label == value) return;
        var caret = text.CaretIndex;
        updatingTags = true;
        TagTypeSelector.ItemsSource = TagTypes().Where(t => CatalogBrowser.Normalize(t.Label).Contains(CatalogBrowser.Normalize(value), StringComparison.Ordinal)).ToArray();
        TagTypeSelector.Text = value;
        text.CaretIndex = Math.Min(caret, value.Length);
        TagTypeSelector.IsDropDownOpen = true;
        updatingTags = false;
    }
    private void TagValueText_Changed(object sender, TextChangedEventArgs e)
    {
        if (updatingTags || !TagValueSelector.IsKeyboardFocusWithin || e.OriginalSource is not TextBox text) return;
        var value = text.Text;
        if (TagValueSelector.SelectedItem is TagValueChoice selected && selected.Label == value) return;
        var caret = text.CaretIndex;
        BuildTagValues(value);
        updatingTags = true;
        TagValueSelector.Text = value;
        text.CaretIndex = Math.Min(caret, value.Length);
        TagValueSelector.IsDropDownOpen = true;
        updatingTags = false;
    }
    private void TagFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (updatingTags) return;
        if (sender is CheckBox { Tag: Guid id } box)
        {
            if (box.IsChecked == true) selectedTags.Add(id); else selectedTags.Remove(id);
            ApplyFilters();
        }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized) ApplyFilters(); }
    private void Options_Changed(object sender, SelectionChangedEventArgs e) { if (IsInitialized && !updating) ApplyFilters(); }
    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        updating = true;
        SearchBox.Clear();
        selectedTags.Clear();
        selectedTagType = null;
        updating = false;
        BuildTagFilters();
        ApplyFilters();
    }

    private void ResetSpecimenView()
    {
        imageRequest?.Cancel();
        ListOfElements.ItemsSource = null;
        ClearPolygons();
        Specimen.Source = OverviewImage.Source = null;
        OverviewBorder.Visibility = Visibility.Collapsed;
        image = null;
        AboutS.Text = AboutE.Text = SelectedTagsText.Text = LanguageStatus.Text = "";
        SInfo.IsEnabled = EInfo.IsEnabled = PreviewButton.IsEnabled = MaterialTagsButton.IsEnabled = false;
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
    private void ShowMaterial(Guid? element = null, Guid? selectedImage = null)
    {
        ResetSpecimenView();
        updating = true;
        ImageSelector.ItemsSource = null;
        ImageSelector.Visibility = Visibility.Collapsed;
        if (ListOfSpecimens.SelectedItem is AtlasSpecimen selected && catalog is not null)
        {
            var variants = selected.MaterialId is { } id ? CatalogBrowser.ChooseLanguage(catalog.Specimens.Where(s => s.MaterialId == id), Locale) : new[] { selected };
            var choices = variants.Select((s, i) => new ImageChoice(i + 1, s)).ToArray();
            ImageSelector.ItemsSource = choices;
            ImageSelector.SelectedItem = choices.FirstOrDefault(x => x.Specimen.ImageId == selectedImage)
                ?? choices.FirstOrDefault(x => x.Specimen.IsPrimaryImage) ?? choices.FirstOrDefault();
            ImageSelector.Visibility = choices.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        updating = false;
        if (CurrentSpecimen is { } current) _ = ShowImageAsync(current, element);
    }
    private void Image_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updating || ListOfElements is null) return;
        var element = CurrentElement?.Id;
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
        ListOfElements.ItemsSource = current.Elements;
        ListOfElements.SelectedItem = element is null ? null : current.Elements.FirstOrDefault(e => e.Id == element);
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
                loaded = cachedPath == current.ImagePath && cachedImage is not null ? cachedImage : await Task.Run(() => ImageLoader.Load(current.ImagePath), request.Token);
            }
            finally { imageGate.Release(); }
            if (request.IsCancellationRequested || closed) return;
            cachedPath = current.ImagePath;
            cachedImage = image = loaded;
            Desk.Width = Specimen.Width = loaded.PixelWidth;
            Desk.Height = Specimen.Height = loaded.PixelHeight;
            Specimen.Source = OverviewImage.Source = loaded.Bitmap;
            ImageMessage.Visibility = Visibility.Collapsed;
            PreviewButton.IsEnabled = true;
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
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException)
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
        if (image is null || CurrentSpecimen is null) return;
        StatusText.Text = T($"{image.PixelWidth} × {image.PixelHeight} пикселей. Элементов: {CurrentSpecimen.Elements.Count}.", $"{image.PixelWidth} × {image.PixelHeight} pixels. Elements: {CurrentSpecimen.Elements.Count}.");
        if (element is null) return;
        foreach (var polygon in element.Polygons)
        {
            var points = polygon.Points.Select(p => ImageCoordinates.ToPixels(p, CurrentSpecimen.CoordinateSpace, image.DpiX, image.DpiY));
            Desk.Children.Add(new Polygon { Points = new PointCollection(points.Select(p => new Point(p.X, p.Y))), IsHitTestVisible = false });
        }
        UpdateOutlineStyle();
        StatusText.Text = element.Polygons.Count == 0 ? T("Для этого элемента разметка пока не добавлена.", "This element has no regions yet.") : T($"{element.Name} — областей: {element.Polygons.Count}.", $"{element.Name} — regions: {element.Polygons.Count}.");
    }
    private void ClearPolygons()
    {
        if (Desk is null) return;
        for (var i = Desk.Children.Count - 1; i >= 0; i--) if (Desk.Children[i] is Polygon) Desk.Children.RemoveAt(i);
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
        foreach (var polygon in Desk.Children.OfType<Polygon>())
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
        OverviewBorder.Visibility = scaledWidth > width + 1 || scaledHeight > height + 1 ? Visibility.Visible : Visibility.Collapsed;
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
    private void Dictionary_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        new TagsWindow(store, CurrentSpecimen?.MaterialId, CurrentSpecimen?.Name, Locale) { Owner = this }.ShowDialog();
        LoadCatalog();
    }
    private void AnalogGroups_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        new AnalogGroupsWindow(store, CurrentSpecimen?.MaterialId, CurrentSpecimen?.Name, Locale) { Owner = this }.ShowDialog();
        LoadCatalog();
    }
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
    private void Data_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CatalogWindow(store, catalogDirectory, dataDirectory) { Owner = this };
        dialog.ShowDialog();
        if (dialog.SelectedCatalogDirectory is { } selected && !string.Equals(selected, catalogDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (ownsStore) store?.Dispose();
            store = null;
            catalog = null;
            catalogDirectory = selected;
            selectedTags.Clear();
            updating = true;
            ListOfSpecimens.ItemsSource = null;
            SearchBox.Clear();
            updating = false;
            if (rememberLocation)
            {
                try { CatalogLocation.Save(selected); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Write(ex); MessageBox.Show(this, T("Каталог открыт, но его путь не удалось запомнить: ", "Catalog opened, but its location could not be saved: ") + UiLanguage.Error(ex), "HISTOSETS"); }
            }
        }
        LoadCatalog();
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
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSpecimen is { } s && image is not null) new ViewerPreviewWindow(s, image, catalogDirectory) { Owner = this }.Show();
    }
}

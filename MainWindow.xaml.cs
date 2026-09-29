using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private CatalogStore? store;
    private AtlasCatalog? catalog;
    private CatalogTaxonomy taxonomy = CatalogTaxonomy.Empty;
    private readonly HashSet<Guid> selectedTags = [];
    private LoadedImage? image;
    private LoadedImage? cachedImage;
    private string? cachedPath;
    private bool updating;
    private bool closed;
    private readonly SemaphoreSlim imageGate = new(1, 1);
    private CancellationTokenSource? imageRequest;
    private AtlasSpecimen? CurrentSpecimen => (ImageSelector.SelectedItem as ImageChoice)?.Specimen ?? ListOfSpecimens.SelectedItem as AtlasSpecimen;
    private AtlasElement? CurrentElement => ListOfElements.SelectedItem as AtlasElement;
    private string Locale => (LanguageSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "ru";
    private sealed record ImageChoice(int Number, AtlasSpecimen Specimen) { public string Label => "Изображение " + Number; }

    public MainWindow() : this(AppContext.BaseDirectory, CatalogLocation.Load(), true) { }
    public MainWindow(string dataDirectory, string? catalogDirectory = null, bool rememberLocation = false)
    {
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        this.catalogDirectory = Path.GetFullPath(catalogDirectory ?? Path.Combine(dataDirectory, "Catalog"));
        this.rememberLocation = rememberLocation;
        InitializeComponent();
        Closed += (_, _) => { closed = true; imageRequest?.Cancel(); store?.Dispose(); };
        LoadCatalog();
    }

    private void LoadCatalog()
    {
        var selected = CurrentSpecimen?.MaterialId;
        var element = CurrentElement?.Id;
        var selectedImage = CurrentSpecimen?.ImageId;
        try
        {
            store ??= new CatalogStore(catalogDirectory);
            if (store.GetStatistics().Materials == 0)
                store.ImportLegacy(Path.Combine(dataDirectory, "ATLAS", "ATLAS.xml"), dataDirectory, ImageLoader.ReadMetadata);
            catalog = store.Load();
            taxonomy = store.LoadTaxonomy();
            cachedPath = null;
            cachedImage = null;
            DictionaryButton.IsEnabled = true;
            WarningsButton.Visibility = catalog.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BuildTagFilters();
            ApplyFilters(selected, element, selectedImage);
        }
        catch (Exception ex) when (ex is CatalogStorageException or SqliteException or IOException or InvalidDataException or FileFormatException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ErrorLog.Write(ex);
            catalog = null;
            taxonomy = CatalogTaxonomy.Empty;
            updating = true;
            ListOfSpecimens.ItemsSource = null;
            ImageSelector.ItemsSource = null;
            ImageSelector.Visibility = Visibility.Collapsed;
            updating = false;
            ResetSpecimenView();
            DictionaryButton.IsEnabled = false;
            CatalogCount.Text = "Каталог недоступен";
            TagFilters.Children.Clear();
            FilterSummary.Text = "";
            WarningsButton.Visibility = Visibility.Collapsed;
            WelcomeLogo.Visibility = Visibility.Collapsed;
            ImageMessage.Text = "Не удалось загрузить каталог";
            ImageMessage.Visibility = Visibility.Visible;
            StatusText.Text = ex.Message + "\nКнопка «Данные атласа» позволяет открыть другой каталог или восстановить резервную копию.";
        }
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
        ListOfSpecimens.SelectedItem = visible.FirstOrDefault(s => s.MaterialId == material);
        updating = false;
        var total = catalog.Specimens.Select(s => s.MaterialId).Distinct().Count();
        CatalogCount.Text = $"Материалов: {visible.Count} / {total}";
        FilterSummary.Text = selectedTags.Count == 0 ? "" : $"Выбрано тегов: {selectedTags.Count}. В группе — любой, между группами — все.";
        ShowMaterial(element, selectedImage);
        if (visible.Count == 0)
        {
            WelcomeLogo.Visibility = Visibility.Collapsed;
            ImageMessage.Text = "Материалы не найдены.\nИзмените запрос или сбросьте фильтры.";
            ImageMessage.Visibility = Visibility.Visible;
            StatusText.Text = "Нет материалов, соответствующих поиску и выбранным тегам.";
        }
    }

    private void BuildTagFilters()
    {
        selectedTags.IntersectWith(taxonomy.Tags.Select(t => t.Id));
        TagFilters.Children.Clear();
        if (taxonomy.Tags.Count == 0)
        {
            TagFilters.Children.Add(new TextBlock { Text = "Тегов пока нет. Создайте их кнопкой «Теги и группы…», затем назначьте материалам.", TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var group in taxonomy.Tags.GroupBy(t => t.GroupId))
        {
            var tagGroup = taxonomy.Groups.FirstOrDefault(g => g.Id == group.Key);
            var title = tagGroup is not null ? CatalogBrowser.Label(tagGroup.Names, Locale) : "Без группы";
            TagFilters.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 3) });
            foreach (var tag in group.OrderBy(t => CatalogBrowser.Label(t.Names, Locale), StringComparer.CurrentCultureIgnoreCase))
            {
                var count = taxonomy.MaterialTags.Count(p => p.Value.Contains(tag.Id));
                var box = new CheckBox { Content = new TextBlock { Text = $"{CatalogBrowser.Label(tag.Names, Locale)} ({count})", TextWrapping = TextWrapping.Wrap },
                    Tag = tag.Id, IsChecked = selectedTags.Contains(tag.Id), Margin = new Thickness(0, 3, 0, 3) };
                box.Checked += TagFilter_Changed;
                box.Unchecked += TagFilter_Changed;
                TagFilters.Children.Add(box);
            }
        }
    }

    private void TagFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: Guid id } box)
        {
            if (box.IsChecked == true) selectedTags.Add(id); else selectedTags.Remove(id);
            ApplyFilters();
        }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized) ApplyFilters(); }
    private void Options_Changed(object sender, SelectionChangedEventArgs e)
    {
        Console.WriteLine($"Options_Changed source={(e.OriginalSource as FrameworkElement)?.Name} updating={updating}");
        if (!IsInitialized || catalog is null) return;
        BuildTagFilters();
        ApplyFilters();
    }
    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        updating = true;
        SearchBox.Clear();
        selectedTags.Clear();
        updating = false;
        BuildTagFilters();
        ApplyFilters();
    }

    private void ResetSpecimenView()
    {
        imageRequest?.Cancel();
        ListOfElements.ItemsSource = null;
        ClearPolygons();
        Specimen.Source = null;
        image = null;
        AboutS.Text = AboutE.Text = SelectedTagsText.Text = LanguageStatus.Text = "";
        SInfo.IsEnabled = EInfo.IsEnabled = PreviewButton.IsEnabled = MaterialTagsButton.IsEnabled = false;
        WelcomeLogo.Visibility = Visibility.Visible;
        ImageMessage.Visibility = Visibility.Collapsed;
        StatusText.Text = "Выберите материал, затем его элемент. Для выбора достаточно одного щелчка.";
    }

    private void Specimen_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Console.WriteLine($"Specimen_SelectionChanged index={ListOfSpecimens.SelectedIndex} updating={updating}");
        if (ListOfElements is null || updating) return;
        ShowMaterial();
    }
    private void ShowMaterial(Guid? element = null, Guid? selectedImage = null)
    {
        Console.WriteLine($"ShowMaterial index={ListOfSpecimens.SelectedIndex}");
        ResetSpecimenView();
        updating = true;
        ImageSelector.ItemsSource = null;
        ImageSelector.Visibility = Visibility.Collapsed;
        if (ListOfSpecimens.SelectedItem is AtlasSpecimen selected && catalog is not null)
        {
            var variants = CatalogBrowser.ChooseLanguage(catalog.Specimens.Where(s => s.MaterialId == selected.MaterialId), Locale);
            var choices = variants.Select((s, i) => new ImageChoice(i + 1, s)).ToArray();
            ImageSelector.ItemsSource = choices;
            ImageSelector.SelectedItem = choices.FirstOrDefault(x => x.Specimen.ImageId == selectedImage) ?? choices.FirstOrDefault();
            ImageSelector.Visibility = choices.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        updating = false;
        if (CurrentSpecimen is { } current) _ = ShowImageAsync(current, element);
    }
    private void Image_Changed(object sender, SelectionChangedEventArgs e)
    {
        Console.WriteLine($"Image_Changed index={ImageSelector.SelectedIndex} updating={updating}");
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
        ImageMessage.Text = "Загрузка изображения…";
        ImageMessage.Visibility = Visibility.Visible;
        AboutS.Text = Fallback(current.Summary);
        SInfo.IsEnabled = !string.IsNullOrWhiteSpace(current.Description);
        MaterialTagsButton.IsEnabled = current.MaterialId is not null;
        LanguageStatus.Text = current.Locale == Locale ? current.LocaleLabel : $"Перевода на выбранный язык нет. Показан: {current.LocaleLabel}.";
        var tags = current.MaterialId is { } id ? taxonomy.MaterialTags.GetValueOrDefault(id) : null;
        SelectedTagsText.Text = tags is null || tags.Count == 0 ? "Теги не назначены" : "Теги: " + string.Join(", ", taxonomy.Tags.Where(t => tags.Contains(t.Id)).Select(t => CatalogBrowser.Label(t.Names, Locale)));
        ListOfElements.ItemsSource = current.Elements;
        ListOfElements.SelectedItem = current.Elements.FirstOrDefault(e => e.Id == element);
        try
        {
            if (current.ImageIssue is not null)
            {
                ImageMessage.Text = "Изображение недоступно";
                StatusText.Text = current.ImageIssue;
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
            Specimen.Source = loaded.Bitmap;
            ImageMessage.Visibility = Visibility.Collapsed;
            PreviewButton.IsEnabled = true;
            StatusText.Text = $"{loaded.PixelWidth} × {loaded.PixelHeight} пикселей. Элементов: {current.Elements.Count}.";
            RenderElement();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException)
        {
            ErrorLog.Write(ex);
            if (request.IsCancellationRequested || closed) return;
            ImageMessage.Text = "Изображение недоступно";
            StatusText.Text = $"Не удалось открыть «{Path.GetFileName(current.ImagePath)}»: {ex.Message}";
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
        if (element is null || image is null || CurrentSpecimen is null) return;
        foreach (var polygon in element.Polygons)
        {
            var points = polygon.Points.Select(p => ImageCoordinates.ToPixels(p, CurrentSpecimen.CoordinateSpace, image.DpiX, image.DpiY));
            Desk.Children.Add(new Polygon { Points = new PointCollection(points.Select(p => new Point(p.X, p.Y))), Fill = Brushes.Aqua, Opacity = 0.5, IsHitTestVisible = false });
        }
        StatusText.Text = element.Polygons.Count == 0 ? "Для этого элемента разметка пока не добавлена." : $"{element.Name} — областей: {element.Polygons.Count}.";
    }
    private void ClearPolygons()
    {
        if (Desk is null) return;
        for (var i = Desk.Children.Count - 1; i >= 0; i--) if (Desk.Children[i] is Polygon) Desk.Children.RemoveAt(i);
    }
    private static string Fallback(string text) => string.IsNullOrWhiteSpace(text) ? "Описание пока не добавлено." : text;
    private void Reload_Click(object sender, RoutedEventArgs e) => LoadCatalog();
    private void Dictionary_Click(object sender, RoutedEventArgs e) => ManageTags(null);
    private void MaterialTags_Click(object sender, RoutedEventArgs e) => ManageTags(CurrentSpecimen?.MaterialId);
    private void ManageTags(Guid? material)
    {
        if (store is null) return;
        var dialog = new TagsWindow(store, material, material is null ? null : CurrentSpecimen?.Name, Locale) { Owner = this };
        dialog.ShowDialog();
        try { taxonomy = store.LoadTaxonomy(); BuildTagFilters(); ApplyFilters(); }
        catch (Exception ex) { ErrorLog.Write(ex); StatusText.Text = ex.Message; }
    }
    private void Data_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CatalogWindow(store, catalogDirectory, dataDirectory) { Owner = this };
        dialog.ShowDialog();
        if (dialog.SelectedCatalogDirectory is { } selected && !string.Equals(selected, catalogDirectory, StringComparison.OrdinalIgnoreCase))
        {
            store?.Dispose();
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Write(ex); MessageBox.Show(this, "Каталог открыт, но его путь не удалось запомнить: " + ex.Message, "HISTOSETS"); }
            }
        }
        LoadCatalog();
    }
    private void SInfo_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSpecimen is { } s && SInfo.IsEnabled) new INFO2(s.Name, s.Description) { Owner = this }.Show();
    }
    private void EInfo_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentElement is { } element && EInfo.IsEnabled) new INFO2(element.Name, element.Description) { Owner = this }.Show();
    }
    private void Warnings_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is not null) new INFO2("Замечания к данным", string.Join("\n\n", catalog.Warnings)) { Owner = this }.Show();
    }
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSpecimen is { } s && image is not null) new ViewerPreviewWindow(s, image, catalogDirectory) { Owner = this }.Show();
    }
}

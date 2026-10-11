using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;

namespace HistOSets;

/// <summary>Index of all editable cards, including incomplete drafts. The owner retains the store.</summary>
public partial class EditorCatalogWindow : Window
{
    private readonly CatalogStore store;
    private CatalogTaxonomy taxonomy = CatalogTaxonomy.Empty;
    private IReadOnlyList<MaterialChoice> choices = [];
    private readonly HashSet<Guid> selectedTags = [];
    private IReadOnlyList<TypeChoice> types = [];
    private TypeChoice? selectedType;
    private bool updating;
    private bool busy;
    private bool closed;
    private bool indexLoaded;
    private bool editorOpen;
    private string catalogName = "";
    private Func<string>? operationMessage;
    private readonly CancellationTokenSource thumbnailCancellation = new();
    private readonly SemaphoreSlim thumbnailSlots = new(2, 2);
    private readonly Dictionary<string, BitmapSource> thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> thumbnailOrder = new();
    private const int ThumbnailCacheLimit = 128;
    private sealed record TypeChoice(Guid? Id, bool All, string Label, string SearchText);
    private sealed record TagChoice(Guid Id, string Label);
    private static string T(string ru, string en) => UiLanguage.T(ru, en);
    private Guid? SelectedId => (ObjectList.SelectedItem as MaterialChoice)?.Entry.Id;

    public EditorCatalogWindow(CatalogStore store)
    {
        this.store = store;
        InitializeComponent();
        TypeSelector.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(TypeText_Changed));
        TagSelector.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(TagText_Changed));
        UiLanguage.Changed += Language_Changed;
        Localize();
        SetBusy(true);
        Loaded += Window_Loaded;
        Closing += (_, e) => { if (busy) e.Cancel = true; };
        Closed += (_, _) =>
        {
            closed = true;
            thumbnailCancellation.Cancel();
            thumbnailCache.Clear();
            UiLanguage.Changed -= Language_Changed;
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        SetBusy(false);
        await ReloadIndex();
    }

    private void Localize()
    {
        updating = true;
        Title = T("Редактирование — HISTOSETS", "Edit catalog — HISTOSETS");
        Heading.Text = T("Объекты для редактирования", "Objects to edit");
        CatalogLabel.Text = catalogName;
        SearchLabel.Text = T("Название объекта или группы аналогов", "Object or analogue group name");
        SearchBox.ToolTip = T("Поиск по RU и EN, без учёта регистра и е/ё", "Search RU and EN names, ignoring case and е/ё");
        StatusFilterLabel.Text = T("Состояние", "Status");
        AllStatusItem.Content = T("Все объекты", "All objects");
        DraftStatusItem.Content = T("Черновики", "Drafts");
        ReadyStatusItem.Content = T("Готовые", "Ready");
        TypeLabel.Text = T("Тип тега — введите для поиска", "Tag type — type to search");
        TagLabel.Text = T("Значение — выберите для фильтра", "Value — select to filter");
        ResetButton.Content = T("Сбросить", "Reset");
        NewButton.Content = T("Новый объект…", "New object…");
        EditButton.Content = T("Редактировать…", "Edit…");
        DeleteButton.Content = T("Удалить…", "Delete…");
        RefreshButton.Content = T("Обновить", "Refresh");
        CloseButton.Content = T("Закрыть", "Close");
        LanguageButton.ToolTip = T("Переключить интерфейс и данные на английский", "Switch interface and content to Russian");
        if (operationMessage is not null) OperationStatus.Text = operationMessage();
        updating = false;
    }

    private async void Language_Changed(object? sender, EventArgs e)
    {
        Localize();
        if (!editorOpen) await ReloadIndex();
    }

    private void SetBusy(bool value)
    {
        busy = value;
        FilterPanel.IsEnabled = LanguageButton.IsEnabled = ObjectList.IsEnabled = Actions.IsEnabled = !value;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        NewButton.IsEnabled = indexLoaded && !busy;
        EditButton.IsEnabled = DeleteButton.IsEnabled = indexLoaded && !busy && SelectedId is not null;
    }

    private void ShowMessage(Func<string> message)
    {
        operationMessage = message;
        OperationStatus.Text = message();
    }

    private void ShowError(Exception ex)
    {
        ErrorLog.Write(ex);
        ShowMessage(() => T("Не удалось выполнить операцию. ", "The operation could not be completed. ") + UiLanguage.Error(ex));
    }

    private async Task ReloadIndex(Guid? select = null)
    {
        if (busy || closed) return;
        select ??= SelectedId;
        var locale = UiLanguage.Locale;
        SetBusy(true);
        ShowMessage(() => T("Загрузка списка…", "Loading objects…"));
        try
        {
            var snapshot = await Task.Run(() => (Entries: store.LoadMaterialIndex(locale), Taxonomy: store.LoadTaxonomy(), Metadata: store.GetMetadata()));
            if (closed) return;
            taxonomy = snapshot.Taxonomy;
            catalogName = snapshot.Metadata.Name;
            CatalogLabel.Text = catalogName;
            choices = snapshot.Entries.Select(CreateChoice).ToArray();
            selectedTags.IntersectWith(taxonomy.Tags.Select(tag => tag.Id));
            BuildTypeChoices();
            indexLoaded = true;
            ApplyFilters(select);
            ShowMessage(() => T("Выберите объект или создайте новый. Черновики без изображения также доступны.",
                "Select an object or create a new one. Drafts without an image are also available."));
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private MaterialChoice CreateChoice(MaterialIndexEntry entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.Name) ? T("Без названия", "Untitled") : entry.Name;
        var group = entry.AnalogGroupId is null ? null : CatalogBrowser.Label(entry.AnalogGroupNames, UiLanguage.Locale);
        var tagGroups = taxonomy.Tags.Where(tag => entry.TagIds.Contains(tag.Id)).GroupBy(tag => tag.GroupId);
        var metadata = tagGroups.Select(tags =>
        {
            var type = taxonomy.Groups.FirstOrDefault(value => value.Id == tags.Key);
            return (type is null ? T("Без типа", "Untyped") : CatalogBrowser.Label(type.Names, UiLanguage.Locale))
                + ": " + string.Join(", ", tags.Select(tag => CatalogBrowser.Label(tag.Names, UiLanguage.Locale)));
        }).ToList();
        if (group is not null) metadata.Insert(0, T("Аналоги: ", "Analogues: ") + group);
        return new(entry)
        {
            DisplayName = group is null ? name : group + " — " + name,
            Summary = string.IsNullOrWhiteSpace(entry.Summary) ? T("Краткое описание пока не заполнено.", "No short description yet.") : entry.Summary,
            Metadata = metadata.Count == 0 ? T("Теги не назначены.", "No tags assigned.") : string.Join(" · ", metadata),
            StatusLabel = entry.Status == "ready" ? T("Готов", "Ready") : T("Черновик", "Draft"),
            StatusBrush = (Brush)FindResource(entry.Status == "ready" ? "SpecimenListBrush" : "ElementListBrush"),
            SearchText = CatalogBrowser.Normalize(string.Join(" ", entry.Names.Values.Concat(entry.AnalogGroupNames.Values))),
            ThumbnailHint = entry.ImagePath is null ? T("Без изображения", "No image") : T("Загрузка…", "Loading…")
        };
    }

    private void BuildTypeChoices()
    {
        var previous = selectedType;
        var result = new List<TypeChoice> { new(null, true, T("Все типы", "All types"), "") };
        result.AddRange(taxonomy.Groups.Select(group => new TypeChoice(group.Id, false,
            CatalogBrowser.Label(group.Names, UiLanguage.Locale), CatalogBrowser.Normalize(string.Join(" ", group.Names.Values)))));
        if (taxonomy.Tags.Any(tag => tag.GroupId is null))
            result.Add(new(null, false, T("Без типа", "Untyped"), CatalogBrowser.Normalize("Без типа Untyped")));
        types = result;
        selectedType = types.FirstOrDefault(type => previous is not null && type.Id == previous.Id && type.All == previous.All) ?? types[0];
        updating = true;
        TypeSelector.ItemsSource = types;
        TypeSelector.SelectedItem = selectedType;
        TypeSelector.Text = selectedType.Label;
        TagSelector.Text = "";
        updating = false;
        BuildTagChoices("");
    }

    private void BuildTagChoices(string query)
    {
        var normalized = CatalogBrowser.Normalize(query);
        var values = taxonomy.Tags.Where(tag => (selectedType is null || selectedType.All || tag.GroupId == selectedType.Id)
            && tag.Names.Values.Any(name => CatalogBrowser.Normalize(name).Contains(normalized, StringComparison.Ordinal)))
            .Select(tag => new TagChoice(tag.Id, CatalogBrowser.Label(tag.Names, UiLanguage.Locale)))
            .OrderBy(tag => tag.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
        updating = true;
        TagSelector.ItemsSource = values;
        TagSelector.SelectedItem = null;
        TagSelector.Text = query;
        updating = false;
    }

    private void ApplyFilters(Guid? select = null)
    {
        if (updating || !indexLoaded) return;
        select ??= SelectedId;
        var tokens = CatalogBrowser.Normalize(SearchBox.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var state = (StatusSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var tagSets = taxonomy.Tags.Where(tag => selectedTags.Contains(tag.Id)).GroupBy(tag => tag.GroupId).Select(group => group.Select(tag => tag.Id).ToArray()).ToArray();
        var filtered = choices.Where(choice => (state == "all" || choice.Entry.Status == state)
            && tokens.All(token => choice.SearchText.Contains(token, StringComparison.Ordinal))
            && tagSets.All(set => set.Any(choice.Entry.TagIds.Contains)))
            .OrderBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        updating = true;
        ObjectList.ItemsSource = filtered;
        ObjectList.SelectedItem = filtered.FirstOrDefault(choice => choice.Entry.Id == select);
        updating = false;
        CountLabel.Text = T($"Объектов: {filtered.Length} / {choices.Count}", $"Objects: {filtered.Length} / {choices.Count}");
        EmptyMessage.Text = choices.Count == 0 ? T("В базе пока нет объектов.\nНажмите «Новый объект…», чтобы создать первый.", "This database has no objects yet.\nChoose New object… to create the first one.")
            : T("Подходящих объектов нет.\nИзмените поиск или сбросьте фильтры.", "No matching objects.\nChange your search or reset filters.");
        EmptyMessage.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildSelectedTags();
        UpdateButtons();
    }

    private void BuildSelectedTags()
    {
        SelectedTags.Children.Clear();
        foreach (var tag in taxonomy.Tags.Where(tag => selectedTags.Contains(tag.Id)))
        {
            var button = new Button
            {
                Content = CatalogBrowser.Label(tag.Names, UiLanguage.Locale) + " ×", Tag = tag.Id,
                Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 5, 5),
                ToolTip = T("Убрать из фильтра", "Remove filter")
            };
            button.Click += (_, _) => { selectedTags.Remove((Guid)button.Tag); ApplyFilters(); };
            SelectedTags.Children.Add(button);
        }
        FilterHint.Text = selectedTags.Count == 0 ? "" : T("Внутри типа — любой тег; между типами — все.", "Any tag within a type; all selected types must match.");
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized) ApplyFilters(); }
    private void Status_Changed(object sender, SelectionChangedEventArgs e) { if (IsInitialized) ApplyFilters(); }
    private void Object_Changed(object sender, SelectionChangedEventArgs e) { if (!updating && EditButton is not null) UpdateButtons(); }
    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updating || TypeSelector.SelectedItem is not TypeChoice type) return;
        selectedType = type;
        BuildTagChoices("");
    }
    private void TypeText_Changed(object sender, TextChangedEventArgs e)
    {
        if (updating || !TypeSelector.IsKeyboardFocusWithin) return;
        var query = TypeSelector.Text;
        var normalized = CatalogBrowser.Normalize(query);
        updating = true;
        TypeSelector.ItemsSource = types.Where(type => type.All || type.SearchText.Contains(normalized, StringComparison.Ordinal)).ToArray();
        TypeSelector.Text = query;
        TypeSelector.IsDropDownOpen = true;
        updating = false;
    }
    private void TagText_Changed(object sender, TextChangedEventArgs e)
    {
        if (updating || !TagSelector.IsKeyboardFocusWithin) return;
        BuildTagChoices(TagSelector.Text);
        TagSelector.IsDropDownOpen = true;
    }
    private void Tag_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updating || TagSelector.SelectedItem is not TagChoice tag) return;
        selectedTags.Add(tag.Id);
        BuildTagChoices("");
        ApplyFilters();
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        updating = true;
        SearchBox.Clear();
        StatusSelector.SelectedIndex = 0;
        selectedTags.Clear();
        selectedType = null;
        updating = false;
        BuildTypeChoices();
        ApplyFilters();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadIndex();
    private void Language_Click(object sender, RoutedEventArgs e)
    {
        try { UiLanguage.Set(UiLanguage.Locale == "ru" ? "en" : "ru"); }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void New_Click(object sender, RoutedEventArgs e) => await OpenEditor(null);
    private async void Edit_Click(object sender, RoutedEventArgs e) { if (SelectedId is { } id) await OpenEditor(id); }
    private async void Object_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject target || ItemsControl.ContainerFromElement(ObjectList, target) is not ListBoxItem) return;
        if (SelectedId is { } id) await OpenEditor(id);
    }
    private async Task OpenEditor(Guid? id)
    {
        if (busy || !indexLoaded) return;
        Guid? saved = id;
        Exception? failure = null;
        try
        {
            editorOpen = true;
            var editor = new EditorWindow(store, id) { Owner = this };
            editor.ShowDialog();
            saved = editor.SavedMaterialId ?? id;
        }
        catch (Exception ex) { failure = ex; }
        finally { editorOpen = false; }
        await ReloadIndex(saved);
        if (failure is not null) ShowError(failure);
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (busy || ObjectList.SelectedItem is not MaterialChoice selected) return;
        if (MessageBox.Show(this,
                T($"Удалить объект «{selected.DisplayName}» вместе с его элементами и областями?", $"Delete “{selected.DisplayName}” with its elements and regions?"),
                T("Удаление объекта", "Delete object"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true);
        var succeeded = false;
        try
        {
            await Task.Run(() => store.DeleteMaterial(selected.Entry.Id, selected.Entry.Revision));
            succeeded = true;
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
        if (succeeded)
        {
            await ReloadIndex();
            ShowMessage(() => T("Объект удалён. Исходные файлы изображений сохранены в каталоге.", "Object deleted. Original image files are retained in the catalog."));
        }
    }

    private async void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image) await LoadThumbnail(image);
    }
    private async void Thumbnail_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MaterialChoice previous) previous.ClearThumbnail();
        if (sender is Image image && image.IsLoaded) await LoadThumbnail(image);
    }
    private void Thumbnail_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image { DataContext: MaterialChoice choice }) choice.ClearThumbnail();
    }
    private async Task LoadThumbnail(Image image)
    {
        if (closed || image.DataContext is not MaterialChoice choice || choice.Entry.ImagePath is not { } path || choice.ThumbnailRequested) return;
        choice.ThumbnailRequested = true;
        var token = thumbnailCancellation.Token;
        var entered = false;
        bool IsCurrent() => !closed && image.IsLoaded && ReferenceEquals(image.DataContext, choice);
        try
        {
            if (thumbnailCache.TryGetValue(path, out var cached)) { if (IsCurrent()) choice.SetThumbnail(cached); return; }
            await thumbnailSlots.WaitAsync(token);
            entered = true;
            if (!IsCurrent()) return;
            if (!thumbnailCache.TryGetValue(path, out cached))
            {
                cached = await Task.Run(() => DecodeThumbnail(path), token);
                if (closed) return;
                if (thumbnailCache.TryGetValue(path, out var existing)) cached = existing;
                else
                {
                    thumbnailCache[path] = cached;
                    thumbnailOrder.Enqueue(path);
                    while (thumbnailCache.Count > ThumbnailCacheLimit) thumbnailCache.Remove(thumbnailOrder.Dequeue());
                }
            }
            if (IsCurrent()) choice.SetThumbnail(cached);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (IsCurrent()) choice.SetUnavailable(T("Изображение недоступно", "Image unavailable"));
        }
        finally { if (entered) thumbnailSlots.Release(); }
    }
    private static BitmapSource DecodeThumbnail(string path)
    {
        int width, height;
        using (var stream = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];
            width = frame.PixelWidth; height = frame.PixelHeight;
        }
        if (width <= 0 || height <= 0) throw new InvalidDataException();
        using var source = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = source;
        var ratio = Math.Min(1d, Math.Min(160d / width, 120d / height));
        bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Floor(width * ratio));
        bitmap.DecodePixelHeight = Math.Max(1, (int)Math.Floor(height * ratio));
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public sealed class MaterialChoice(MaterialIndexEntry entry) : INotifyPropertyChanged
    {
        public MaterialIndexEntry Entry { get; } = entry;
        public string DisplayName { get; init; } = "";
        public string Summary { get; init; } = "";
        public string Metadata { get; init; } = "";
        public string StatusLabel { get; init; } = "";
        public Brush StatusBrush { get; init; } = Brushes.Transparent;
        public string SearchText { get; init; } = "";
        public string ThumbnailHint { get; set; } = "";
        public BitmapSource? Thumbnail { get; private set; }
        public Visibility PlaceholderVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
        internal bool ThumbnailRequested { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
        internal void SetThumbnail(BitmapSource bitmap) { Thumbnail = bitmap; Changed(nameof(Thumbnail)); Changed(nameof(PlaceholderVisibility)); }
        internal void SetUnavailable(string message) { ThumbnailHint = message; Changed(nameof(ThumbnailHint)); }
        internal void ClearThumbnail() { ThumbnailRequested = false; Thumbnail = null; Changed(nameof(Thumbnail)); Changed(nameof(PlaceholderVisibility)); }
    }
}

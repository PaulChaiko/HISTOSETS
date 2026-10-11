using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HistOSets.Controls;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;
using Microsoft.Win32;

namespace HistOSets;

/// <summary>The editor owns a working copy, never the shared catalog connection.</summary>
public partial class EditorWindow : Window
{
    private readonly CatalogStore store;
    private MaterialDraft draft;
    private string savedFingerprint;
    private readonly List<MaterialDraft> undo = [];
    private readonly List<MaterialDraft> redo = [];
    private const int HistoryLimit = 80;
    private bool rendering;
    private bool initialized;
    private Guid? selectedImageId;
    private Guid? selectedElementId;
    private string? displayedImagePath;
    private CatalogTaxonomy taxonomy = CatalogTaxonomy.Empty;
    private IReadOnlyList<CatalogAnalogGroup> analogs = [];
    private Func<string>? statusMessage;
    private sealed record Choice(Guid? Id, string Label);
    private sealed record TypeChoice(Guid? Id, string Label, bool All = false);
    private static string T(string ru, string en) => UiLanguage.T(ru, en);

    public Guid? SavedMaterialId { get; private set; }
    public bool SavedChanges { get; private set; }

    public EditorWindow(CatalogStore store, Guid? materialId = null)
    {
        this.store = store;
        draft = materialId is { } id ? store.LoadMaterialDraft(id) : new MaterialDraft();
        savedFingerprint = Fingerprint(draft);
        InitializeComponent();
        Surface.Tool = RegionEditorTool.Pan;
        Surface.RegionsChanged += Surface_RegionsChanged;
        Surface.SelectionChanged += (_, _) => RefreshRegionSelection();
        Surface.PendingOperationChanged += (_, _) => UpdateState();
        selectedImageId = draft.Images.FirstOrDefault(i => i.IsPrimary)?.Id ?? draft.Images.FirstOrDefault()?.Id;
        selectedElementId = draft.Elements.FirstOrDefault()?.Id;
        initialized = true;
        UiLanguage.Changed += Language_Changed;
        Closed += (_, _) => UiLanguage.Changed -= Language_Changed;
        RefreshDictionaries();
        Localize();
        Render();
        Loaded += (_, _) => MaterialRuName.Focus();
    }

    private static string Fingerprint(MaterialDraft value) => JsonSerializer.Serialize(value);
    private MaterialElementDraft? SelectedElement => draft.Elements.FirstOrDefault(e => e.Id == selectedElementId);
    private MaterialImageDraft? SelectedImage => draft.Images.FirstOrDefault(i => i.Id == selectedImageId);
    private bool IsDirty => Fingerprint(ReadFields(draft.Clone())) != savedFingerprint;

    private static MaterialTextDraft Text(Dictionary<string, MaterialTextDraft> texts, string locale)
        => texts.GetValueOrDefault(locale) ?? new MaterialTextDraft();

    private MaterialDraft ReadFields(MaterialDraft target)
    {
        if (!initialized) return target;
        WriteText(target.Texts, "ru", MaterialRuName, MaterialRuSummary, MaterialRuDescription);
        WriteText(target.Texts, "en", MaterialEnName, MaterialEnSummary, MaterialEnDescription);
        var element = target.Elements.FirstOrDefault(e => e.Id == selectedElementId);
        if (element is not null)
        {
            WriteText(element.Texts, "ru", ElementRuName, ElementRuSummary, ElementRuDescription);
            WriteText(element.Texts, "en", ElementEnName, ElementEnSummary, ElementEnDescription);
        }
        target.AuthorCredit = AuthorCredit.Text;
        target.RightsCredit = RightsCredit.Text;
        return target;
    }

    private static void WriteText(Dictionary<string, MaterialTextDraft> texts, string locale, TextBox name, TextBox summary, TextBox description)
    {
        // Do not invent missing empty translations in a legacy card just by opening it.
        if (!texts.ContainsKey(locale) && name.Text.Length == 0 && summary.Text.Length == 0 && description.Text.Length == 0) return;
        texts[locale] = new MaterialTextDraft { Name = name.Text, Summary = summary.Text, Description = description.Text };
    }

    private void FlushFields()
    {
        if (!initialized || rendering) return;
        var before = draft.Clone();
        ReadFields(draft);
        Record(before);
    }

    private void Record(MaterialDraft before)
    {
        if (Fingerprint(before) == Fingerprint(draft)) return;
        undo.Add(before);
        if (undo.Count > HistoryLimit) undo.RemoveAt(0);
        redo.Clear();
        UpdateState();
    }

    private void Change(Action action, bool render = true)
    {
        FlushFields();
        var before = draft.Clone();
        action();
        Record(before);
        if (render) Render(); else UpdateState();
    }

    private void Render()
    {
        rendering = true;
        try
        {
            ReadText(draft.Texts, "ru", MaterialRuName, MaterialRuSummary, MaterialRuDescription);
            ReadText(draft.Texts, "en", MaterialEnName, MaterialEnSummary, MaterialEnDescription);
            AuthorCredit.Text = draft.AuthorCredit;
            RightsCredit.Text = draft.RightsCredit;
            if (!draft.Images.Any(i => i.Id == selectedImageId)) selectedImageId = draft.Images.FirstOrDefault(i => i.IsPrimary)?.Id ?? draft.Images.FirstOrDefault()?.Id;
            if (!draft.Elements.Any(e => e.Id == selectedElementId)) selectedElementId = draft.Elements.FirstOrDefault()?.Id;
            RefreshLists();
            RenderElementFields();
            RefreshAnalogChoices();
            RefreshTags();
            RefreshLegacyNotice();
            RenderSurface();
        }
        finally { rendering = false; }
        UpdateState();
    }

    private void RefreshAnalogChoices()
    {
        var options = new[] { new Choice(null, T("Без группы аналогов", "No analog group")) }
            .Concat(analogs.Select(g => new Choice(g.Id, g.Label(UiLanguage.Locale)))).ToList();
        if (draft.AnalogGroupId is { } missing && options.All(option => option.Id != missing))
            options.Add(new Choice(missing, T("Недоступная группа — снимите назначение", "Unavailable group — remove assignment")));
        AnalogList.ItemsSource = options;
        AnalogList.SelectedItem = options.FirstOrDefault(option => option.Id == draft.AnalogGroupId);
    }

    private void RefreshLegacyNotice()
    {
        var extraLocales = draft.Texts.Keys.Concat(draft.Elements.SelectMany(e => e.Texts.Keys)).Where(locale => locale is not ("ru" or "en")).Distinct().ToArray();
        LegacyTextNotice.Text = extraLocales.Length == 0 ? "" : T("Старые языковые варианты сохранены без изменений: ", "Legacy language variants are preserved unchanged: ") + string.Join(", ", extraLocales);
    }

    private static void ReadText(Dictionary<string, MaterialTextDraft> texts, string locale, TextBox name, TextBox summary, TextBox description)
    {
        var text = Text(texts, locale);
        name.Text = text.Name;
        summary.Text = text.Summary;
        description.Text = text.Description;
    }

    private void RefreshLists()
    {
        ImageList.ItemsSource = draft.Images.Select((i, n) => new Choice(i.Id,
            (i.IsPrimary ? "★ " : "") + (n + 1) + ". " + Path.GetFileName(i.SourcePath ?? i.ManagedPath ?? "")
            + (i.Metadata is { } m ? $" ({m.Width} × {m.Height})" : ""))).ToArray();
        ImageList.SelectedItem = ((IEnumerable<Choice>)ImageList.ItemsSource).FirstOrDefault(c => c.Id == selectedImageId);
        ElementList.ItemsSource = draft.Elements.Select((e, n) => new Choice(e.Id, ElementLabel(e, n))).ToArray();
        ElementList.SelectedItem = ((IEnumerable<Choice>)ElementList.ItemsSource).FirstOrDefault(c => c.Id == selectedElementId);
    }

    private string ElementLabel(MaterialElementDraft element, int number)
    {
        var text = Text(element.Texts, UiLanguage.Locale).Name;
        if (string.IsNullOrWhiteSpace(text)) text = element.Texts.Values.Select(t => t.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? T("Новый элемент", "New element");
        return $"{number + 1}. {text}";
    }

    private void RenderElementFields()
    {
        var texts = SelectedElement?.Texts ?? [];
        ReadText(texts, "ru", ElementRuName, ElementRuSummary, ElementRuDescription);
        ReadText(texts, "en", ElementEnName, ElementEnSummary, ElementEnDescription);
        ElementFields.IsEnabled = SelectedElement is not null;
    }

    private void RenderSurface()
    {
        var image = SelectedImage;
        var path = image?.SourcePath ?? image?.ManagedPath;
        if (path != displayedImagePath)
        {
            displayedImagePath = null;
            Surface.ImageSource = null;
            if (path is not null)
            {
                try
                {
                    var loaded = ImageLoader.Load(path);
                    Surface.ImageWidth = loaded.PixelWidth;
                    Surface.ImageHeight = loaded.PixelHeight;
                    Surface.ImageSource = loaded.Bitmap;
                    displayedImagePath = path;
                    Surface.FitToView();
                }
                catch (Exception ex) { ShowError(ex); }
            }
        }
        Surface.CanEdit = image is not null && SelectedElement is not null && Surface.ImageSource is not null;
        Surface.Regions = SelectedElement?.Regions.Where(r => r.MaterialImageId == selectedImageId)
            .Select(r => new AtlasRegion(r.Id, r.Geometry)).ToArray() ?? [];
        RefreshRegions();
    }

    private void RefreshRegions()
    {
        var wasRendering = rendering;
        rendering = true;
        try
        {
            RegionList.ItemsSource = (SelectedElement?.Regions.Where(r => r.MaterialImageId == selectedImageId) ?? [])
                .Select((r, n) => new Choice(r.Id, $"{n + 1}. {GeometryName(r.Geometry.Kind)}")).ToArray();
            RegionList.SelectedItem = ((IEnumerable<Choice>)RegionList.ItemsSource).FirstOrDefault(c => c.Id == Surface.SelectedRegionId);
        }
        finally { rendering = wasRendering; }
    }

    private static string GeometryName(string kind) => kind switch
    {
        "rectangle" => T("Прямоугольник", "Rectangle"), "ellipse" => T("Эллипс", "Ellipse"),
        "polygon" => T("Полигон", "Polygon"), _ => kind
    };

    private void RefreshRegionSelection()
    {
        if (!initialized || rendering) return;
        RefreshRegions();
        UpdateState();
    }

    private void UpdateState()
    {
        if (!initialized || rendering) return;
        var dirty = IsDirty;
        Title = T("Редактор — HISTOSETS", "Editor — HISTOSETS") + (dirty ? " *" : "") + " — " +
            (draft.Status == "ready" ? T("Готов", "Ready") : T("Черновик", "Draft"));
        UndoButton.IsEnabled = undo.Count > 0 || Fingerprint(ReadFields(draft.Clone())) != Fingerprint(draft);
        RedoButton.IsEnabled = redo.Count > 0;
        PrimaryImageButton.IsEnabled = SelectedImage is { IsPrimary: false };
        RemoveImageButton.IsEnabled = SelectedImage is not null;
        DeleteElementButton.IsEnabled = SelectedElement is not null;
        DeleteRegionButton.IsEnabled = Surface.SelectedRegionId is not null && !Surface.HasPendingOperation;
        FinishPolygonButton.IsEnabled = CancelPolygonButton.IsEnabled = Surface.HasPendingOperation;
        RectangleTool.IsEnabled = EllipseTool.IsEnabled = PolygonTool.IsEnabled = SelectTool.IsEnabled = Surface.CanEdit;
        CanvasHint.Text = Surface.HasPendingOperation
            ? T("Контур ещё не завершён. Enter или «Завершить» — сохранить контур; Esc — отменить его. Масштаб меняется колесом мыши.",
                "The contour is unfinished. Enter or Finish completes it; Esc cancels it. Use the mouse wheel to zoom.")
            : SelectedImage is null ? T("Добавьте изображение. Его оригинал будет скопирован в каталог при сохранении.", "Add an image. Its original will be copied into the catalog when you save.")
            : SelectedElement is null ? T("Добавьте или выберите элемент, чтобы нанести его области. Колесо мыши — масштаб; «Перемещение» — панорамирование.",
                "Add or select an element to draw its regions. Mouse wheel: zoom; Pan: move the image.")
            : T("Показаны области выбранного элемента на текущем изображении. Выбор: перемещение фигуры и её узлов. Полигон: щелчки, затем Enter. Esc отменяет незавершённое действие.",
                "Regions belong to the selected element and current image. Select: move a shape or its handles. Polygon: click vertices, then Enter. Esc cancels the current operation.");
    }

    private void Form_TextChanged(object sender, TextChangedEventArgs e) { if (initialized && !rendering) UpdateState(); }
    private void Form_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!initialized || rendering) return;
        FlushFields();
    }

    private void Image_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || rendering) return;
        var next = (ImageList.SelectedItem as Choice)?.Id;
        if (!ResolvePending()) { RestoreSelection(ImageList, selectedImageId); return; }
        FlushFields();
        selectedImageId = next;
        Render();
    }

    private void Element_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || rendering) return;
        var next = (ElementList.SelectedItem as Choice)?.Id;
        if (!ResolvePending()) { RestoreSelection(ElementList, selectedElementId); return; }
        FlushFields();
        selectedElementId = next;
        Render();
    }

    private void RestoreSelection(System.Windows.Controls.Primitives.Selector control, Guid? id)
    {
        rendering = true;
        try { control.SelectedItem = control.Items.OfType<Choice>().FirstOrDefault(c => c.Id == id); }
        finally { rendering = false; }
    }

    private void Region_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || rendering || Surface.HasPendingOperation) return;
        Surface.SelectRegion((RegionList.SelectedItem as Choice)?.Id);
        UpdateState();
    }

    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolvePending()) return;
        var dialog = new OpenFileDialog { Title = T("Добавить изображение", "Add image"), Filter = T("Изображения|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|Все файлы|*.*", "Images|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|All files|*.*"), CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) AddImageFromPath(dialog.FileName);
    }

    /// <summary>Uses the same validation and staging path as the native file picker.</summary>
    public void AddImageFromPath(string path)
    {
        if (!ResolvePending()) return;
        try
        {
            path = Path.GetFullPath(path);
            using var sourceGuard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexStringLower(SHA256.HashData(sourceGuard));
            var metadata = ImageLoader.ReadMetadata(path);
            if (metadata.Width <= 0 || metadata.Height <= 0 || metadata.Orientation != 1)
                throw new InvalidDataException(T("Сначала сохраните изображение с правильной ориентацией без EXIF-поворота.", "First save the image in its correct orientation without EXIF rotation."));
            // Decode before admitting the file to the working copy; metadata alone is insufficient.
            _ = ImageLoader.Load(path);
            sourceGuard.Position = 0;
            if (Convert.ToHexStringLower(SHA256.HashData(sourceGuard)) != hash)
                throw new InvalidDataException(T("Изображение изменилось во время загрузки. Добавьте его заново.", "The image changed while loading. Add it again."));
            var image = new MaterialImageDraft { SourcePath = path, SourceSha256 = hash, Metadata = metadata, IsPrimary = draft.Images.Count == 0 };
            Change(() => { draft.Images.Add(image); selectedImageId = image.Id; });
            SetStatus("Изображение добавлено в рабочую копию. Сохраните объект, чтобы записать его в каталог.", "Image added to the working copy. Save the object to store it in the catalog.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void PrimaryImage_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedImage is not { } image || !ResolvePending()) return;
        Change(() => { foreach (var candidate in draft.Images) candidate.IsPrimary = candidate.Id == image.Id; });
    }

    private void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedImage is not { } image || !ResolvePending()) return;
        var regions = draft.Elements.Sum(element => element.Regions.Count(region => region.MaterialImageId == image.Id));
        var question = regions == 0
            ? T("Удалить изображение из объекта? Изменение применится при сохранении.", "Remove this image from the object? This change takes effect when you save.")
            : T($"Удалить изображение и все связанные с ним области ({regions})? Калибровка также будет удалена. Изменение применится при сохранении.",
                $"Remove this image and all {regions} regions linked to it? Its calibration will also be removed. This change takes effect when you save.");
        if (MessageBox.Show(this, question, "HISTOSETS", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Change(() =>
        {
            draft.Images.Remove(image);
            foreach (var element in draft.Elements) element.Regions.RemoveAll(r => r.MaterialImageId == image.Id);
            if (image.IsPrimary && draft.Images.Count > 0) draft.Images[0].IsPrimary = true;
        });
    }

    private void AddElement_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolvePending()) return;
        Change(() => { var element = new MaterialElementDraft(); draft.Elements.Add(element); selectedElementId = element.Id; });
        FormTabs.SelectedItem = ElementsTab;
        ElementRuName.Focus();
    }

    private void DeleteElement_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedElement is not { } element || !ResolvePending()) return;
        if (MessageBox.Show(this, T("Удалить элемент со всеми его областями? Изменение можно отменить до сохранения.", "Delete this element and all its regions? You can undo this before saving."),
            "HISTOSETS", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Change(() => draft.Elements.Remove(element));
    }

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized || rendering) return;
        var next = sender == RectangleTool ? RegionEditorTool.Rectangle : sender == EllipseTool ? RegionEditorTool.Ellipse
            : sender == PolygonTool ? RegionEditorTool.Polygon : sender == SelectTool ? RegionEditorTool.Select : RegionEditorTool.Pan;
        if (Surface.HasPendingOperation && !ResolvePending())
        {
            rendering = true;
            try { ToolButton(Surface.Tool).IsChecked = true; } finally { rendering = false; }
            return;
        }
        FlushFields();
        Surface.Tool = next;
        Surface.Focus();
        UpdateState();
    }

    private RadioButton ToolButton(RegionEditorTool tool) => tool switch
    {
        RegionEditorTool.Rectangle => RectangleTool, RegionEditorTool.Ellipse => EllipseTool,
        RegionEditorTool.Polygon => PolygonTool, RegionEditorTool.Select => SelectTool, _ => PanTool
    };

    private void Surface_RegionsChanged(object? sender, RegionsChangedEventArgs e)
    {
        if (!initialized || rendering || SelectedElement is not { } element || selectedImageId is not { } imageId) return;
        Change(() =>
        {
            element.Regions.RemoveAll(r => r.MaterialImageId == imageId);
            element.Regions.AddRange(e.Regions.Select(r => new MaterialRegionDraft { Id = r.Id, MaterialImageId = imageId, Geometry = r.Geometry }));
        }, render: false);
        RefreshRegions();
    }

    private void FinishPolygon_Click(object sender, RoutedEventArgs e) { Surface.FinishPolygon(); UpdateState(); }
    private void CancelPolygon_Click(object sender, RoutedEventArgs e) { Surface.CancelPendingOperation(); UpdateState(); }
    private void DeleteRegion_Click(object sender, RoutedEventArgs e) => Surface.DeleteSelected();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Surface.ZoomBy(1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Surface.ZoomBy(0.8);
    private void Fit_Click(object sender, RoutedEventArgs e) => Surface.FitToView();
    private void ActualSize_Click(object sender, RoutedEventArgs e) => Surface.ActualSize();

    private bool ResolvePending()
    {
        if (!Surface.HasPendingOperation) return true;
        var prompt = new EditorPendingPrompt { Owner = this };
        prompt.ShowDialog();
        if (prompt.Decision == EditorPendingDecision.CancelStroke) Surface.CancelPendingOperation();
        else if (prompt.Decision == EditorPendingDecision.Finish) Surface.FinishPolygon();
        if (Surface.HasPendingOperation)
        {
            SetStatus("Завершите контур (не менее трёх разных точек) или отмените его. Незавершённый контур не сохранён.",
                "Finish the contour (at least three distinct vertices) or cancel it. The unfinished contour has not been saved.");
            return false;
        }
        return prompt.Decision != EditorPendingDecision.Continue;
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolvePending()) return;
        FlushFields();
        if (undo.Count == 0) return;
        redo.Add(draft.Clone());
        draft = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        Render();
        SetStatus("Действие отменено.", "Action undone.");
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolvePending()) return;
        FlushFields();
        if (redo.Count == 0) return;
        undo.Add(draft.Clone());
        draft = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        Render();
        SetStatus("Действие повторено.", "Action redone.");
    }

    private void SaveDraft_Click(object sender, RoutedEventArgs e) => Save(ready: false);
    private void Ready_Click(object sender, RoutedEventArgs e) => Save(ready: true);
    private bool Save(bool ready)
    {
        if (!ResolvePending()) return false;
        FlushFields();
        try
        {
            var candidate = draft.Clone();
            candidate.Status = ready ? "ready" : "draft";
            var persisted = store.SaveMaterialDraft(candidate, ImageLoader.ReadMetadata);
            draft = persisted;
            savedFingerprint = Fingerprint(draft);
            undo.Clear();
            redo.Clear();
            SavedMaterialId = draft.Id;
            SavedChanges = true;
            RefreshDictionaries();
            Render();
            if (ready) SetStatus("Объект сохранён и готов к использованию.", "Object saved and ready to use.");
            else SetStatus("Черновик сохранён. Можно продолжить редактирование или закрыть окно.", "Draft saved. You can continue editing or close this window.");
            return true;
        }
        catch (Exception ex) { ShowError(ex); return false; }
    }

    private void ShowError(Exception ex)
    {
        ErrorLog.Write(ex);
        statusMessage = () => UiLanguage.Error(ex);
        OperationStatus.Text = statusMessage();
    }

    private void SetStatus(string russian, string english)
    {
        statusMessage = () => T(russian, english);
        OperationStatus.Text = statusMessage();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!initialized) return;
        if (!ResolvePending()) { e.Cancel = true; return; }
        FlushFields();
        if (!IsDirty) return;
        var prompt = new EditorClosePrompt { Owner = this };
        prompt.ShowDialog();
        e.Cancel = prompt.Decision == EditorCloseDecision.Cancel || prompt.Decision == EditorCloseDecision.Save && !Save(ready: false);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.S) { e.Handled = true; Save(ready: false); }
        else if (e.Key == Key.Z) { e.Handled = true; Undo_Click(sender, e); }
        else if (e.Key == Key.Y) { e.Handled = true; Redo_Click(sender, e); }
    }

    private void RefreshDictionaries()
    {
        taxonomy = store.LoadTaxonomy();
        analogs = store.LoadAnalogGroups();
        var selected = TagTypeList.SelectedItem as TypeChoice;
        var wasRendering = rendering;
        rendering = true;
        try
        {
            var types = new List<TypeChoice> { new(null, T("Все типы", "All types"), true) };
            types.AddRange(taxonomy.Groups.OrderByDescending(g => g.IsSystem).Select(g => new TypeChoice(g.Id, CatalogBrowser.Label(g.Names, UiLanguage.Locale))));
            if (taxonomy.Tags.Any(t => t.GroupId is null)) types.Add(new(null, T("Без типа (старые теги)", "Untyped (legacy tags)")));
            TagTypeList.ItemsSource = types;
            TagTypeList.SelectedItem = types.FirstOrDefault(t => t.Id == selected?.Id && t.All == selected?.All) ?? types[0];
        }
        finally { rendering = wasRendering; }
    }

    private void RefreshTags()
    {
        TagChoices.Children.Clear();
        var group = TagTypeList.SelectedItem as TypeChoice;
        var tokens = CatalogBrowser.Normalize(TagSearch.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var tag in taxonomy.Tags.Where(t => (group is null || group.All || t.GroupId == group.Id)
            && tokens.All(token => t.Names.Values.Any(name => CatalogBrowser.Normalize(name).Contains(token, StringComparison.Ordinal))))
            .OrderBy(t => CatalogBrowser.Label(t.Names, UiLanguage.Locale), StringComparer.CurrentCultureIgnoreCase))
        {
            AddTagChoice(tag.Id, CatalogBrowser.Label(tag.Names, UiLanguage.Locale));
        }
        foreach (var missing in draft.TagIds.Where(id => taxonomy.Tags.All(t => t.Id != id)))
            AddTagChoice(missing, T("Недоступный тег — снимите назначение", "Unavailable tag — remove assignment"));
    }

    private void AddTagChoice(Guid id, string label)
    {
        var checkbox = new CheckBox { Content = label, Tag = id, IsChecked = draft.TagIds.Contains(id), Margin = new Thickness(0, 3, 0, 3) };
        checkbox.Checked += (_, _) => { if (!rendering) Change(() => draft.TagIds.Add(id), render: false); };
        checkbox.Unchecked += (_, _) => { if (!rendering) Change(() => draft.TagIds.Remove(id), render: false); };
        TagChoices.Children.Add(checkbox);
    }

    private void TagType_Changed(object sender, SelectionChangedEventArgs e) { if (initialized && !rendering) RefreshTags(); }
    private void TagSearch_Changed(object sender, TextChangedEventArgs e) { if (initialized && !rendering) RefreshTags(); }
    private void Analog_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || rendering) return;
        Change(() => draft.AnalogGroupId = (AnalogList.SelectedItem as Choice)?.Id, render: false);
    }

    private void NewTag_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolvePending()) return;
        FlushFields();
        var groups = taxonomy.Groups.Where(g => g.IsSystem).Select(g => ((Guid?)g.Id, CatalogBrowser.Label(g.Names, UiLanguage.Locale))).ToList();
        Guid? created = null;
        var dialog = new TaxonomyEditWindow(T("Создать значение в общем справочнике", "Create a value in the shared dictionary"), null, groups,
            (TagTypeList.SelectedItem as TypeChoice)?.Id, (ru, en, group) => created = store.SaveTag(null, group, ru, en), requireBoth: true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        RefreshDictionaries();
        if (created is { } id) Change(() => draft.TagIds.Add(id)); else Render();
    }

    private void NewAnalog_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolvePending()) return;
        FlushFields();
        Guid? created = null;
        var dialog = new TaxonomyEditWindow(T("Создать группу в общем справочнике", "Create a group in the shared dictionary"), null, null, null,
            (ru, en, _) => created = store.SaveAnalogGroup(null, ru, en), requireBoth: true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        RefreshDictionaries();
        Change(() => draft.AnalogGroupId = created);
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        try { UiLanguage.Set(UiLanguage.Locale == "ru" ? "en" : "ru"); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Language_Changed(object? sender, EventArgs e)
    {
        FlushFields();
        Localize();
        RefreshDictionaries();
        // A locale switch must not reset the surface or cancel an unfinished contour.
        rendering = true;
        try
        {
            RefreshLists();
            RefreshAnalogChoices();
            RefreshTags();
            RefreshRegions();
            RefreshLegacyNotice();
        }
        finally { rendering = false; }
        OperationStatus.Text = statusMessage?.Invoke() ?? "";
        UpdateState();
    }

    private void Localize()
    {
        SaveDraftButton.Content = T("Сохранить черновик", "Save draft"); SaveDraftButton.ToolTip = "Ctrl+S";
        ReadyButton.Content = T("Проверить и сохранить готовым", "Validate and save as ready");
        UndoButton.Content = T("Отменить", "Undo"); UndoButton.ToolTip = "Ctrl+Z";
        RedoButton.Content = T("Повторить", "Redo"); RedoButton.ToolTip = "Ctrl+Y";
        CloseButton.Content = T("Закрыть", "Close");
        ImageHeading.Text = T("Изображения объекта", "Object images");
        AddImageButton.Content = T("Добавить изображение…", "Add image…");
        PrimaryImageButton.Content = T("Сделать основным", "Set as primary");
        RemoveImageButton.Content = T("Удалить изображение", "Remove image");
        PanTool.Content = T("Перемещение", "Pan"); SelectTool.Content = T("Выбор / правка", "Select / edit");
        RectangleTool.Content = T("Прямоугольник", "Rectangle"); EllipseTool.Content = T("Эллипс", "Ellipse"); PolygonTool.Content = T("Полигон", "Polygon");
        FitButton.Content = T("Вписать", "Fit"); ActualSizeButton.ToolTip = T("Один пиксель изображения на единицу экрана", "One image pixel per screen unit");
        FinishPolygonButton.Content = T("Завершить", "Finish"); CancelPolygonButton.Content = T("Отменить контур", "Cancel contour");
        DeleteRegionButton.Content = T("Удалить область", "Delete region");
        ObjectTab.Header = T("Объект", "Object"); ElementsTab.Header = T("Элементы", "Elements"); MetadataTab.Header = T("Метаданные", "Metadata");
        MaterialHelp.Text = T("Черновик допускает неполные данные. Для готовности заполните все поля RU и EN, добавьте основное изображение и описания элементов.",
            "Drafts may be incomplete. To mark an object ready, fill all RU and EN fields, add a primary image and complete element descriptions.");
        foreach (var label in new[] { MaterialRuNameLabel, MaterialEnNameLabel, ElementRuNameLabel, ElementEnNameLabel }) label.Text = T("Название", "Name");
        foreach (var label in new[] { MaterialRuSummaryLabel, MaterialEnSummaryLabel, ElementRuSummaryLabel, ElementEnSummaryLabel }) label.Text = T("Краткое описание", "Summary");
        foreach (var label in new[] { MaterialRuDescriptionLabel, MaterialEnDescriptionLabel, ElementRuDescriptionLabel, ElementEnDescriptionLabel }) label.Text = T("Подробное описание", "Full description");
        AddElementButton.Content = T("Добавить элемент", "Add element"); DeleteElementButton.Content = T("Удалить", "Delete");
        RegionsHeading.Text = T("Области на текущем изображении", "Regions on the current image");
        AnalogLabel.Text = T("Группа аналогов", "Analog group"); NewAnalogButton.Content = T("Создать группу…", "Create group…");
        TagTypeLabel.Text = T("Типовые теги", "Typed tags"); TagSearch.ToolTip = T("Поиск по RU и EN", "Search RU and EN");
        System.Windows.Automation.AutomationProperties.SetName(TagSearch, T("Поиск тегов", "Search tags"));
        NewTagButton.Content = T("Создать тег…", "Create tag…");
        DictionaryHelp.Text = T("Назначения сохраняются вместе с объектом. Новые значения тегов и группы аналогов записываются в общий справочник сразу и останутся в нём при отмене карточки.",
            "Assignments are saved with the object. New tag values and analog groups are saved to the shared dictionary immediately and remain there if you discard this card.");
        AuthorCreditLabel.Text = T("Сведения об авторстве (старое поле)", "Author credit (legacy field)");
        RightsCreditLabel.Text = T("Права и источник изображения", "Image rights and source");
    }
}

internal enum EditorCloseDecision { Cancel, Save, Discard }
internal sealed class EditorClosePrompt : Window
{
    public EditorCloseDecision Decision { get; private set; }
    public EditorClosePrompt()
    {
        NameScope.SetNameScope(this, new NameScope());
        Title = UiLanguage.T("Несохранённые изменения", "Unsaved changes");
        Width = 530; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = UiLanguage.T("Сохранить изменения объекта? Сохранение создаст черновик. «Не сохранять» отменяет изменения с момента последнего сохранения.",
            "Save changes to this object? Saving creates a draft. Discard removes changes made since the last save."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        var actions = new WrapPanel();
        Add("PromptSaveButton", UiLanguage.T("Сохранить", "Save"), EditorCloseDecision.Save);
        Add("PromptDiscardButton", UiLanguage.T("Не сохранять", "Discard"), EditorCloseDecision.Discard);
        Add("PromptCancelButton", UiLanguage.T("Продолжить работу", "Continue editing"), EditorCloseDecision.Cancel);
        panel.Children.Add(actions); Content = panel;
        void Add(string name, string label, EditorCloseDecision decision)
        {
            var button = new Button { Name = name, Content = label, IsCancel = decision == EditorCloseDecision.Cancel };
            button.Click += (_, _) => { Decision = decision; DialogResult = decision != EditorCloseDecision.Cancel; };
            RegisterName(name, button); actions.Children.Add(button);
        }
    }
}

internal enum EditorPendingDecision { Continue, Finish, CancelStroke }
internal sealed class EditorPendingPrompt : Window
{
    public EditorPendingDecision Decision { get; private set; }
    public EditorPendingPrompt()
    {
        NameScope.SetNameScope(this, new NameScope());
        Title = UiLanguage.T("Незавершённый контур", "Unfinished contour");
        Width = 550; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = UiLanguage.T("Перед продолжением завершите контур либо отмените его. Незавершённые точки не записываются в базу данных.",
            "Finish the contour or cancel it before continuing. Unfinished vertices are never written to the database."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        var actions = new WrapPanel();
        Add("PromptFinishButton", UiLanguage.T("Завершить контур", "Finish contour"), EditorPendingDecision.Finish);
        Add("PromptCancelStrokeButton", UiLanguage.T("Отменить контур", "Cancel contour"), EditorPendingDecision.CancelStroke);
        Add("PromptContinueButton", UiLanguage.T("Вернуться", "Go back"), EditorPendingDecision.Continue);
        panel.Children.Add(actions); Content = panel;
        void Add(string name, string label, EditorPendingDecision decision)
        {
            var button = new Button { Name = name, Content = label, IsCancel = decision == EditorPendingDecision.Continue };
            button.Click += (_, _) => { Decision = decision; DialogResult = decision != EditorPendingDecision.Continue; };
            RegisterName(name, button); actions.Children.Add(button);
        }
    }
}

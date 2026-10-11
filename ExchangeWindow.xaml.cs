using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;
using Microsoft.Win32;

namespace HistOSets;

/// <summary>Review a selected subset before applying it to the borrowed catalog.</summary>
public partial class ExchangeWindow : Window
{
    private readonly CatalogStore store;
    private readonly List<ExportRow> exportRows = [];
    private readonly ObservableCollection<PreviewRow> previewRows = [];
    private ExchangePackage? package;
    private bool busy;
    private bool initialized;
    private bool closed;
    private string? packagePath;
    private Func<string>? statusFactory;

    public ExchangePreview? Preview { get; private set; }
    public bool IsBusy => busy;

    public ExchangeWindow(CatalogStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        InitializeComponent();
        PreviewList.ItemsSource = previewRows;
        initialized = true;
        UiLanguage.Changed += Language_Changed;
        ApplyLanguage();
        Loaded += Window_Loaded;
        Closing += (_, e) => { if (busy) e.Cancel = true; };
        Closed += (_, _) =>
        {
            closed = true;
            UiLanguage.Changed -= Language_Changed;
            DisposePackage();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        await ReloadExportIndexAsync();
    }

    private static string T(string ru, string en) => UiLanguage.T(ru, en);
    private async void Language_Changed(object? sender, EventArgs e)
    {
        ApplyLanguage();
        if (!busy && Preview is not null && package is not null) await RefreshPreviewLanguageAsync();
    }

    private void ApplyLanguage()
    {
        Title = T("HISTOSETS — обмен материалами", "HISTOSETS — material exchange");
        Heading.Text = T("Обмен материалами", "Material exchange");
        CatalogLabel.Text = T("Текущая база: ", "Current database: ") + store.GetMetadata().Name;
        ExportTab.Header = T("Экспорт", "Export");
        ImportTab.Header = T("Импорт и объединение", "Import and merge");
        ExportHint.Text = T(
            "Выберите объекты для передачи. Пакет включает их изображения, тексты, области, калибровку, теги и группы аналогов. Удалённые объекты добавляются только при явном выборе.",
            "Select objects to share. The package includes their images, texts, regions, calibration, tags and analog groups. Deleted objects are included only when explicitly selected.");
        SearchLabel.Text = T("Поиск по названию или UUID", "Search by name or UUID");
        IncludeDeletedBox.Content = T("Показать удалённые", "Show deleted objects");
        IncludeDeletedBox.ToolTip = T("Выбранные удалённые объекты передают отметку об удалении. Импорт потребует отдельного решения.",
            "Selecting a deleted object transfers its deletion marker. Import requires an explicit decision.");
        SelectAllButton.Content = T("Выбрать показанные", "Select visible");
        ClearSelectionButton.Content = T("Снять выбор", "Clear selection");
        ExportButton.Content = T("Сохранить пакет…", "Save package…");
        ImportHint.Text = T("Откройте пакет, проверьте различия и выберите действия. Предварительный просмотр не изменяет базу.",
            "Open a package, review the differences and choose actions. Preview does not change the database.");
        LoadPackageButton.Content = T("Открыть пакет…", "Open package…");
        ObjectColumn.Header = T("Объект / справочник", "Object / dictionary");
        ChangeColumn.Header = T("Различие", "Change");
        DecisionColumn.Header = T("Решение", "Decision");
        LocalHeading.Text = T("В текущей базе", "In the current database");
        IncomingHeading.Text = T("В пакете", "In the package");
        ComparisonSplitter.ToolTip = T("Перетащите, чтобы увеличить область сравнения", "Drag to enlarge the comparison area");
        ApplyButton.Content = T("Применить выбранные действия", "Apply selected actions");
        CloseButton.Content = T("Закрыть", "Close");
        LanguageButton.ToolTip = T("Переключить интерфейс на английский", "Switch interface to Russian");
        foreach (var row in exportRows) row.RefreshLanguage();
        foreach (var row in previewRows) row.RefreshLanguage();
        if (statusFactory is not null) OperationStatus.Text = statusFactory();
        RefreshExportFilter();
        RefreshPackageLabels();
        RefreshDetails();
        RefreshCommands();
    }

    public async Task ReloadExportIndexAsync()
    {
        if (busy || closed) return;
        var selected = exportRows.Where(r => r.IsSelected).Select(r => r.Info.Id).ToHashSet();
        SetBusy(true);
        try
        {
            var includeDeleted = IncludeDeletedBox.IsChecked == true;
            var locale = UiLanguage.Locale;
            var items = await Task.Run(() => store.LoadExchangeIndex(locale, includeDeleted));
            InstallExportIndex(items, selected);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void InstallExportIndex(IReadOnlyList<ExchangeMaterialInfo> items, HashSet<Guid>? selected = null)
    {
        exportRows.Clear();
        foreach (var item in items)
        {
            var row = new ExportRow(item, RefreshCommands) { IsSelected = selected?.Contains(item.Id) == true };
            exportRows.Add(row);
        }
        RefreshExportFilter();
    }

    public void SelectForExport(IEnumerable<Guid> ids)
    {
        if (busy) return;
        var selected = ids.ToHashSet();
        foreach (var row in exportRows) row.IsSelected = selected.Contains(row.Info.Id);
        RefreshCommands();
    }

    public async Task ExportSelectedToPathAsync(string path)
    {
        if (busy || closed) return;
        var ids = exportRows.Where(r => r.IsSelected).Select(r => r.Info.Id).ToArray();
        if (ids.Length == 0) return;
        SetBusy(true);
        ShowStatus(() => T("Создание и проверка пакета…", "Creating and verifying the package…"));
        try
        {
            await Task.Run(() => store.ExportMaterials(path, ids));
            ShowStatus(() => T($"Пакет сохранён. Объектов: {ids.Length}.\n", $"Package saved. Objects: {ids.Length}.\n") + Path.GetFullPath(path));
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    public async Task LoadPackagePathAsync(string path)
    {
        if (busy || closed) return;
        ExchangeTabs.SelectedItem = ImportTab;
        SetBusy(true);
        ClearPreview();
        ShowStatus(() => T("Проверка пакета и сравнение с текущей базой…", "Verifying the package and comparing it with the current database…"));
        ExchangePackage? candidate = null;
        try
        {
            candidate = await Task.Run(() =>
            {
                var opened = ExchangePackage.Open(path);
                try { opened.ValidateImages(ImageLoader.ReadMetadata); return opened; }
                catch { DisposeSafely(opened); throw; }
            });
            var incoming = candidate;
            var locale = UiLanguage.Locale;
            var plan = await Task.Run(() => store.PreviewExchange(incoming, locale));
            package = candidate;
            candidate = null;
            packagePath = Path.GetFullPath(path);
            InstallPreview(plan);
            PreviewList.SelectedItem = previewRows.FirstOrDefault(r => r.Resolution == ExchangeResolution.Unresolved) ?? previewRows.FirstOrDefault();
            RefreshPackageLabels();
            RefreshDetails();
            ShowStatus(() => T("Проверка завершена. Просмотр и выбор решений не изменяют базу.",
                "Verification complete. Reviewing the plan and choosing decisions do not change the database."));
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (candidate is not null) DisposeSafely(candidate);
            SetBusy(false);
        }
    }

    private void InstallPreview(ExchangePreview plan)
    {
        Preview = plan;
        previewRows.Clear();
        foreach (var item in plan.Items.OrderByDescending(item => item.RequiresDecision)
            .ThenBy(item => item.Status == ExchangeChangeKind.Unchanged)
            .ThenBy(item => item.Kind != ExchangeEntityKind.Material))
            previewRows.Add(new PreviewRow(item, item.Kind == ExchangeEntityKind.Material
                ? package?.Materials.FirstOrDefault(m => m.Id == item.Id)?.Names : null, RefreshCommands));
    }

    private async Task RefreshPreviewLanguageAsync()
    {
        if (package is null || Preview is null) return;
        var currentPackage = package;
        var prior = Preview;
        var selectedKey = (PreviewList.SelectedItem as PreviewRow)?.Item.Key;
        var choices = previewRows.ToDictionary(r => r.Item.Key, r => (r.Item.Status, r.Resolution), StringComparer.Ordinal);
        var locale = UiLanguage.Locale;
        SetBusy(true);
        try
        {
            var localized = await Task.Run(() => store.PreviewExchange(currentPackage, locale));
            InstallPreview(localized);
            if (localized.MatchesTargetState(prior))
            {
                foreach (var row in previewRows)
                    if (choices.TryGetValue(row.Item.Key, out var choice) && choice.Status == row.Item.Status
                        && row.Options.Any(option => option.Value == choice.Resolution)) row.Resolution = choice.Resolution;
            }
            else ShowStatus(() => T("База изменилась. Предварительный просмотр обновлён; проверьте решения ещё раз.",
                "The database changed. The preview was refreshed; review the decisions again."));
            PreviewList.SelectedItem = previewRows.FirstOrDefault(r => r.Item.Key == selectedKey) ?? previewRows.FirstOrDefault();
            RefreshPackageLabels();
            RefreshDetails();
        }
        catch (Exception ex) { ClearPreview(); ShowError(ex); }
        finally { SetBusy(false); }
    }

    public void SetResolution(string key, ExchangeResolution value)
    {
        if (busy) return;
        var row = previewRows.FirstOrDefault(r => r.Item.Key == key);
        if (row is null) throw new ArgumentException("Unknown exchange preview item.", nameof(key));
        if (!row.Options.Any(option => option.Value == value)) throw new ArgumentOutOfRangeException(nameof(value));
        row.Resolution = value;
    }

    public async Task<ExchangeImportResult?> ApplyImportAsync()
    {
        if (busy || closed || Preview is null || previewRows.Any(r => r.Resolution == ExchangeResolution.Unresolved)) return null;
        var plan = Preview;
        var choices = previewRows.ToDictionary(r => r.Item.Key, r => r.Resolution, StringComparer.Ordinal);
        SetBusy(true);
        ShowStatus(() => T("Создание резервной копии и применение выбранных действий…", "Creating a backup and applying selected actions…"));
        ExchangeImportResult result;
        try
        {
            result = await Task.Run(() => store.ApplyExchange(plan, choices, ImageLoader.ReadMetadata));
        }
        catch (Exception ex) { ShowError(ex); SetBusy(false); return null; }
        // The transaction has committed. A nonessential list refresh must never report an import failure.
        try
        {
            ClearPreview();
            ShowStatus(() => ImportResultText(result));
            var locale = UiLanguage.Locale;
            var includeDeleted = IncludeDeletedBox.IsChecked == true;
            var items = await Task.Run(() => store.LoadExchangeIndex(locale, includeDeleted));
            InstallExportIndex(items);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            ShowStatus(() => ImportResultText(result) + T("\nНе удалось обновить список: ", "\nThe list could not be refreshed: ") + UiLanguage.Error(ex));
        }
        finally { SetBusy(false); }
        return result;
    }

    private static string ImportResultText(ExchangeImportResult result)
    {
        var text = result.Changed
            ? T($"Импорт завершён. Добавлено: {result.Added}; обновлено: {result.Updated}; удалено: {result.Deleted}.",
                $"Import complete. Added: {result.Added}; updated: {result.Updated}; deleted: {result.Deleted}.")
            : T("Объединение завершено. Изменений в данных нет.", "Merge complete. No data changes.");
        text += T($" Сохранено локальных: {result.Kept}; пропущено: {result.Skipped}.",
            $" Kept local: {result.Kept}; skipped: {result.Skipped}.");
        if (result.DemotedMaterials > 0)
            text += T($" Переведено в черновики после проверки полноты: {result.DemotedMaterials}.",
                $" Changed to drafts after completeness checks: {result.DemotedMaterials}.");
        if (result.BackupPath is not null) text += T("\nРезервная копия: ", "\nBackup: ") + result.BackupPath;
        return text;
    }

    private void ClearPreview()
    {
        Preview = null;
        previewRows.Clear();
        packagePath = null;
        DisposePackage();
        RefreshPackageLabels();
        RefreshDetails();
        RefreshCommands();
    }

    private void DisposePackage()
    {
        var previous = package;
        package = null;
        if (previous is not null) DisposeSafely(previous);
    }

    private static void DisposeSafely(ExchangePackage value)
    {
        try { value.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Write(ex); }
    }

    private void RefreshExportFilter()
    {
        if (!initialized) return;
        var search = SearchBox.Text.Trim();
        var visible = exportRows.Where(row => search.Length == 0 || row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || row.Info.Names.Values.Any(name => name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            || row.Info.Id.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        ExportList.ItemsSource = visible;
        EmptyExportLabel.Text = exportRows.Count == 0
            ? T("В этой базе нет объектов для экспорта.", "This database has no objects to export.")
            : T("По вашему запросу ничего не найдено.", "No objects match your search.");
        EmptyExportLabel.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshCommands();
    }

    private void RefreshPackageLabels()
    {
        if (!initialized) return;
        if (package is null)
        {
            PackageLabel.Text = T("Пакет не выбран", "No package selected");
            PackageDetailsLabel.Text = T("Формат: .histosets-exchange", "Format: .histosets-exchange");
            PreviewWarningLabel.Text = "";
            return;
        }
        PackageLabel.Text = T("База-источник: ", "Source database: ") + package.SourceCatalogName;
        var date = DateTimeOffset.TryParse(package.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
            ? created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : package.CreatedUtc;
        PackageDetailsLabel.Text = T($"Объектов: {package.Materials.Count} · Создан: {date}\n", $"Objects: {package.Materials.Count} · Created: {date}\n")
            + Path.GetFileName(packagePath) + T(" · Источник UUID: ", " · Source UUID: ") + package.SourceCatalogId;
        PackageDetailsLabel.ToolTip = packagePath;
        PreviewWarningLabel.Text = Preview is null ? "" : string.Join("\n", Preview.Warnings);
    }

    private void RefreshDetails()
    {
        if (!initialized) return;
        var row = PreviewList.SelectedItem as PreviewRow;
        DetailsNameLabel.Text = row is null ? T("Выберите строку для сравнения", "Select a row to compare") : row.Name;
        LocalDetails.Text = row?.Item.LocalSummary ?? "—";
        IncomingDetails.Text = row?.Item.IncomingSummary ?? "—";
        ItemWarningLabel.Text = row?.Item.Warning ?? "";
    }

    private void RefreshCommands()
    {
        if (!initialized) return;
        var selected = exportRows.Count(row => row.IsSelected);
        var selectedDeleted = exportRows.Count(row => row.IsSelected && row.Info.IsDeleted);
        SelectionCountLabel.Text = T($"Выбрано: {selected} из {exportRows.Count}", $"Selected: {selected} of {exportRows.Count}")
            + (selectedDeleted == 0 ? "" : T($" · отметок об удалении: {selectedDeleted}", $" · deletion markers: {selectedDeleted}"));
        ExportButton.IsEnabled = !busy && selected > 0;
        SelectAllButton.IsEnabled = !busy && ExportList.Items.Count > 0;
        ClearSelectionButton.IsEnabled = !busy && selected > 0;
        var unresolved = previewRows.Count(row => row.Resolution == ExchangeResolution.Unresolved);
        var conflicts = previewRows.Count(row => row.Item.Status == ExchangeChangeKind.Conflict);
        var deletions = previewRows.Count(row => row.Item.Status == ExchangeChangeKind.Deletion);
        PreviewCountLabel.Text = Preview is null ? "" : T(
            $"Строк: {previewRows.Count} · конфликтов: {conflicts} · удалений: {deletions} · без решения: {unresolved}",
            $"Rows: {previewRows.Count} · conflicts: {conflicts} · deletions: {deletions} · unresolved: {unresolved}");
        EmptyPreviewLabel.Text = T("Откройте пакет, чтобы увидеть план импорта.", "Open a package to see the import plan.");
        EmptyPreviewLabel.Visibility = Preview is null ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = !busy && Preview is not null && previewRows.Count > 0 && unresolved == 0;
        DecisionHint.Text = unresolved > 0
            ? T("Для конфликтов и удалений выберите решение в каждой строке. Изменения ещё не применены.",
                "Choose a decision for every conflict and deletion. Changes have not been applied yet.")
            : T("Перед изменением данных будет создана полная резервная копия. Совпадения определяются по UUID.",
                "A full backup is created before data changes. Matching uses UUIDs.");
    }

    private void SetBusy(bool value)
    {
        busy = value;
        ExchangeTabs.IsEnabled = LanguageButton.IsEnabled = CloseButton.IsEnabled = !value;
        RefreshCommands();
    }

    private void ShowStatus(Func<string> text, bool isError = false)
    {
        statusFactory = text;
        OperationStatus.Text = text();
        OperationStatus.Foreground = isError ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(96, 87, 111));
    }

    private void ShowError(Exception ex)
    {
        ErrorLog.Write(ex);
        ShowStatus(() => T("Операция не завершена. ", "The operation did not complete. ") + UiLanguage.Error(ex), isError: true);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => RefreshExportFilter();
    private async void IncludeDeleted_Changed(object sender, RoutedEventArgs e)
    {
        if (initialized) await ReloadExportIndexAsync();
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in ExportList.Items.OfType<ExportRow>()) row.IsSelected = true;
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in exportRows) row.IsSelected = false;
    }
    private void Preview_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDetails();
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = T("Сохранить пакет обмена", "Save exchange package"),
            Filter = T("Пакет обмена HISTOSETS (*.histosets-exchange)|*.histosets-exchange", "HISTOSETS exchange package (*.histosets-exchange)|*.histosets-exchange"),
            DefaultExt = ".histosets-exchange", AddExtension = true, FileName = "materials-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) == true) await ExportSelectedToPathAsync(dialog.FileName);
    }
    private async void LoadPackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = T("Открыть пакет обмена", "Open exchange package"),
            Filter = T("Пакет обмена HISTOSETS (*.histosets-exchange)|*.histosets-exchange", "HISTOSETS exchange package (*.histosets-exchange)|*.histosets-exchange"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) await LoadPackagePathAsync(dialog.FileName);
    }
    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyImportAsync();
    private void Language_Click(object sender, RoutedEventArgs e)
    {
        try { UiLanguage.Set(UiLanguage.Locale == "ru" ? "en" : "ru"); }
        catch (Exception ex) { ShowError(ex); }
    }

    private sealed record ResolutionOption(ExchangeResolution Value, string Label);

    private abstract class RowView : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class ExportRow(ExchangeMaterialInfo info, Action changed) : RowView
    {
        private bool selected;
        public ExchangeMaterialInfo Info { get; } = info;
        public string Name => LocalizedName(Info.Names, Info.Name);
        public string Identity => Info.Id + T($" · версия {Info.Revision}", $" · revision {Info.Revision}");
        public string StatusLabel => Info.IsDeleted ? T("Удалён", "Deleted") : Info.Status == "ready" ? T("Готов", "Ready") : T("Черновик", "Draft");
        public Brush StatusBrush => Info.IsDeleted ? Brushes.Firebrick : Brushes.DimGray;
        public bool IsSelected
        {
            get => selected;
            set { if (selected == value) return; selected = value; Notify(); changed(); }
        }
        public void RefreshLanguage() { Notify(nameof(Name)); Notify(nameof(Identity)); Notify(nameof(StatusLabel)); }
    }

    private sealed class PreviewRow : RowView
    {
        private ExchangeResolution resolution;
        private readonly IReadOnlyDictionary<string, string>? names;
        private readonly Action changed;
        public ExchangePreviewItem Item { get; }
        public string Name => names is null ? Item.Name : LocalizedName(names, Item.Name);
        public string Identity => Item.Id.ToString();
        public string KindLabel => Item.Kind switch
        {
            ExchangeEntityKind.Material => T("Объект", "Object"),
            ExchangeEntityKind.TagGroup => T("Тип тегов", "Tag type"),
            ExchangeEntityKind.Tag => T("Тег", "Tag"),
            _ => T("Группа аналогов", "Analog group")
        };
        public string StatusLabel => Item.Status switch
        {
            ExchangeChangeKind.Add => T("Новый", "New"),
            ExchangeChangeKind.Unchanged => T("Без изменений", "Unchanged"),
            ExchangeChangeKind.Update => T("Обновление", "Update"),
            ExchangeChangeKind.LocalChanged => T("Изменён локально", "Changed locally"),
            ExchangeChangeKind.Conflict => T("Конфликт", "Conflict"),
            _ => T("Удаление", "Deletion")
        };
        public Brush StatusBrush => Item.Status is ExchangeChangeKind.Conflict or ExchangeChangeKind.Deletion ? Brushes.Firebrick : Brushes.DimGray;
        public IReadOnlyList<ResolutionOption> Options { get; private set; } = [];
        public ExchangeResolution Resolution
        {
            get => resolution;
            set { if (resolution == value) return; resolution = value; Notify(); changed(); }
        }
        public PreviewRow(ExchangePreviewItem item, IReadOnlyDictionary<string, string>? names, Action changed)
        {
            Item = item;
            this.names = names;
            this.changed = changed;
            resolution = item.SuggestedResolution;
            RefreshLanguage();
        }
        public void RefreshLanguage()
        {
            var options = new List<ResolutionOption>();
            if (Item.RequiresDecision) options.Add(new(ExchangeResolution.Unresolved, T("Выберите решение…", "Choose a decision…")));
            if (Item.Status != ExchangeChangeKind.Add) options.Add(new(ExchangeResolution.KeepLocal, T("Сохранить локальное", "Keep local")));
            options.Add(new(ExchangeResolution.UseIncoming, T("Принять пакет", "Use incoming")));
            options.Add(new(ExchangeResolution.Skip, T("Пропустить", "Skip")));
            Options = options;
            Notify(nameof(Name)); Notify(nameof(KindLabel)); Notify(nameof(StatusLabel)); Notify(nameof(Options)); Notify(nameof(Resolution));
        }
    }

    private static string LocalizedName(IReadOnlyDictionary<string, string> names, string fallback)
    {
        var label = CatalogBrowser.Label(names, UiLanguage.Locale);
        return string.IsNullOrWhiteSpace(label) ? fallback : label;
    }
}

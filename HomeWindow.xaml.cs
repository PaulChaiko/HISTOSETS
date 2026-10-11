using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HistOSets.Services;
using HistOSets.Storage;
using Microsoft.Win32;

namespace HistOSets;

public partial class HomeWindow : Window
{
    private readonly string sourceDirectory;
    private readonly bool rememberLocation;
    private readonly string? requestedCatalog;
    private CatalogStore? store;
    private bool busy;
    private bool closed;
    private Func<string>? statusFactory;
    public string? SelectedCatalogDirectory => store?.RootDirectory;

    public HomeWindow(string? sourceDirectory = null, string? catalogDirectory = null, bool rememberLocation = true)
    {
        this.sourceDirectory = Path.GetFullPath(sourceDirectory ?? AppContext.BaseDirectory);
        this.rememberLocation = rememberLocation;
        requestedCatalog = catalogDirectory;
        InitializeComponent();
        UiLanguage.Changed += Language_Changed;
        ApplyLanguage();
        Loaded += Home_Loaded;
        Closing += (_, e) => { if (busy) e.Cancel = true; };
        Closed += (_, _) => { closed = true; UiLanguage.Changed -= Language_Changed; store?.Dispose(); };
    }

    private async void Home_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Home_Loaded;
        try
        {
            var directory = requestedCatalog;
            if (directory is null)
            {
                directory = CatalogLocation.LoadSaved();
                if (directory is null && File.Exists(Path.Combine(CatalogLocation.DefaultDirectory, "catalog.sqlite")))
                    directory = CatalogLocation.DefaultDirectory;
            }
            if (!string.IsNullOrWhiteSpace(directory)) await OpenExisting(directory);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Language_Changed(object? sender, EventArgs e) => ApplyLanguage();
    private void ApplyLanguage()
    {
        ChooseButton.Content = T("Выбрать базу данных…", "Choose database…");
        FolderButton.Content = T("Выбрать папку…", "Choose folder…");
        CreateButton.Content = T("Создать пустую базу данных…", "Create empty database…");
        ViewButton.Content = T("Просмотр", "View");
        SelfTestButton.Content = T("Самопроверка…", "Self-test…");
        EditButton.Content = T("Редактирование", "Edit");
        ExchangeButton.Content = T("Обмен материалами…", "Exchange materials…");
        ManageButton.Content = T("Управление базой и резервные копии…", "Database management and backups…");
        DemoButton.Content = T("Создать учебную демонстрационную базу…", "Create demonstration database…");
        VersionLabel.Text = T("Интерактивный атлас · update7.2 · 0.7.2", "Interactive atlas · update7.2 · 0.7.2");
        LanguageButton.ToolTip = T("Переключить интерфейс и данные на английский", "Switch interface and content to Russian");
        if (statusFactory is not null) StatusText.Text = statusFactory();
        RefreshSummary();
    }

    private static string T(string ru, string en) => UiLanguage.T(ru, en);
    private void RefreshSummary()
    {
        ViewButton.IsEnabled = SelfTestButton.IsEnabled = EditButton.IsEnabled = ExchangeButton.IsEnabled = store is not null;
        DemoButton.Visibility = store is null ? Visibility.Visible : Visibility.Collapsed;
        if (store is null)
        {
            CatalogName.Text = T("База данных не выбрана", "No database selected");
            CatalogStatistics.Text = T("Выберите существующую базу или создайте новую.", "Choose an existing database or create a new one.");
            CatalogModified.Text = "";
            CatalogPath.Text = "";
            return;
        }
        try
        {
            var metadata = store.GetMetadata();
            var statistics = store.GetStatistics();
            CatalogName.Text = metadata.Name;
            CatalogStatistics.Text = T("Объектов: ", "Objects: ") + statistics.Materials;
            CatalogModified.Text = T("Последнее изменение: ", "Last changed: ") + metadata.ModifiedUtc.ToLocalTime()
                .ToString("g", CultureInfo.GetCultureInfo(UiLanguage.Locale == "en" ? "en-US" : "ru-RU"));
            CatalogPath.Text = store.RootDirectory;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowStatus(Func<string> text)
    {
        statusFactory = text;
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = text();
    }
    private void ShowError(Exception ex)
    {
        ErrorLog.Write(ex);
        ShowStatus(() => T("Операция не завершена.", "The operation did not complete.")
            + (store is null ? "" : T(" Текущая база остаётся выбранной.", " The current database remains selected."))
            + "\n" + UiLanguage.Error(ex));
    }

    private async Task InstallSelection(Func<CatalogStore> open)
    {
        if (busy) return;
        busy = true;
        Actions.IsEnabled = LanguageButton.IsEnabled = false;
        ShowStatus(() => T("Открытие базы данных…", "Opening database…"));
        CatalogStore? candidate = null;
        try
        {
            candidate = await Task.Run(() =>
            {
                var result = open();
                try { result.Load(); result.GetMetadata(); return result; }
                catch { result.Dispose(); throw; }
            });
            if (closed) return;
            var previous = store;
            store = candidate;
            candidate = null;
            previous?.Dispose();
            RefreshSummary();
            var migrationBackup = store.LastMigrationBackupPath;
            ShowStatus(() => migrationBackup is not null
                ? T("База обновлена. Резервная копия предыдущей версии:\n", "Database upgraded. Backup of the previous version:\n") + migrationBackup
                : T("База данных готова к просмотру.", "The database is ready to view."));
            if (rememberLocation)
            {
                try { CatalogLocation.Save(store.RootDirectory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ErrorLog.Write(ex);
                    ShowStatus(() => T("База открыта, но её путь не удалось запомнить:\n", "Database opened, but its location could not be remembered:\n") + UiLanguage.Error(ex));
                }
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { candidate?.Dispose(); busy = false; Actions.IsEnabled = LanguageButton.IsEnabled = true; }
    }

    private async Task OpenExisting(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (store is not null && string.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(store.RootDirectory), StringComparison.OrdinalIgnoreCase))
        {
            RefreshSummary();
            ShowStatus(() => T("Эта база уже выбрана.", "This database is already selected."));
            return;
        }
        await InstallSelection(() =>
        {
            if (!File.Exists(Path.Combine(directory, "catalog.sqlite")))
                throw new IOException(T("В выбранной папке нет catalog.sqlite. Найдите базу или восстановите резервную копию; новая база автоматически не создаётся.",
                    "The selected folder contains no catalog.sqlite. Locate the database or restore a backup; it will not be recreated automatically."));
            return new CatalogStore(directory);
        });
    }

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = T("Выберите catalog.sqlite", "Choose catalog.sqlite"),
            Filter = T("База HISTOSETS (catalog.sqlite)|catalog.sqlite", "HISTOSETS database (catalog.sqlite)|catalog.sqlite"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) await OpenExisting(Path.GetDirectoryName(dialog.FileName)!);
    }
    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = T("Выберите папку базы с catalog.sqlite", "Choose a database folder containing catalog.sqlite") };
        if (dialog.ShowDialog(this) == true) await OpenExisting(dialog.FolderName);
    }
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var folder = new OpenFolderDialog { Title = T("Выберите пустую папку для новой базы", "Choose an empty folder for the new database") };
        if (folder.ShowDialog(this) != true) return;
        var name = new CatalogNameDialog(Path.GetFileName(folder.FolderName)) { Owner = this };
        if (name.ShowDialog() != true) return;
        await CreateEmptyCatalogAsync(folder.FolderName, name.CatalogName);
    }

    private Task CreateEmptyCatalogAsync(string directory, string name)
    {
        return InstallSelection(() =>
        {
            RequireEmpty(directory);
            var result = new CatalogStore(directory);
            try { result.RenameCatalog(name); return result; }
            catch { result.Dispose(); throw; }
        });
    }
    private static void RequireEmpty(string directory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException(T("Выберите пустую папку. Существующие файлы сохранены.", "Choose an empty folder. Existing files have been preserved."));
        if (File.Exists(directory)) throw new IOException(T("Вместо папки выбран файл.", "The selected path is a file, not a folder."));
    }
    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
        var folder = new OpenFolderDialog { Title = T("Выберите пустую папку для демонстрационной базы", "Choose an empty folder for the demonstration database") };
        if (folder.ShowDialog(this) != true) return;
        await InstallSelection(() =>
        {
            RequireEmpty(folder.FolderName);
            CatalogStore.CreateFromLegacy(Path.Combine(sourceDirectory, "ATLAS", "ATLAS.xml"), sourceDirectory, folder.FolderName, ImageLoader.ReadMetadata);
            var result = new CatalogStore(folder.FolderName);
            try { result.RenameCatalog(T("Демонстрационный атлас HISTOSETS", "HISTOSETS demonstration atlas")); return result; }
            catch { result.Dispose(); throw; }
        });
    }
    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || busy) return;
        try { new MainWindow(store, ownsStore: false) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { ShowError(ex); }
        RefreshSummary();
    }
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || busy) return;
        try { new EditorCatalogWindow(store) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { ShowError(ex); }
        RefreshSummary();
    }
    private async void Manage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CatalogWindow(store, store?.RootDirectory ?? "", sourceDirectory) { Owner = this };
        dialog.ShowDialog();
        if (dialog.SelectedCatalogDirectory is { } selected) await OpenExisting(selected);
        else RefreshSummary();
    }
    private void Exchange_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || busy) return;
        try { new ExchangeWindow(store) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { ShowError(ex); }
        RefreshSummary();
    }
    private void SelfTest_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || busy) return;
        try { new TrainingSetupWindow(store) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Language_Click(object sender, RoutedEventArgs e)
    {
        try { UiLanguage.Set(UiLanguage.Locale == "ru" ? "en" : "ru"); }
        catch (Exception ex) { ShowError(ex); }
    }
}

internal sealed class CatalogNameDialog : Window
{
    private readonly TextBox name;
    public string CatalogName => name.Text.Trim();
    internal CatalogNameDialog(string initialName)
    {
        Title = UiLanguage.T("Название новой базы", "New database name");
        Width = 470; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 14;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrandSurfaceBrush");
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = UiLanguage.T("Название базы данных", "Database name"), Margin = new Thickness(0, 0, 0, 8) });
        name = new TextBox { Name = "CatalogNameBox", Text = new string(initialName.Where(c => !char.IsControl(c)).Take(200).ToArray()).Trim(), MaxLength = 200 };
        panel.Children.Add(name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var save = new Button { Name = "CreateNameButton", Content = UiLanguage.T("Создать", "Create"), IsDefault = true, IsEnabled = ValidName(name.Text) };
        var cancel = new Button { Content = UiLanguage.T("Отмена", "Cancel"), IsCancel = true };
        name.TextChanged += (_, _) => save.IsEnabled = ValidName(name.Text);
        save.Click += (_, _) => { if (ValidName(CatalogName)) DialogResult = true; };
        buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
    private static bool ValidName(string value) => value.Trim().Length is > 0 and <= 200 && !value.Any(char.IsControl);
}

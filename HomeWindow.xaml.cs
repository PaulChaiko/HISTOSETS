using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HistOSets.Services;
using HistOSets.Storage;
using Microsoft.Win32;

namespace HistOSets;

public partial class HomeWindow : HistOSets.Controls.WorkAreaWindow
{
    private readonly bool rememberLocation;
    private readonly string? requestedCatalog;
    private CatalogStore? store;
    private bool busy;
    private bool closed;
    private Func<string>? statusFactory;
    public string? SelectedCatalogPath => store?.DatabasePath;

    public HomeWindow(string? catalogPath = null, bool rememberLocation = true)
    {
        this.rememberLocation = rememberLocation;
        requestedCatalog = catalogPath;
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
            var path = requestedCatalog ?? (rememberLocation ? CatalogLocation.LoadSaved() : null);
            if (path is null && rememberLocation && File.Exists(Path.Combine(CatalogLocation.DefaultDirectory, "catalog.sqlite")))
                path = CatalogLocation.DefaultDirectory;
            if (string.IsNullOrWhiteSpace(path)) return;
            if (Directory.Exists(path) || string.Equals(Path.GetExtension(path), ".sqlite", StringComparison.OrdinalIgnoreCase))
                ShowStatus(() => T("Найден старый каталог. Чтобы создать файл .histosets, откройте «Управление каталогами» и выберите «Перенести старый каталог…».\n",
                    "An old catalog was found. To create a .histosets file, open Catalog management and select Convert an older catalog.\n") + path);
            else await OpenCatalogAsync(path);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Language_Changed(object? sender, EventArgs e) => ApplyLanguage();
    private void ApplyLanguage()
    {
        ChooseButton.Content = T("Открыть каталог .histosets…", "Open .histosets catalog…");
        CreateButton.Content = T("Создать каталог…", "Create catalog…");
        ViewButton.Content = T("Просмотр", "View");
        SelfTestButton.Content = T("Самопроверка…", "Self-test…");
        EditButton.Content = T("Редактирование", "Edit");
        ExchangeButton.Content = T("Обмен материалами…", "Exchange materials…");
        ManageButton.Content = T("Управление каталогами…", "Catalog management…");
        DemoButton.Content = T("Создать копию демонстрационного каталога…", "Create a copy of the demo catalog…");
        VersionLabel.Text = T("Интерактивный атлас · update8 · 0.8.0", "Interactive atlas · update8 · 0.8.0");
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
            CatalogPath.Text = store.DatabasePath;
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
                try { result.GetMetadata(); return result; }
                catch { result.Dispose(); throw; }
            });
            if (closed) return;
            var previous = store;
            store = candidate;
            candidate = null;
            previous?.Dispose();
            RefreshSummary();
            ShowStatus(() => T("Каталог открыт.", "Catalog opened."));
            if (rememberLocation)
            {
                try { CatalogLocation.Save(store.DatabasePath); }
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

    public async Task OpenCatalogAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (store is not null && string.Equals(path, store.DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            RefreshSummary();
            ShowStatus(() => T("Этот каталог уже открыт.", "This catalog is already open."));
            return;
        }
        await InstallSelection(() => new CatalogStore(path));
    }

    internal static string CatalogFilter => T("Каталог HISTOSETS (*.histosets)|*.histosets", "HISTOSETS catalog (*.histosets)|*.histosets");

    internal static SaveFileDialog CatalogDestination(string name, string title) => new()
    {
        Title = title, Filter = CatalogFilter, FileName = name, DefaultExt = ".histosets",
        AddExtension = true, OverwritePrompt = false
    };

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = T("Открыть каталог", "Open catalog"), Filter = CatalogFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (!CatalogStore.IsNativeCatalog(dialog.FileName) && LooksLikeZip(dialog.FileName))
            {
                var target = CatalogDestination(Path.GetFileNameWithoutExtension(dialog.FileName) + ".converted.histosets",
                    T("Куда перенести старый каталог?", "Where should the converted catalog be saved?"));
                if (target.ShowDialog(this) == true)
                    await InstallSelection(() => { CatalogStore.ConvertLegacyCatalog(dialog.FileName, target.FileName); return new CatalogStore(target.FileName); });
                return;
            }
            await OpenCatalogAsync(dialog.FileName);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static bool LooksLikeZip(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 'P' && stream.ReadByte() == 'K';
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var file = CatalogDestination("Atlas.histosets", T("Новый каталог HISTOSETS", "New HISTOSETS catalog"));
        if (file.ShowDialog(this) != true) return;
        var name = new CatalogNameDialog(Path.GetFileNameWithoutExtension(file.FileName)) { Owner = this };
        if (name.ShowDialog() != true) return;
        await CreateEmptyCatalogAsync(file.FileName, name.CatalogName);
    }

    private Task CreateEmptyCatalogAsync(string path, string name) => InstallSelection(() =>
    {
        CatalogStore.CreateCatalog(path, name);
        return new CatalogStore(path);
    });

    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
        var file = CatalogDestination("Demo.histosets", T("Сохранить демонстрационный каталог", "Save demo catalog"));
        if (file.ShowDialog(this) != true) return;
        await InstallSelection(() =>
        {
            CatalogStore.CopyCatalog(Path.Combine(AppContext.BaseDirectory, "Demo.histosets"), file.FileName);
            return new CatalogStore(file.FileName);
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
        var dialog = new CatalogWindow(store) { Owner = this };
        dialog.ShowDialog();
        if (dialog.SelectedCatalogPath is { } selected) await OpenCatalogAsync(selected);
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

internal sealed class CatalogNameDialog : HistOSets.Controls.WorkAreaWindow
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

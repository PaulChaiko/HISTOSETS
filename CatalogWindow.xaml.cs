using System.IO;
using System.Windows;
using HistOSets.Services;
using HistOSets.Storage;
using Microsoft.Win32;

namespace HistOSets;

public partial class CatalogWindow : HistOSets.Controls.WorkAreaWindow
{
    private readonly CatalogStore? store;
    private bool busy;
    private Func<string>? statusFactory;
    public string? SelectedCatalogPath { get; private set; }

    public CatalogWindow(CatalogStore? store)
    {
        this.store = store;
        InitializeComponent();
        LocationText.Text = store?.DatabasePath ?? "";
        BackupButton.IsEnabled = NamePanel.IsEnabled = store is not null;
        UiLanguage.Changed += Language_Changed;
        ApplyLanguage();
        RefreshStatistics(refreshName: true);
        Closing += (_, e) => { if (busy) e.Cancel = true; };
        Closed += (_, _) => UiLanguage.Changed -= Language_Changed;
    }

    private static string T(string ru, string en) => UiLanguage.T(ru, en);
    private void Language_Changed(object? sender, EventArgs e) => ApplyLanguage();
    private void ApplyLanguage()
    {
        Title = T("Данные атласа — HISTOSETS", "Atlas data — HISTOSETS");
        HeadingText.Text = T("Управление базой HISTOSETS", "HISTOSETS database management");
        NameLabel.Text = T("Название базы данных", "Database name");
        RenameButton.Content = T("Переименовать", "Rename");
        BackupButton.Content = T("Сохранить копию…", "Save a copy…");
        ConvertButton.Content = T("Перенести старый каталог…", "Convert an older catalog…");
        CloseButton.Content = T("Закрыть", "Close");
        HelpText.Text = T(
            "Копия .histosets включает данные и оригиналы изображений. Её можно открыть или отправить как обычный каталог.\n\nДля переноса старого каталога выберите catalog.sqlite из его папки или старый резервный архив .histosets. Программа запишет новый файл, а исходник останется на месте.",
            "A .histosets copy includes the data and original images. Open or share it like any other catalog.\n\nTo convert an older catalog, select its catalog.sqlite or an older .histosets backup. The app creates a new file and keeps the source in place.");
        if (statusFactory is not null) OperationStatus.Text = statusFactory();
        RefreshStatistics();
    }

    private void RefreshStatistics(bool refreshName = false)
    {
        if (store is null)
        {
            StatisticsText.Text = T("Текущий каталог недоступен. Можно открыть другой или восстановить копию.", "No catalog is currently available. Open another catalog or restore a backup.");
            return;
        }
        try
        {
            var s = store.GetStatistics();
            StatisticsText.Text = T($"Объектов: {s.Materials} · Изображений: {s.Images} · Переводов карточек: {s.Translations}\nЭлементов: {s.Elements} · Областей: {s.Regions}",
                $"Objects: {s.Materials} · Images: {s.Images} · Card translations: {s.Translations}\nElements: {s.Elements} · Regions: {s.Regions}");
            if (refreshName) CatalogNameBox.Text = store.GetMetadata().Name;
        }
        catch (Exception ex) { StatisticsText.Text = UiLanguage.Error(ex); }
    }

    private void ShowStatus(Func<string> factory)
    {
        statusFactory = factory;
        OperationPanel.Visibility = Visibility.Visible;
        OperationStatus.Text = factory();
    }
    private async Task Run(Action operation, Func<string> success, string? select = null)
    {
        if (busy) return;
        busy = true;
        Actions.IsEnabled = NamePanel.IsEnabled = CloseButton.IsEnabled = false;
        ShowStatus(() => T("Выполняется операция. Не закрывайте приложение…", "Operation in progress. Please keep the application open…"));
        try
        {
            await Task.Run(operation);
            ShowStatus(success);
            RefreshStatistics(refreshName: true);
            if (select is not null) SelectedCatalogPath = select;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            ShowStatus(() => T("Операция не завершена.\n", "The operation did not complete.\n") + UiLanguage.Error(ex));
        }
        finally { busy = false; Actions.IsEnabled = CloseButton.IsEnabled = true; NamePanel.IsEnabled = store is not null; }
        if (SelectedCatalogPath is not null) Close();
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        var name = CatalogNameBox.Text;
        await Run(() => store.RenameCatalog(name), () => T("Название базы сохранено.", "Database name saved."));
    }
    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        var dialog = HomeWindow.CatalogDestination("HISTOSETS-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".histosets",
            T("Сохранить резервную копию", "Save a backup"));
        if (dialog.ShowDialog(this) == true)
            await Run(() => store.CreateBackup(dialog.FileName), () => T("Копия создана:\n", "Copy created:\n") + dialog.FileName);
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var input = new OpenFileDialog
        {
            Title = T("Выберите прежнюю базу или резервную копию", "Choose an older database or backup"),
            Filter = T("Старый каталог (*.sqlite;*.histosets)|*.sqlite;*.histosets", "Older catalog (*.sqlite;*.histosets)|*.sqlite;*.histosets")
        };
        var previous = CatalogLocation.LoadSaved();
        if (previous is not null && Directory.Exists(previous)) input.InitialDirectory = previous;
        else if (Directory.Exists(CatalogLocation.DefaultDirectory)) input.InitialDirectory = CatalogLocation.DefaultDirectory;
        if (input.ShowDialog(this) != true) return;
        var name = Path.GetFileNameWithoutExtension(input.FileName);
        var destination = HomeWindow.CatalogDestination(name + ".converted.histosets",
            T("Сохранить перенесённый каталог", "Save the converted catalog"));
        if (destination.ShowDialog(this) != true) return;
        await Run(() => CatalogStore.ConvertLegacyCatalog(input.FileName, destination.FileName),
            () => T("Каталог перенесён.", "Catalog converted."), destination.FileName);
    }
}

using System.IO;
using System.Windows;
using HistOSets.Services;
using HistOSets.Storage;
using Microsoft.Win32;

namespace HistOSets;

public partial class CatalogWindow : Window
{
    private readonly CatalogStore? store;
    private readonly string sourceDirectory;
    private bool busy;
    private Func<string>? statusFactory;
    public string? SelectedCatalogDirectory { get; private set; }

    public CatalogWindow(CatalogStore? store, string directory, string sourceDirectory)
    {
        this.store = store;
        this.sourceDirectory = sourceDirectory;
        InitializeComponent();
        LocationText.Text = directory;
        BackupButton.IsEnabled = ImportButton.IsEnabled = NamePanel.IsEnabled = store is not null;
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
        BackupButton.Content = T("Создать резервную копию…", "Create backup…");
        RestoreButton.Content = T("Восстановить копию…", "Restore backup…");
        OpenButton.Content = T("Открыть каталог…", "Open catalog…");
        CreateXmlButton.Content = T("Создать из XML…", "Create from XML…");
        ImportButton.Content = T("Повторить импорт XML…", "Repeat XML import…");
        CloseButton.Content = T("Закрыть", "Close");
        HelpText.Text = T(
            "Резервная копия включает базу, изображения и исходный XML, если он был импортирован. Для восстановления выберите пустую папку: текущий каталог сохранится.\n\nИзменение исходного XML не меняет базу автоматически. Повтор того же импорта не создаёт дубликатов. Для другой версии XML создайте отдельный каталог.\n\nПри выборе XML рядом с его папкой ATLAS должна находиться папка SPECIMENS с изображениями. Новую пустую базу можно создать на стартовом экране.",
            "A backup includes the database, images, and source XML when imported. Restore into an empty folder; the current catalog will remain intact.\n\nEditing the source XML does not change the database automatically. Repeating the same import creates no duplicates. Create a separate catalog for another XML version.\n\nWhen choosing an XML file, its ATLAS folder should have a sibling SPECIMENS folder containing the images. An empty database can be created on the home screen.");
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
            if (select is not null) SelectedCatalogDirectory = select;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            ShowStatus(() => T("Операция не завершена.\n", "The operation did not complete.\n") + UiLanguage.Error(ex));
        }
        finally { busy = false; Actions.IsEnabled = CloseButton.IsEnabled = true; NamePanel.IsEnabled = store is not null; }
        if (SelectedCatalogDirectory is not null) Close();
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
        var dialog = new SaveFileDialog
        {
            Title = T("Сохранить резервную копию", "Save backup"),
            Filter = T("Каталог HISTOSETS (*.histosets)|*.histosets", "HISTOSETS catalog (*.histosets)|*.histosets"),
            FileName = "HISTOSETS-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".histosets", AddExtension = true
        };
        if (dialog.ShowDialog(this) == true)
            await Run(() => store.CreateBackup(dialog.FileName), () => T("Резервная копия создана:\n", "Backup created:\n") + dialog.FileName);
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var file = new OpenFileDialog { Title = T("Выберите резервную копию", "Choose backup"), Filter = T("Каталог HISTOSETS (*.histosets)|*.histosets", "HISTOSETS catalog (*.histosets)|*.histosets") };
        if (file.ShowDialog(this) != true) return;
        var folder = new OpenFolderDialog { Title = T("Выберите пустую папку для восстановленного каталога", "Choose an empty folder for the restored catalog") };
        if (folder.ShowDialog(this) != true) return;
        await Run(() => CatalogStore.RestoreBackup(file.FileName, folder.FolderName), () => T("Каталог восстановлен.", "Catalog restored."), folder.FolderName);
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var folder = new OpenFolderDialog { Title = T("Выберите папку с catalog.sqlite", "Choose a folder containing catalog.sqlite") };
        if (folder.ShowDialog(this) != true) return;
        if (store is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.FolderName)),
                Path.TrimEndingDirectorySeparator(store.RootDirectory), StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus(() => T("Этот каталог уже открыт.", "This catalog is already open."));
            return;
        }
        await Run(() =>
        {
            if (!File.Exists(Path.Combine(folder.FolderName, "catalog.sqlite")))
                throw new IOException(T("В выбранной папке нет catalog.sqlite.", "The selected folder contains no catalog.sqlite."));
            using var candidate = new CatalogStore(folder.FolderName);
            candidate.Load();
        }, () => T("Каталог открыт.", "Catalog opened."), folder.FolderName);
    }
    private OpenFileDialog XmlDialog() => new()
    {
        Title = T("Выберите исходный XML атласа", "Choose the source atlas XML"),
        Filter = T("Каталог XML (*.xml)|*.xml", "XML catalog (*.xml)|*.xml"),
        InitialDirectory = Path.Combine(sourceDirectory, "ATLAS")
    };
    private static string SourceRoot(string xml)
    {
        var directory = Path.GetDirectoryName(xml)!;
        return string.Equals(Path.GetFileName(directory), "ATLAS", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory)! : directory;
    }
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var file = XmlDialog();
        if (file.ShowDialog(this) != true) return;
        var folder = new OpenFolderDialog { Title = T("Выберите пустую папку для нового каталога", "Choose an empty folder for the new catalog") };
        if (folder.ShowDialog(this) != true) return;
        await Run(() => CatalogStore.CreateFromLegacy(file.FileName, SourceRoot(file.FileName), folder.FolderName, ImageLoader.ReadMetadata),
            () => T("Каталог создан.", "Catalog created."), folder.FolderName);
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        var file = XmlDialog();
        if (file.ShowDialog(this) != true) return;
        ImportResult? result = null;
        await Run(() => result = store.ImportLegacy(file.FileName, SourceRoot(file.FileName), ImageLoader.ReadMetadata),
            () => T("Проверка импорта завершена.", "Import check completed."));
        if (result is not null) ShowStatus(() => result.AlreadyImported
            ? T("Этот XML и изображения уже импортированы. Дубликаты не созданы.", "This XML and its images have already been imported. No duplicates were created.")
            : T("Импорт выполнен. Закройте это окно, чтобы увидеть объекты.", "Import completed. Close this window to view the objects."));
    }
}

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
    public string? SelectedCatalogDirectory { get; private set; }

    public CatalogWindow(CatalogStore? store, string directory, string sourceDirectory)
    {
        this.store = store;
        this.sourceDirectory = sourceDirectory;
        InitializeComponent();
        LocationText.Text = directory;
        BackupButton.IsEnabled = ImportButton.IsEnabled = store is not null;
        if (store is not null)
        {
            try
            {
                var s = store.GetStatistics();
                StatisticsText.Text = $"Материалов: {s.Materials} · Изображений: {s.Images} · Языковых записей: {s.Translations}\nЭлементов: {s.Elements} · Общих контуров: {s.Regions}";
            }
            catch (Exception ex) { StatisticsText.Text = ex.Message; }
        }
        else StatisticsText.Text = "Текущий каталог недоступен. Можно открыть другой или восстановить копию.";
        Closing += (_, e) => { if (busy) e.Cancel = true; };
    }

    private async Task Run(Action operation, string success, string? select = null)
    {
        busy = true;
        Actions.IsEnabled = CloseButton.IsEnabled = false;
        OperationPanel.Visibility = Visibility.Visible;
        OperationStatus.Text = "Выполняется операция. Не закрывайте приложение…";
        try
        {
            await Task.Run(operation);
            OperationStatus.Text = success;
            if (select is not null) SelectedCatalogDirectory = select;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            OperationStatus.Text = "Операция не завершена.\n" + ex.Message;
        }
        finally { busy = false; Actions.IsEnabled = CloseButton.IsEnabled = true; }
        if (SelectedCatalogDirectory is not null) Close();
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить резервную копию", Filter = "Каталог HISTOSETS (*.histosets)|*.histosets",
            FileName = "HISTOSETS-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".histosets", AddExtension = true
        };
        if (dialog.ShowDialog(this) == true)
            await Run(() => store.CreateBackup(dialog.FileName), "Резервная копия создана:\n" + dialog.FileName);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var file = new OpenFileDialog { Title = "Выберите резервную копию", Filter = "Каталог HISTOSETS (*.histosets)|*.histosets" };
        if (file.ShowDialog(this) != true) return;
        var folder = new OpenFolderDialog { Title = "Выберите пустую папку для восстановленного каталога" };
        if (folder.ShowDialog(this) != true) return;
        await Run(() => CatalogStore.RestoreBackup(file.FileName, folder.FolderName), "Каталог восстановлен.", folder.FolderName);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var folder = new OpenFolderDialog { Title = "Выберите папку с catalog.sqlite" };
        if (folder.ShowDialog(this) != true) return;
        if (store is not null && string.Equals(Path.GetFullPath(folder.FolderName), store.RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            OperationPanel.Visibility = Visibility.Visible;
            OperationStatus.Text = "Этот каталог уже открыт.";
            return;
        }
        await Run(() =>
        {
            if (!File.Exists(Path.Combine(folder.FolderName, "catalog.sqlite"))) throw new CatalogStorageException("В выбранной папке нет catalog.sqlite.");
            using var candidate = new CatalogStore(folder.FolderName);
            candidate.Load();
        }, "Каталог открыт.", folder.FolderName);
    }

    private OpenFileDialog XmlDialog() => new()
    {
        Title = "Выберите исходный XML атласа", Filter = "Каталог XML (*.xml)|*.xml",
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
        var folder = new OpenFolderDialog { Title = "Выберите пустую папку для нового каталога" };
        if (folder.ShowDialog(this) != true) return;
        await Run(() => CatalogStore.CreateFromLegacy(file.FileName, SourceRoot(file.FileName), folder.FolderName, ImageLoader.ReadMetadata),
            "Каталог создан.", folder.FolderName);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        var file = XmlDialog();
        if (file.ShowDialog(this) != true) return;
        ImportResult? result = null;
        await Run(() => result = store.ImportLegacy(file.FileName, SourceRoot(file.FileName), ImageLoader.ReadMetadata), "Проверка импорта завершена.");
        if (result is not null) OperationStatus.Text = result.AlreadyImported
            ? "Этот XML и изображения уже импортированы. Дубликаты не созданы."
            : "Импорт выполнен. Закройте это окно, чтобы увидеть материалы.";
    }
}

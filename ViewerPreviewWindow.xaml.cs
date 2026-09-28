using System.IO;
using System.Text.Json;
using System.Windows;
using HistOSets.Core;
using HistOSets.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace HistOSets;

public partial class ViewerPreviewWindow : Window
{
    private const string ViewerHost = "viewer.histosets.local";
    private readonly AtlasSpecimen specimen;
    private readonly LoadedImage image;
    private readonly string dataDirectory;
    private bool closed;
    private bool ready;
    private string? pyramidHost;

    public ViewerPreviewWindow(AtlasSpecimen specimen, LoadedImage image, string dataDirectory)
    {
        this.specimen = specimen;
        this.image = image;
        this.dataDirectory = dataDirectory;
        InitializeComponent();
        Title = "HISTOSETS — " + specimen.Name;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var viewerDirectory = Path.Combine(AppContext.BaseDirectory, "Viewer");
            if (!File.Exists(Path.Combine(viewerDirectory, "index.html")))
                throw new FileNotFoundException("Не найдена папка Viewer. Распакуйте архив приложения целиком.");
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HISTOSETS", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: cache);
            if (closed) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (closed) return;
            var core = Browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping(ViewerHost, viewerDirectory, CoreWebView2HostResourceAccessKind.Deny);
            core.SetVirtualHostNameToFolderMapping("media.histosets.local", Path.Combine(dataDirectory, "SPECIMENS"), CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != ViewerHost)
                    args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += MessageReceived;
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess && !closed) ViewerStatus.Text = "Не удалось загрузить просмотрщик: " + args.WebErrorStatus;
            };
            Browser.Source = new Uri($"https://{ViewerHost}/index.html");
        }
        catch (Exception ex)
        {
            if (closed) return;
            ErrorLog.Write(ex);
            ViewerStatus.Text = ex is WebView2RuntimeNotFoundException
                ? "Для нового просмотрщика установите Microsoft Edge WebView2 Runtime. Основное окно атласа доступно без него."
                : "Не удалось запустить новый просмотрщик: " + ex.Message;
        }
    }

    private void MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (closed || !e.Source.StartsWith($"https://{ViewerHost}/", StringComparison.Ordinal)) return;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var type = message.RootElement.GetProperty("type").GetString();
            if (type == "ready" && !ready)
            {
                ready = true;
                DziButton.IsEnabled = true;
                var relative = Path.GetRelativePath(Path.Combine(dataDirectory, "SPECIMENS"), specimen.ImagePath);
                var url = "https://media.histosets.local/" + EncodePath(relative);
                Send(new
                {
                    type = "load", title = specimen.Name,
                    tileSource = new { type = "image", url },
                    elements = specimen.Elements.Select((element, i) => new
                    {
                        id = "element-" + i, name = element.Name,
                        polygons = element.Polygons.Select(polygon => polygon.Points.Select(p =>
                        {
                            var point = ImageCoordinates.ToPixels(p, specimen.CoordinateSpace, image.DpiX, image.DpiY);
                            return new[] { point.X, point.Y };
                        }))
                    })
                });
            }
            else if (type == "loaded") ViewerStatus.Text = "Колесо мыши — масштаб. Перетаскивание — перемещение. Выберите элемент справа.";
            else if (type == "error") ViewerStatus.Text = "Изображение недоступно. Проверьте файл и содержимое папки пирамиды.";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            ErrorLog.Write(ex);
            ViewerStatus.Text = "Не удалось прочитать ответ просмотрщика.";
        }
    }

    private static string EncodePath(string path) => string.Join("/", path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
    private void Send(object payload) => Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));

    private void Dzi_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Открыть пирамиду изображения", Filter = "Deep Zoom Image (*.dzi)|*.dzi", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (pyramidHost is not null) Browser.CoreWebView2.ClearVirtualHostNameToFolderMapping(pyramidHost);
            // A new origin prevents cached tiles from a different folder with the same DZI filename.
            pyramidHost = Guid.NewGuid().ToString("N") + ".sample.histosets.local";
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(pyramidHost, Path.GetDirectoryName(dialog.FileName)!, CoreWebView2HostResourceAccessKind.Allow);
            Send(new
            {
                type = "load", title = Path.GetFileNameWithoutExtension(dialog.FileName),
                tileSource = "https://" + pyramidHost + "/" + Uri.EscapeDataString(Path.GetFileName(dialog.FileName)),
                elements = Array.Empty<object>()
            });
            ViewerStatus.Text = "Открывается пирамида DZI. Исходные препараты доступны в основном окне.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ErrorLog.Write(ex);
            ViewerStatus.Text = "Не удалось открыть пирамиду: " + ex.Message;
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        closed = true;
        Browser.Dispose();
    }
}

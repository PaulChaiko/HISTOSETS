using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HistOSets;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        var output = System.IO.Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts");
        Directory.CreateDirectory(output);
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "histosets-smoke-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        var previousDirectory = Environment.CurrentDirectory;
        try
        {
            // Reproduce launching a portable build from a shortcut with another working folder.
            Environment.CurrentDirectory = temp;
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var catalogRoot = System.IO.Path.Combine(temp, "catalog");
            var window = new MainWindow(AppContext.BaseDirectory, catalogRoot);
            window.Show();
            Pump();
            var specimens = (ListBox)window.FindName("ListOfSpecimens");
            var elements = (ListBox)window.FindName("ListOfElements");
            var image = (Image)window.FindName("Specimen");
            var canvas = (Canvas)window.FindName("Desk");
            var welcome = (Image)window.FindName("WelcomeLogo");
            Check(specimens.Items.Count == 5, "Portable XML imports from application directory into SQLite. Status: " + ((TextBlock)window.FindName("StatusText")).Text);
            Check(File.Exists(System.IO.Path.Combine(catalogRoot, "catalog.sqlite")), "SQLite database created in the chosen writable directory");
            var legacy = AtlasLoader.Load(AppContext.BaseDirectory);
            Check(specimens.SelectedIndex == -1 && image.Source is null && welcome.Source is not null && welcome.IsVisible,
                "Startup displays the original logo until a specimen is selected");
            Check(!((Button)window.FindName("PreviewButton")).IsEnabled, "Preview requires a selected specimen");
            SaveScreenshot(window, System.IO.Path.Combine(output, "native-welcome.png"));
            var regions = 0;
            var language = (ComboBox)window.FindName("LanguageSelector");
            foreach (var languageIndex in new[] { 0, 1 })
            {
            language.SelectedIndex = languageIndex;
            foreach (AtlasSpecimen specimen in specimens.Items.Cast<AtlasSpecimen>().ToArray())
            {
                Console.WriteLine("Selecting " + specimen.Name + " index=" + specimens.Items.IndexOf(specimen));
                specimens.SelectedItem = specimen;
                Console.WriteLine("Selected=" + (specimens.SelectedItem as AtlasSpecimen)?.Name + " images=" + ((ComboBox)window.FindName("ImageSelector")).Items.Count + " status=" + ((TextBlock)window.FindName("ImageMessage")).Text);
                WaitImage(window);
                Check(image.Source is not null, "Image: " + specimen.Name);
                Check(canvas.Children.OfType<Polygon>().Count() == 0, "No stale contours after image switch");
                foreach (AtlasElement element in elements.Items)
                {
                    elements.SelectedItem = element;
                    var polygons = canvas.Children.OfType<Polygon>().ToArray();
                    Check(polygons.Length == element.Polygons.Count, "Contours: " + element.Name);
                    regions += polygons.Length;
                    if (canvas.Width == 2592 && polygons.Length > 0)
                    {
                        var stored = element.Polygons[0].Points[0];
                        var original = legacy.Specimens.Single(s => s.Name == specimen.Name).Elements.Single(e => e.Name == element.Name).Polygons[0].Points[0];
                        var actual = polygons[0].Points[0];
                        Check(specimen.CoordinateSpace == CoordinateSpace.Pixels && Math.Abs(actual.X - original.X * 25) < .001 && Math.Abs(actual.Y - original.Y * 25) < .001 && actual.X == stored.X && actual.Y == stored.Y,
                            "WIC 2400 DPI is applied once at import; native view uses persisted pixels");
                    }
                }
            }
            }
            Check(regions == 66, "All 66 baseline polygons rendered");
            specimens.SelectedIndex = -1;
            Check(welcome.IsVisible && image.Source is null && canvas.Children.OfType<Polygon>().Count() == 0,
                "Clearing selection restores logo without stale image or contours");
            specimens.SelectedIndex = 0;
            WaitImage(window);
            elements.SelectedIndex = 0;
            Pump();
            Check(!welcome.IsVisible, "Logo does not cover the selected specimen");
            SaveScreenshot(window, System.IO.Path.Combine(output, "native-view.png"));
            var materialId = ((AtlasSpecimen)specimens.SelectedItem).MaterialId;
            var elementId = ((AtlasElement)elements.SelectedItem).Id;
            language.SelectedIndex = 0;
            WaitImage(window);
            Check(((AtlasSpecimen)specimens.SelectedItem).MaterialId == materialId && ((AtlasElement)elements.SelectedItem).Id == elementId, "Language switch preserves selected material and element");
            var search = (TextBox)window.FindName("SearchBox");
            search.Text = "CORNEA";
            WaitImage(window);
            Check(specimens.Items.Count == 2 && specimens.Items.Cast<AtlasSpecimen>().All(s => s.Locale == "ru"), "English search finds Russian material cards");
            search.Text = "Несуществующий препарат";
            Check(specimens.Items.Count == 0 && image.Source is null && canvas.Children.OfType<Polygon>().Count() == 0 && !((Button)window.FindName("PreviewButton")).IsEnabled, "Empty search clears the prior image, elements and contours");
            search.Clear();
            foreach (var index in new[] { 0, 1, 2, 3, 4, 0 }) specimens.SelectedIndex = index;
            WaitImage(window);
            Check(((AtlasSpecimen)specimens.SelectedItem).MaterialId == materialId && image.Source is BitmapSource loadedLatest && loadedLatest.PixelWidth == 1968, "Rapid selection displays only the last requested image");
            elements.SelectedIndex = 0;

            var selected = (AtlasSpecimen)specimens.SelectedItem;
            var loaded = ImageLoader.Load(selected.ImagePath);
            using (File.Open(selected.ImagePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(true, "Image stream released after loading");
            var preview = new ViewerPreviewWindow(selected, loaded, catalogRoot) { Owner = window };
            preview.Show();
            var browser = (WebView2)preview.FindName("Browser");
            var deadline = DateTime.UtcNow.AddSeconds(45);
            var viewerReady = false;
            while (DateTime.UtcNow < deadline && !viewerReady)
            {
                Pump();
                if (browser.CoreWebView2 is not null)
                {
                    var query = browser.ExecuteScriptAsync("window.histosetsPreview ? JSON.stringify(window.histosetsPreview.snapshot()) : null");
                    while (!query.IsCompleted && DateTime.UtcNow < deadline) Pump();
                    if (query.IsCompletedSuccessfully && query.Result != "null")
                    {
                        var json = JsonSerializer.Deserialize<string>(query.Result);
                        if (json is not null)
                        {
                            using var state = JsonDocument.Parse(json);
                            viewerReady = state.RootElement.GetProperty("loaded").GetBoolean();
                            if (viewerReady)
                                Check(state.RootElement.GetProperty("annotations").GetArrayLength() == selected.Elements.Sum(e => e.Polygons.Count),
                                    "WebView2 local assets and annotation bridge");
                        }
                    }
                }
            }
            Check(viewerReady, "WebView2 image loads offline from virtual host");
            preview.Close();
            window.Close();

            var backupPath = System.IO.Path.Combine(temp, "native-backup.histosets");
            using (var data = new CatalogStore(catalogRoot))
            {
                var statistics = data.GetStatistics();
                Check(statistics == new CatalogStatistics(5, 5, 10, 27, 33), "WIC import shares verified translations and geometry");
                var management = new CatalogWindow(data, catalogRoot, AppContext.BaseDirectory);
                management.Show();
                Pump();
                Check(((Button)management.FindName("BackupButton")).IsEnabled, "Data management offers a full backup");
                SaveScreenshot(management, System.IO.Path.Combine(output, "native-data.png"));
                management.Close();
                var tagWindow = new TagsWindow(data, selected.MaterialId, selected.Name, "ru");
                tagWindow.Show();
                Pump();
                EditDictionaryDialog(tagWindow, "Создать…", "Тема", "Topic");
                EditDictionaryDialog(tagWindow, "Создать тег…", "Эпителий", "Epithelium");
                var tagList = (ListBox)tagWindow.FindName("TagList");
                tagList.UpdateLayout();
                var checkBox = VisualChildren<CheckBox>(tagList).Single();
                checkBox.IsChecked = true;
                Pump();
                SaveScreenshot(tagWindow, System.IO.Path.Combine(output, "native-tags.png"));
                ((Button)tagWindow.FindName("SaveAssignments")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(data.LoadTaxonomy().MaterialTags[selected.MaterialId!.Value].Count == 1, "Native dialog creates translated group/tag and saves material assignments");
                data.CreateBackup(backupPath);
            }
            var filtered = new MainWindow(AppContext.BaseDirectory, catalogRoot);
            filtered.Show();
            Pump();
            ((Expander)filtered.FindName("TagFilterExpander")).IsExpanded = true;
            filtered.UpdateLayout();
            var filterBox = ((StackPanel)filtered.FindName("TagFilters")).Children.OfType<CheckBox>().Single();
            filterBox.IsChecked = true;
            var filteredList = (ListBox)filtered.FindName("ListOfSpecimens");
            Check(filteredList.Items.Count == 1 && ((AtlasSpecimen)filteredList.Items[0]).MaterialId == selected.MaterialId, "Saved tag filters the native catalog after restart");
            filteredList.SelectedIndex = 0;
            WaitImage(filtered);
            SaveScreenshot(filtered, System.IO.Path.Combine(output, "native-catalog-filter.png"));
            filtered.Close();
            File.Delete(selected.ImagePath);
            var damagedMedia = new MainWindow(AppContext.BaseDirectory, catalogRoot);
            ((ListBox)damagedMedia.FindName("ListOfSpecimens")).SelectedIndex = 0;
            Check(((TextBlock)damagedMedia.FindName("ImageMessage")).Text == "Изображение недоступно", "Missing managed image leaves window usable");
            Check(!((Button)damagedMedia.FindName("PreviewButton")).IsEnabled, "Missing managed image cannot open preview");
            damagedMedia.Close();
            var restoredRoot = System.IO.Path.Combine(temp, "restored");
            CatalogStore.RestoreBackup(backupPath, restoredRoot);
            var withoutSource = new MainWindow(temp, restoredRoot);
            Check(((ListBox)withoutSource.FindName("ListOfSpecimens")).Items.Count == 5, "Restored database opens with no XML at the application source path");
            ((ListBox)withoutSource.FindName("ListOfSpecimens")).SelectedIndex = 0;
            WaitImage(withoutSource);
            Check(((Image)withoutSource.FindName("Specimen")).Source is not null, "Restored managed image renders");
            withoutSource.Close();

            var backgroundImport = System.IO.Path.Combine(temp, "background-import");
            Task.Run(() => CatalogStore.CreateFromLegacy(System.IO.Path.Combine(AppContext.BaseDirectory, "ATLAS", "ATLAS.xml"),
                AppContext.BaseDirectory, backgroundImport, ImageLoader.ReadMetadata)).GetAwaiter().GetResult();
            using (var created = new CatalogStore(backgroundImport))
                Check(created.GetStatistics() == new CatalogStatistics(5, 5, 10, 27, 33), "Data-window background WIC import creates a complete separate catalog");

            Directory.CreateDirectory(System.IO.Path.Combine(temp, "ATLAS"));
            Directory.CreateDirectory(System.IO.Path.Combine(temp, "SPECIMENS"));
            var xml = System.IO.Path.Combine(temp, "ATLAS", "ATLAS.xml");
            File.WriteAllText(xml, "<ATLAS><Specimen NAME='Missing' IMAGE='missing.jpg'/></ATLAS>");
            var brokenCatalog = System.IO.Path.Combine(temp, "failed-import");
            var broken = new MainWindow(temp, brokenCatalog);
            ((ListBox)broken.FindName("ListOfSpecimens")).SelectedIndex = 0;
            Check(((TextBlock)broken.FindName("CatalogCount")).Text == "Каталог недоступен", "Missing source image aborts migration with recovery UI");
            Check(!((Button)broken.FindName("PreviewButton")).IsEnabled, "Failed migration cannot open preview");
            broken.Close();
            File.WriteAllText(xml, "<ATLAS><Specimen");
            broken = new MainWindow(temp, brokenCatalog);
            Check(((TextBlock)broken.FindName("CatalogCount")).Text == "Каталог недоступен", "Malformed catalog is recoverable");
            broken.Close();
            app.Shutdown();
            Console.WriteLine($"{checks} Windows checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (Application.Current is { } current)
            {
                foreach (Window open in current.Windows)
                    if (open is MainWindow)
                    {
                        Console.Error.WriteLine(((TextBlock)open.FindName("StatusText")).Text);
                        SaveScreenshot(open, System.IO.Path.Combine(output, "native-failure.png"));
                    }
            }
            var logs = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HISTOSETS", "Logs");
            if (Directory.Exists(logs))
                foreach (var log in Directory.GetFiles(logs)) Console.Error.WriteLine(File.ReadAllText(log));
            return 1;
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            if (Application.Current is { } current)
                foreach (var open in current.Windows.Cast<Window>().ToArray()) open.Close();
            Directory.Delete(temp, true);
        }
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualChildren<T>(child)) yield return nested;
        }
    }
    private static void EditDictionaryDialog(TagsWindow owner, string buttonTitle, string russian, string english)
    {
        owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().Single(w => w.GetType().Name == "TaxonomyEditWindow");
            dialog.UpdateLayout();
            var fields = VisualChildren<TextBox>(dialog).ToArray();
            fields[0].Text = russian;
            fields[1].Text = english;
            VisualChildren<Button>(dialog).Single(b => Equals(b.Content, "Сохранить")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        VisualChildren<Button>(owner).Single(b => Equals(b.Content, buttonTitle)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }
    private static void WaitImage(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!((Button)window.FindName("PreviewButton")).IsEnabled && ((TextBlock)window.FindName("ImageMessage")).Text != "Изображение недоступно" && DateTime.UtcNow < deadline) Pump();
        if (!((Button)window.FindName("PreviewButton")).IsEnabled) throw new Exception("Image did not load: " + ((TextBlock)window.FindName("StatusText")).Text);
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Thread.Sleep(20);
    }
    private static void SaveScreenshot(Window window, string path)
    {
        if (!window.IsVisible || window.ActualWidth < 1 || window.ActualHeight < 1) return;
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

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

internal static partial class Program
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
        var previousSettingsDirectory = Environment.GetEnvironmentVariable("HISTOSETS_SETTINGS_DIRECTORY");
        try
        {
            // Never read or overwrite the developer's selected catalog or language.
            Environment.SetEnvironmentVariable("HISTOSETS_SETTINGS_DIRECTORY", System.IO.Path.Combine(temp, "settings"));
            // Reproduce launching a portable build from a shortcut with another working folder.
            Environment.CurrentDirectory = temp;
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Programmatic control changes below run outside Dispatcher.PushFrame.
            // Keep async UI continuations on the dispatcher just as real clicks do.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var catalogRoot = System.IO.Path.Combine(temp, "catalog");
            var window = new MainWindow(AppContext.BaseDirectory, catalogRoot);
            // Supply the startup window before pumping the dispatcher, so App
            // uses only this isolated catalog. The published EXE is tested separately.
            app.MainWindow = window;
            window.Show();
            Pump();
            Check(app.Windows.Count == 1, "Smoke harness opens only its isolated catalog window");
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
                specimens.SelectedItem = specimen;
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
            Check(specimens.Items.Cast<AtlasSpecimen>().All(s => s.IsPrimaryImage), "Each object card opens its designated primary image");
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
            var contourPoints = canvas.Children.OfType<Polygon>().Select(p => p.Points.ToArray()).ToArray();
            Click(window, "ActualSizeButton");
            var imageScale = (ScaleTransform)window.FindName("ImageScale");
            Check(Math.Abs(imageScale.ScaleX - 1) < .001 && Math.Abs(imageScale.ScaleY - 1) < .001, "100 percent displays one image pixel per logical display unit");
            Click(window, "ZoomInButton");
            Check(imageScale.ScaleX > 1 && ((Border)window.FindName("OverviewBorder")).IsVisible, "Zoom exposes a navigation overview for the enlarged image");
            var outline = (CheckBox)window.FindName("OutlineToggle");
            outline.IsChecked = false;
            Check(!canvas.Children.OfType<Polygon>().Any(p => p.IsVisible), "Outline switch hides selected contours");
            outline.IsChecked = true;
            ((Slider)window.FindName("OutlineOpacity")).Value = .7;
            ((Slider)window.FindName("OutlineHue")).Value = 0;
            var presentedContours = canvas.Children.OfType<Polygon>().ToArray();
            Check(presentedContours.Length == contourPoints.Length && presentedContours.Where((p, i) => !p.Points.SequenceEqual(contourPoints[i])).Any() == false
                && presentedContours.All(p => p.IsVisible) && ((AtlasElement)elements.SelectedItem).Id == elementId,
                "Zoom and contour presentation retain original coordinates and selected element");
            Click(window, "FitButton");
            Check(imageScale.ScaleX > 0 && imageScale.ScaleX < 1, "Fit returns the baseline image to the available viewport");

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

            var home = new HomeWindow(sourceDirectory: null, catalogDirectory: "", rememberLocation: false);
            home.Show();
            Pump();
            Check(home.SelectedCatalogDirectory is null && !((Button)home.FindName("ViewButton")).IsEnabled,
                "Home starts with no selected database and cannot open the viewer");
            Check(!((Button)home.FindName("SelfTestButton")).IsEnabled && !((Button)home.FindName("EditButton")).IsEnabled,
                "Home requires a selected database for editing and keeps unfinished self-test disabled");
            Click(home, "LanguageButton");
            Check(Equals(((Button)home.FindName("ViewButton")).Content, "View")
                && ((TextBlock)home.FindName("CatalogName")).Text == "No database selected"
                && File.ReadAllText(System.IO.Path.Combine(temp, "settings", "language.txt")).Trim() == "en",
                "Language switch updates home UI and persists the preference outside the catalog");
            SaveScreenshot(home, System.IO.Path.Combine(output, "native-home-empty.png"));
            home.Close();

            home = new HomeWindow(sourceDirectory: null, catalogDirectory: catalogRoot, rememberLocation: false);
            home.Show();
            WaitHome(home);
            Check(Equals(((Button)home.FindName("ViewButton")).Content, "View")
                && home.SelectedCatalogDirectory == catalogRoot
                && ((TextBlock)home.FindName("CatalogStatistics")).Text == "Objects: 5"
                && !string.IsNullOrWhiteSpace(((TextBlock)home.FindName("CatalogModified")).Text),
                "Home reopens the chosen catalog with its object count, modification date and selected UI language");
            var borrowedViewerReady = false;
            home.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var borrowedViewer = Application.Current.Windows.OfType<MainWindow>().SingleOrDefault();
                if (borrowedViewer is not null)
                {
                    var borrowedSpecimens = (ListBox)borrowedViewer.FindName("ListOfSpecimens");
                    borrowedViewerReady = borrowedSpecimens.Items.Count == 5
                        && ((ComboBox)borrowedViewer.FindName("LanguageSelector")).SelectedIndex == 1;
                    borrowedViewer.Close();
                }
            }));
            Click(home, "ViewButton");
            Check(borrowedViewerReady, "Viewer opened from home uses the shared catalog and global language");
            Check(((Button)home.FindName("ViewButton")).IsEnabled && ((TextBlock)home.FindName("CatalogStatistics")).Text == "Objects: 5",
                "Closing a borrowed viewer leaves the home catalog open and readable");
            Click(home, "LanguageButton");
            SaveScreenshot(home, System.IO.Path.Combine(output, "native-home-catalog.png"));
            home.Close();

            var emptyRoot = System.IO.Path.Combine(temp, "empty-catalog");
            using (var empty = new CatalogStore(emptyRoot)) empty.RenameCatalog("Пустая учебная база");
            home = new HomeWindow(sourceDirectory: null, catalogDirectory: emptyRoot, rememberLocation: false);
            home.Show();
            WaitHome(home);
            Check(((TextBlock)home.FindName("CatalogName")).Text == "Пустая учебная база"
                && ((TextBlock)home.FindName("CatalogStatistics")).Text == "Объектов: 0",
                "A newly created empty catalog opens without importing demonstration objects");
            Check(((Button)home.FindName("EditButton")).IsEnabled && !((Button)home.FindName("SelfTestButton")).IsEnabled,
                "Selected empty database enables the object editor while self-test remains unavailable");
            var edited = CheckEditorWorkflow(home, selected.ImagePath, output);
            home.Close();
            CheckSavedEditorObject(emptyRoot, output, edited);
            CheckAdvancedEditorWorkflow(temp, output);
            CheckExchangeWorkflow(temp, output);

            var backupPath = System.IO.Path.Combine(temp, "native-backup.histosets");
            Guid topicId;
            Guid assignedTagId;
            Guid analogGroupId;
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
                topicId = data.LoadTaxonomy().Groups.Single(g => g.IsSystem && g.Code == "topic").Id;
                var groupList = (ListBox)tagWindow.FindName("GroupList");
                groupList.SelectedValuePath = "Id";
                groupList.SelectedValue = topicId;
                Check(!((Button)tagWindow.FindName("EditGroupButton")).IsEnabled && !((Button)tagWindow.FindName("DeleteGroupButton")).IsEnabled,
                    "Predefined tag types cannot be renamed or deleted in the native dictionary");
                EditDictionaryDialog(tagWindow, "NewTagButton", "Эпителий", "Epithelium");
                var tagList = (ListBox)tagWindow.FindName("TagList");
                ((TextBox)tagWindow.FindName("TagSearch")).Text = "EPITHELIUM";
                Check(tagList.Items.Count == 1, "Tag value search finds an existing value through either translation");
                tagList.UpdateLayout();
                var checkBox = VisualChildren<CheckBox>(tagList).Single();
                checkBox.IsChecked = true;
                Pump();
                SaveScreenshot(tagWindow, System.IO.Path.Combine(output, "native-tags.png"));
                Click(tagWindow, "SaveAssignments");
                var taxonomy = data.LoadTaxonomy();
                assignedTagId = taxonomy.MaterialTags[selected.MaterialId!.Value].Single();
                Check(taxonomy.Tags.Single(t => t.Id == assignedTagId).GroupId == topicId,
                    "Native dialog creates a translated value in a predefined type and saves its object assignment");

                var analogWindow = new AnalogGroupsWindow(data, selected.MaterialId, selected.Name, "ru");
                analogWindow.Show();
                Pump();
                EditDictionaryDialog(analogWindow, "NewGroupButton", "Учебные аналоги", "Teaching analogues");
                Click(analogWindow, "SaveAssignmentButton");
                analogGroupId = data.LoadAnalogGroups().Single().Id;
                Check(data.Load().Specimens.Where(s => s.MaterialId == selected.MaterialId).All(s => s.AnalogGroupId == analogGroupId),
                    "Native analogue dialog saves one group assignment shared by the object's translations");
                data.CreateBackup(backupPath);
            }
            var filtered = new MainWindow(AppContext.BaseDirectory, catalogRoot);
            filtered.Show();
            Pump();
            ((Expander)filtered.FindName("TagFilterExpander")).IsExpanded = true;
            filtered.UpdateLayout();
            var typeSelector = (ComboBox)filtered.FindName("TagTypeSelector");
            typeSelector.SelectedValuePath = "Id";
            typeSelector.SelectedValue = topicId;
            var valueSelector = (ComboBox)filtered.FindName("TagValueSelector");
            valueSelector.SelectedValuePath = "Id";
            valueSelector.SelectedValue = assignedTagId;
            var filteredList = (ListBox)filtered.FindName("ListOfSpecimens");
            Check(filteredList.Items.Count == 1 && ((AtlasSpecimen)filteredList.Items[0]).MaterialId == selected.MaterialId, "Saved tag filters the native catalog after restart");
            Check(((AtlasSpecimen)filteredList.Items[0]).AnalogGroupId == analogGroupId
                && ((AtlasSpecimen)filteredList.Items[0]).DisplayName.StartsWith("Учебные аналоги — ", StringComparison.Ordinal),
                "Viewer displays the saved analogue group before the object's own name");
            filteredList.SelectedIndex = 0;
            WaitImage(filtered);
            var assignmentDialogReady = false;
            filtered.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var assignmentDialog = Application.Current.Windows.OfType<TagsWindow>().SingleOrDefault();
                if (assignmentDialog is not null)
                {
                    assignmentDialogReady = ((Button)assignmentDialog.FindName("SaveAssignments")).Visibility == Visibility.Visible
                        && ((ListBox)assignmentDialog.FindName("TagList")).Items.Cast<TagsWindow.TagChoice>()
                            .Any(tag => tag.Id == assignedTagId && tag.Assigned);
                    assignmentDialog.Close();
                }
            }));
            Click(filtered, "DictionaryButton");
            Check(assignmentDialogReady, "Tags button opens assignment editing for the selected object with its saved tags");
            WaitImage(filtered);
            Click(filtered, "MaterialTagsButton");
            var metadataWindow = Application.Current.Windows.OfType<INFO2>().Single();
            var metadataPage = (TextBox)metadataWindow.FindName("Page");
            Check(metadataPage.IsReadOnly && metadataPage.Text.Contains("Учебные аналоги", StringComparison.Ordinal)
                && metadataPage.Text.Contains("Тема", StringComparison.Ordinal) && metadataPage.Text.Contains("Эпителий", StringComparison.Ordinal),
                "Read-only object metadata includes its analogue group and assigned typed tags");
            ((ComboBox)filtered.FindName("LanguageSelector")).SelectedIndex = 1;
            WaitImage(filtered);
            Check(metadataWindow.Title == "Object metadata" && metadataPage.Text.Contains("Teaching analogues", StringComparison.Ordinal)
                && metadataPage.Text.Contains("Epithelium", StringComparison.Ordinal)
                && Equals(((Button)filtered.FindName("MaterialTagsButton")).Content, "Metadata")
                && filteredList.Items.Count == 1 && ((AtlasSpecimen)filteredList.SelectedItem).MaterialId == selected.MaterialId,
                "Global language change translates the viewer and its open metadata window while retaining the selected object and filters");
            metadataWindow.Close();
            ((ComboBox)filtered.FindName("LanguageSelector")).SelectedIndex = 0;
            WaitImage(filtered);
            SaveScreenshot(filtered, System.IO.Path.Combine(output, "native-catalog-filter.png"));
            Click(filtered, "ResetFiltersButton");
            Check(filteredList.Items.Count == 5, "Reset clears selected tag filters and restores all object cards");
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
            Environment.SetEnvironmentVariable("HISTOSETS_SETTINGS_DIRECTORY", previousSettingsDirectory);
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
    private static void Click(Window window, string name)
        => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void EditDictionaryDialog(Window owner, string buttonName, string russian, string english)
    {
        Exception? failure = null;
        owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w.GetType().Name == "TaxonomyEditWindow");
            try
            {
                if (dialog is null) throw new Exception("Dictionary edit dialog did not open");
                dialog.UpdateLayout();
                var fields = VisualChildren<TextBox>(dialog).ToArray();
                fields[0].Text = russian;
                fields[1].Text = english;
                VisualChildren<Button>(dialog).Single(b => Equals(b.Content, "Сохранить")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (dialog.IsVisible)
                    throw new Exception("Dictionary edit did not save: " + string.Join(" | ", VisualChildren<TextBlock>(dialog).Select(t => t.Text)));
            }
            catch (Exception ex)
            {
                failure = ex;
                dialog?.Close();
            }
        }));
        Click(owner, buttonName);
        Pump();
        if (failure is not null) throw failure;
    }
    private static void WaitImage(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!((Button)window.FindName("PreviewButton")).IsEnabled && ((TextBlock)window.FindName("ImageMessage")).Text != "Изображение недоступно" && DateTime.UtcNow < deadline) Pump();
        if (!((Button)window.FindName("PreviewButton")).IsEnabled) throw new Exception("Image did not load: " + ((TextBlock)window.FindName("StatusText")).Text);
    }
    private static void WaitHome(HomeWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((!((Button)window.FindName("ViewButton")).IsEnabled || !((Button)window.FindName("LanguageButton")).IsEnabled)
            && DateTime.UtcNow < deadline) Pump();
        if (window.SelectedCatalogDirectory is null || !((Button)window.FindName("ViewButton")).IsEnabled)
            throw new Exception("Home could not open the catalog: " + ((TextBlock)window.FindName("StatusText")).Text);
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

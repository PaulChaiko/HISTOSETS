using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HistOSets;
using HistOSets.Core;
using HistOSets.Storage;

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
            var catalogPath = System.IO.Path.Combine(temp, "catalog.histosets");
            CatalogStore.CopyCatalog(System.IO.Path.Combine(AppContext.BaseDirectory, "Demo.histosets"), catalogPath);
            var window = new MainWindow(new CatalogStore(catalogPath), ownsStore: true);
            // Supply the startup window before pumping the dispatcher, so App
            // uses only this isolated catalog. The published EXE is tested separately.
            app.MainWindow = window;
            window.Show();
            Pump();
            Check(app.Windows.Count == 1, "Smoke harness opens only its isolated catalog window");
            RunWindowBoundsChecks();
            var specimens = (ListBox)window.FindName("ListOfSpecimens");
            var elements = (ListBox)window.FindName("ListOfElements");
            var image = (Image)window.FindName("Specimen");
            var canvas = (Canvas)window.FindName("Desk");
            var welcome = (Image)window.FindName("WelcomeLogo");
            Check(specimens.Items.Count == 5, "The bundled catalog directly opens five objects from one file");
            Check(specimens.SelectedIndex == -1 && image.Source is null && welcome.Source is not null && welcome.IsVisible,
                "Startup displays the original logo until a specimen is selected");
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
                        for (var i = 0; i < polygons.Length; i++)
                            Check(polygons[i].Points.SequenceEqual(element.Polygons[i].Points.Select(p => new Point(p.X, p.Y))),
                                "The viewer renders the exact original pixel coordinates of the saved region");
                        regions += polygons.Length;
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
            Check(window.FindName("ImageSelector") is null, "The viewer has one image per card with no image selector");
            language.SelectedIndex = 0;
            WaitImage(window);
            Check(((AtlasSpecimen)specimens.SelectedItem).MaterialId == materialId && ((AtlasElement)elements.SelectedItem).Id == elementId, "Language switch preserves selected material and element");
            var search = (TextBox)window.FindName("SearchBox");
            search.Text = "CORNEA";
            WaitImage(window);
            Check(specimens.Items.Count == 2 && specimens.Items.Cast<AtlasSpecimen>().All(s => s.Locale == "ru"), "English search finds Russian material cards");
            search.Text = "Несуществующий препарат";
            Check(specimens.Items.Count == 0 && image.Source is null && canvas.Children.OfType<Polygon>().Count() == 0
                && !((CheckBox)window.FindName("OverviewToggle")).IsEnabled, "Empty search clears the prior image, elements and contours");
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
            var overview = (Border)window.FindName("OverviewBorder");
            var overviewToggle = (CheckBox)window.FindName("OverviewToggle");
            Check(imageScale.ScaleX > 1 && overview.IsVisible, "Zoom exposes a navigation overview for the enlarged image");
            overviewToggle.IsChecked = false;
            Click(window, "ZoomInButton");
            Check(!overview.IsVisible && imageScale.ScaleX > 1, "Overview remains hidden while zooming after it has been turned off");
            overviewToggle.IsChecked = true;
            Check(overview.IsVisible, "The overview can be shown again without changing images");
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
            var selectedImageId = selected.ImageId ?? throw new Exception("Missing demo image identity");
            window.Close();
            // The editor test imports an ordinary image chosen by a user. Obtain its
            // unchanged contents from the bundled catalog, without a resource folder.
            var importImage = System.IO.Path.Combine(temp, "editor-import.jpg");
            byte[] selectedImageHash;
            using (var data = new CatalogStore(catalogPath))
            {
                using var source = data.OpenImage(selectedImageId);
                using (var destination = File.Create(importImage)) source.CopyTo(destination);
                using var original = File.OpenRead(importImage);
                selectedImageHash = SHA256.HashData(original);
            }

            var home = new HomeWindow(catalogPath: null, rememberLocation: false);
            home.Show();
            Pump();
            Check(home.SelectedCatalogPath is null && !((Button)home.FindName("ViewButton")).IsEnabled,
                "Home starts with no selected database and cannot open the viewer");
            Check(!((Button)home.FindName("SelfTestButton")).IsEnabled && !((Button)home.FindName("EditButton")).IsEnabled,
                "Home requires a selected database for editing and self-test");
            Click(home, "LanguageButton");
            Check(Equals(((Button)home.FindName("ViewButton")).Content, "View")
                && ((TextBlock)home.FindName("CatalogName")).Text == "No database selected"
                && File.ReadAllText(System.IO.Path.Combine(temp, "settings", "language.txt")).Trim() == "en",
                "Language switch updates home UI and persists the preference outside the catalog");
            SaveScreenshot(home, System.IO.Path.Combine(output, "native-home-empty.png"));
            home.Close();

            home = new HomeWindow(catalogPath: catalogPath, rememberLocation: false);
            home.Show();
            WaitHome(home);
            Check(Equals(((Button)home.FindName("ViewButton")).Content, "View")
                && home.SelectedCatalogPath == catalogPath
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

            var emptyPath = System.IO.Path.Combine(temp, "empty-catalog.histosets");
            CatalogStore.CreateCatalog(emptyPath, "Пустая учебная база");
            home = new HomeWindow(catalogPath: emptyPath, rememberLocation: false);
            home.Show();
            WaitHome(home);
            Check(((TextBlock)home.FindName("CatalogName")).Text == "Пустая учебная база"
                && ((TextBlock)home.FindName("CatalogStatistics")).Text == "Объектов: 0",
                "A newly created empty catalog opens without importing demonstration objects");
            Check(((Button)home.FindName("EditButton")).IsEnabled && ((Button)home.FindName("SelfTestButton")).IsEnabled,
                "Selected empty database enables the editor and the training preparation screen");
            var edited = CheckEditorWorkflow(home, importImage, output);
            home.Close();
            CheckSavedEditorObject(emptyPath, output, edited);
            CheckAdvancedEditorWorkflow(temp, output);
            CheckExchangeWorkflow(temp, output);
            CheckTrainingWorkflow(temp, output);
            CheckTrainingSessions(temp, output);
            CheckTraceTrainingSessions(temp, output);

            var backupPath = System.IO.Path.Combine(temp, "native-backup.histosets");
            Guid topicId;
            Guid assignedTagId;
            Guid analogGroupId;
            using (var data = new CatalogStore(catalogPath))
            {
                var statistics = data.GetStatistics();
                Check(statistics == new CatalogStatistics(5, 5, 10, 27, 33), "The demo retains verified translations and geometry");
                var management = new CatalogWindow(data);
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
                Check(groupList.Items.Count == 7 && tagWindow.FindName("EditGroupButton") is null && tagWindow.FindName("DeleteGroupButton") is null,
                    "The tag dictionary offers six fixed types and an All types filter");
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
            var filtered = new MainWindow(new CatalogStore(catalogPath), ownsStore: true);
            filtered.Show();
            Pump();
            var filteredList = (ListBox)filtered.FindName("ListOfSpecimens");
            Check(filtered.FindName("SortSelector") is null && filtered.FindName("DictionaryButton") is null
                && filtered.FindName("AnalogGroupsButton") is null && filtered.FindName("TagFilterExpander") is null,
                "Viewer keeps catalog editing, ordering, and filters out of the specimen panel");
            ChooseViewerTagFilter(filtered, topicId, assignedTagId, apply: false);
            Check(filteredList.Items.Count == 5, "Cancelling the filter dialog leaves the viewer selection unchanged");
            ChooseViewerTagFilter(filtered, topicId, assignedTagId, apply: true);
            Check(filteredList.Items.Count == 1 && ((AtlasSpecimen)filteredList.Items[0]).MaterialId == selected.MaterialId, "Saved tag filters the native catalog after restart");
            Check(((AtlasSpecimen)filteredList.Items[0]).AnalogGroupId == analogGroupId
                && ((AtlasSpecimen)filteredList.Items[0]).DisplayName.StartsWith("Учебные аналоги — ", StringComparison.Ordinal),
                "Viewer displays the saved analogue group before the object's own name");
            filteredList.SelectedIndex = 0;
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

            var isolatedFolder = Directory.CreateDirectory(System.IO.Path.Combine(temp, "isolated"));
            var transferredPath = System.IO.Path.Combine(isolatedFolder.FullName, "received.histosets");
            File.Move(backupPath, transferredPath);
            Check(isolatedFolder.EnumerateFileSystemInfos().Single().FullName == transferredPath,
                "Only the catalog file is transferred to the receiving folder");
            using (var received = new CatalogStore(transferredPath))
            {
                using (var original = received.OpenImage(selectedImageId))
                    Check(selectedImageHash.SequenceEqual(SHA256.HashData(original)), "The standalone copy preserves the original image bytes");
                var viewer = new MainWindow(received);
                viewer.Show();
                Pump();
                var receivedObjects = (ListBox)viewer.FindName("ListOfSpecimens");
                Check(receivedObjects.Items.Count == 5, "The transferred catalog opens directly with no additional files");
                receivedObjects.SelectedItem = receivedObjects.Items.Cast<AtlasSpecimen>().Single(item => item.MaterialId == selected.MaterialId);
                WaitImage(viewer);
                viewer.Close();
            }

            var brokenPath = System.IO.Path.Combine(temp, "invalid.histosets");
            File.WriteAllText(brokenPath, "Not a HISTOSETS catalog");
            home = new HomeWindow(catalogPath: transferredPath, rememberLocation: false);
            home.Show();
            WaitHome(home);
            var attempt = home.OpenCatalogAsync(brokenPath);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!attempt.IsCompleted && DateTime.UtcNow < deadline) Pump();
            if (!attempt.IsCompleted) throw new Exception("Opening an invalid catalog did not finish");
            attempt.GetAwaiter().GetResult();
            Check(home.SelectedCatalogPath == transferredPath && ((Button)home.FindName("ViewButton")).IsEnabled
                && File.ReadAllText(brokenPath) == "Not a HISTOSETS catalog",
                "An invalid catalog is left intact and the previous selection remains usable");
            home.Close();
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
    private static void ChooseViewerTagFilter(Window owner, Guid typeId, Guid tagId, bool apply)
    {
        Exception? problem = null;
        owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<TagFiltersWindow>().SingleOrDefault();
            try
            {
                if (dialog is null) throw new Exception("The viewer did not open the tag filter dialog");
                var type = (ComboBox)dialog.FindName("TypeSelector");
                type.SelectedValuePath = "Id";
                type.SelectedValue = typeId;
                ((TextBox)dialog.FindName("TagSearchBox")).Text = "EPITHELIUM";
                dialog.UpdateLayout();
                var tags = (ListBox)dialog.FindName("TagList");
                var target = tags.Items.Cast<TagFiltersWindow.TagChoice>().Single();
                Check(target.Id == tagId, "The filter dialog searches translated tag names within the selected type");
                var box = VisualChildren<CheckBox>(tags).Single();
                box.SetCurrentValue(CheckBox.IsCheckedProperty, true);
                Check(target.Selected, "Checking a visible tag changes the dialog's draft selection");
                if (apply) Click(dialog, "ApplyButton");
                else dialog.Close();
            }
            catch (Exception ex)
            {
                problem = ex;
                dialog?.Close();
            }
        }));
        Click(owner, "TagFiltersButton");
        if (problem is not null) throw problem;
    }
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
        var specimen = (Image)window.FindName("Specimen");
        var message = (TextBlock)window.FindName("ImageMessage");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (specimen.Source is null && message.Text is not ("Изображение недоступно" or "Image unavailable")
            && DateTime.UtcNow < deadline) Pump();
        if (specimen.Source is null) throw new Exception("Image did not load: " + ((TextBlock)window.FindName("StatusText")).Text);
    }
    private static void WaitHome(HomeWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((!((Button)window.FindName("ViewButton")).IsEnabled || !((Button)window.FindName("LanguageButton")).IsEnabled)
            && DateTime.UtcNow < deadline) Pump();
        if (window.SelectedCatalogPath is null || !((Button)window.FindName("ViewButton")).IsEnabled)
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

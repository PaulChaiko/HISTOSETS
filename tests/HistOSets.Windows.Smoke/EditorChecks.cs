using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using HistOSets;
using HistOSets.Controls;
using HistOSets.Core;
using HistOSets.Storage;

internal static partial class Program
{
    private sealed record EditorResult(Guid MaterialId, IReadOnlyDictionary<Guid, string> Regions);

    private static EditorResult CheckEditorWorkflow(HomeWindow home, string imagePath, string output)
    {
        EditorResult? result = null;
        InModal<EditorCatalogWindow>(home, "EditButton", chooser =>
        {
            SaveScreenshot(chooser, System.IO.Path.Combine(output, "native-editor-empty-catalog.png"));
            InModal<EditorWindow>(chooser, "NewButton", editor =>
            {
                editor.AddImageFromPath(imagePath);
                FillText(editor, "MaterialRu", "Проверочный объект", "Краткое описание на русском.", "Полное описание объекта на русском.");
                var ready = (Button)editor.FindName("ReadyButton");
                var previousStatus = ((TextBlock)editor.FindName("OperationStatus")).Text;
                if (ready.IsEnabled) Click(editor, "ReadyButton");
                Check(editor.SavedMaterialId is null && (!ready.IsEnabled || ((TextBlock)editor.FindName("OperationStatus")).Text != previousStatus),
                    "Incomplete bilingual object cannot be saved as ready");
                FillText(editor, "MaterialEn", "Smoke specimen", "English object summary.", "Full English object description.");
                ((TabControl)editor.FindName("FormTabs")).SelectedItem = editor.FindName("ElementsTab");
                Click(editor, "AddElementButton");
                FillText(editor, "ElementRu", "Проверочный элемент", "Краткое описание элемента.", "Полное описание элемента.");
                FillText(editor, "ElementEn", "Smoke element", "English element summary.", "Full English element description.");
                editor.UpdateLayout();
                var surface = (RegionEditorSurface)editor.FindName("Surface");
                Click(editor, "FitButton");
                DrawBox(editor, surface, "RectangleTool", new(100, 100), new(400, 300));
                Click(editor, "ZoomInButton");
                DrawBox(editor, surface, "EllipseTool", new(500, 100), new(800, 300));
                ((RadioButton)editor.FindName("PolygonTool")).IsChecked = true;
                foreach (var point in new[] { new Point(900, 100), new Point(1100, 120), new Point(1050, 300) })
                {
                    var position = surface.ImageToViewport(point);
                    surface.PointerDown(position);
                    surface.PointerUp(position);
                }
                Click(editor, "FinishPolygonButton");
                Check(surface.Regions.Count == 3 && surface.Regions.Select(r => r.Geometry.Kind).Order().SequenceEqual(new[] { "ellipse", "polygon", "rectangle" }),
                    "Editor drawing tools create a rectangle, parametric ellipse and closed polygon");
                var rectangle = surface.Regions.Single(r => r.Geometry.Kind == "rectangle").Geometry;
                var ellipse = surface.Regions.Single(r => r.Geometry.Kind == "ellipse").Geometry;
                Check(Near(rectangle.X, 100) && Near(rectangle.Y, 100) && Near(rectangle.Width, 300) && Near(rectangle.Height, 200)
                    && Near(ellipse.X, 500) && Near(ellipse.Y, 100) && Near(ellipse.Width, 300) && Near(ellipse.Height, 200),
                    "Drawing under different zoom levels retains original image pixel coordinates");
                var beforeUndo = surface.Regions.ToDictionary(r => r.Id, r => RegionGeometryCodec.Serialize(r.Geometry));
                Click(editor, "UndoButton");
                Check(surface.Regions.Count == 2, "Undo removes the last completed drawing operation");
                Click(editor, "RedoButton");
                Check(SameRegions(surface.Regions, beforeUndo), "Redo restores exact region identities and geometry");
                Click(editor, "SaveDraftButton");
                Check(editor.SavedMaterialId is not null && editor.SavedChanges, "Editor saves the new object draft through its normal save action");
                result = new(editor.SavedMaterialId!.Value, beforeUndo);
                Click(editor, "FitButton");
                SaveScreenshot(editor, System.IO.Path.Combine(output, "native-editor-regions.png"));
            });
            Check(result is not null, "New editor returns the saved object identity to its catalog chooser");
            var saved = result ?? throw new Exception("Editor did not return a saved object identity");
            SelectEditedObject(chooser, saved.MaterialId);
            Check(((EditorCatalogWindow.MaterialChoice)((ListBox)chooser.FindName("ObjectList")).SelectedItem).Entry.Status == "draft",
                "Save draft keeps the object in draft status until explicitly marked ready");
            InModal<EditorWindow>(chooser, "EditButton", editor =>
            {
                Check(((TextBox)editor.FindName("MaterialRuName")).Text == "Проверочный объект"
                    && ((TextBox)editor.FindName("MaterialEnName")).Text == "Smoke specimen",
                    "Reopening an object restores both language versions of its editable text");
                ((ListBox)editor.FindName("ElementList")).SelectedIndex = 0;
                var surface = (RegionEditorSurface)editor.FindName("Surface");
                Check(SameRegions(surface.Regions, saved.Regions), "Reopening the editor restores exact saved shapes and their identifiers");
                ((TextBox)editor.FindName("MaterialRuName")).Text = "Несохранённое изменение";
                RespondToEditorClose(editor, "PromptCancelButton");
                Check(editor.IsVisible && ((TextBox)editor.FindName("MaterialRuName")).Text == "Несохранённое изменение",
                    "Cancel in the unsaved-changes prompt keeps the working copy open");
                RespondToEditorClose(editor, "PromptDiscardButton");
                Check(!editor.IsVisible, "Discard closes the editor without saving its working copy");
            });

            SelectEditedObject(chooser, saved.MaterialId);
            InModal<EditorWindow>(chooser, "EditButton", editor =>
            {
                Check(((TextBox)editor.FindName("MaterialRuName")).Text == "Проверочный объект",
                    "Discarded text changes do not reappear when the object is reopened");
                Click(editor, "ReadyButton");
                Check(editor.SavedMaterialId == saved.MaterialId && editor.SavedChanges,
                    "Complete bilingual object can be saved as ready");
            });
            WaitEditorCatalog(chooser);
            SaveScreenshot(chooser, System.IO.Path.Combine(output, "native-editor-catalog.png"));
        });
        Check(((TextBlock)home.FindName("CatalogStatistics")).Text == "Объектов: 1",
            "Returning from the editor updates the home database object count");
        return result ?? throw new Exception("Editor workflow did not produce an object");
    }

    private static void CheckSavedEditorObject(string catalogRoot, string output, EditorResult expected)
    {
        using var store = new CatalogStore(catalogRoot);
        var draft = store.LoadMaterialDraft(expected.MaterialId);
        Check(draft.Status == "ready" && draft.Texts["ru"].Name == "Проверочный объект" && draft.Texts["en"].Name == "Smoke specimen"
            && draft.Texts["en"].Summary == "English object summary." && draft.Texts["ru"].Description == "Полное описание объекта на русском."
            && draft.Elements.Single().Texts["en"].Name == "Smoke element",
            "Saved editor object retains ready status and complete bilingual content after catalog reopening");
        Check(draft.Images.Count == 1 && draft.Images[0].IsPrimary
            && SameRegions(draft.Elements.Single().Regions.Select(r => new AtlasRegion(r.Id, r.Geometry)), expected.Regions),
            "Catalog reopening retains the primary image and exact persisted region geometry");
        var viewer = new MainWindow(store);
        viewer.Show();
        Pump();
        ((ListBox)viewer.FindName("ListOfSpecimens")).SelectedIndex = 0;
        WaitImage(viewer);
        ((ListBox)viewer.FindName("ListOfElements")).SelectedIndex = 0;
        var desk = (Canvas)viewer.FindName("Desk");
        var shapes = desk.Children.OfType<Shape>().ToArray();
        Check(shapes.Length == 3 && shapes.OfType<Rectangle>().Count() == 1 && shapes.OfType<Ellipse>().Count() == 1 && shapes.OfType<Polygon>().Count() == 1,
            "Read-only viewer renders each persisted region with its original shape type");
        var ellipse = shapes.OfType<Ellipse>().Single();
        Check(Near(Canvas.GetLeft(ellipse), 500) && Near(Canvas.GetTop(ellipse), 100) && Near(ellipse.Width, 300) && Near(ellipse.Height, 200),
            "Read-only viewer renders an exact ellipse instead of a polygon approximation");
        Click(viewer, "FitButton");
        SaveScreenshot(viewer, System.IO.Path.Combine(output, "native-editor-saved-view.png"));
        viewer.Close();
    }

    private static void FillText(Window window, string prefix, string name, string summary, string description)
    {
        ((TextBox)window.FindName(prefix + "Name")).Text = name;
        ((TextBox)window.FindName(prefix + "Summary")).Text = summary;
        ((TextBox)window.FindName(prefix + "Description")).Text = description;
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < .000001;
    private static bool SameRegions(IEnumerable<AtlasRegion> regions, IReadOnlyDictionary<Guid, string> expected)
    {
        var actual = regions.ToArray();
        return actual.Length == expected.Count && actual.All(r => expected.TryGetValue(r.Id, out var geometry) && RegionGeometryCodec.Serialize(r.Geometry) == geometry);
    }

    private static void DrawBox(EditorWindow editor, RegionEditorSurface surface, string tool, Point start, Point end)
    {
        ((RadioButton)editor.FindName(tool)).IsChecked = true;
        surface.PointerDown(surface.ImageToViewport(start));
        surface.PointerMove(surface.ImageToViewport(end));
        surface.PointerUp(surface.ImageToViewport(end));
    }

    private static void SelectEditedObject(EditorCatalogWindow chooser, Guid id)
    {
        WaitEditorCatalog(chooser);
        var objects = (ListBox)chooser.FindName("ObjectList");
        objects.SelectedValuePath = "Entry.Id";
        objects.SelectedValue = id;
        Check(objects.SelectedItem is not null, "Editor chooser selects the newly saved object");
    }

    private static void WaitEditorCatalog(EditorCatalogWindow chooser)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!((Button)chooser.FindName("NewButton")).IsEnabled && DateTime.UtcNow < deadline) Pump();
        if (!((Button)chooser.FindName("NewButton")).IsEnabled)
            throw new Exception("Editor catalog did not load: " + ((TextBlock)chooser.FindName("OperationStatus")).Text);
    }

    private static void InModal<T>(Window owner, string buttonName, Action<T> action) where T : Window
    {
        Exception? failure = null;
        var found = false;
        owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<T>().SingleOrDefault();
            if (dialog is null) { failure = new Exception(typeof(T).Name + " did not open"); return; }
            found = true;
            try
            {
                if (dialog is EditorCatalogWindow chooser) WaitEditorCatalog(chooser);
                action(dialog);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (dialog is EditorWindow editor && dialog.IsVisible) CloseEditorForCleanup(editor);
                else if (dialog.IsVisible)
                {
                    if (dialog is EditorCatalogWindow chooser)
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(20);
                        while (!((Grid)chooser.FindName("Actions")).IsEnabled && DateTime.UtcNow < deadline) Pump();
                    }
                    dialog.Close();
                }
            }
        }));
        Click(owner, buttonName);
        Pump();
        if (failure is not null) throw failure;
        Check(found, buttonName + " opens " + typeof(T).Name);
    }

    private static void RespondToEditorClose(EditorWindow editor, string responseButton)
    {
        var answered = false;
        editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var prompt = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w.GetType().Name == "EditorClosePrompt");
            if (prompt is null) return;
            var button = VisualChildren<Button>(prompt).SingleOrDefault(b => b.Name == responseButton);
            if (button is null) { prompt.Close(); return; }
            answered = true;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        Click(editor, "CloseButton");
        Pump();
        Check(answered, "Unsaved editor shows " + responseButton + " confirmation choice");
    }

    private static void CloseEditorForCleanup(EditorWindow editor)
    {
        ((RegionEditorSurface)editor.FindName("Surface")).CancelPendingOperation();
        editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var prompt = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w.GetType().Name == "EditorClosePrompt");
            var discard = prompt is null ? null : VisualChildren<Button>(prompt).SingleOrDefault(b => b.Name == "PromptDiscardButton");
            discard?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        editor.Close();
        Pump();
    }
}

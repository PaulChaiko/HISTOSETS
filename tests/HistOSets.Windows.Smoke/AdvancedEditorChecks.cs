using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HistOSets;
using HistOSets.Controls;
using HistOSets.Core;
using HistOSets.Storage;
using ShapePath = System.Windows.Shapes.Path;

internal static partial class Program
{
    private static void CheckAdvancedEditorWorkflow(string temp, string output)
    {
        var imagePath = System.IO.Path.Combine(temp, "calibration-source.png");
        CreateUniformImage(imagePath, 1200, 900);
        var catalogRoot = System.IO.Path.Combine(temp, "advanced-editor-catalog");
        Guid materialId;
        Guid editedRegionId;
        Guid rectangleId;
        IReadOnlyDictionary<Guid, string> savedRegions;
        using (var store = new CatalogStore(catalogRoot))
        {
            var editor = new EditorWindow(store);
            editor.Show();
            Pump();
            try
            {
                editor.AddImageFromPath(imagePath);
                FillText(editor, "MaterialRu", "Калиброванные области", "Кисть, ластик и масштаб.", "Объект для проверки сложных областей и физических измерений.");
                FillText(editor, "MaterialEn", "Calibrated regions", "Brush, eraser and scale.", "A specimen for checking compound regions and physical measurements.");
                ((TabControl)editor.FindName("FormTabs")).SelectedItem = editor.FindName("ElementsTab");
                Click(editor, "AddElementButton");
                FillText(editor, "ElementRu", "Измеряемая структура", "Структура с несколькими областями.", "Несвязанные участки и внутреннее отверстие принадлежат одной структуре.");
                FillText(editor, "ElementEn", "Measured structure", "A structure with several regions.", "Separate components and an interior hole belong to the same structure.");
                editor.UpdateLayout();
                var surface = (RegionEditorSurface)editor.FindName("Surface");
                DrawBox(editor, surface, "RectangleTool", new(100, 100), new(400, 300));
                var initialRectangle = surface.Regions.Single();
                editedRegionId = initialRectangle.Id;

                var uncalibratedSummary = ((TextBlock)editor.FindName("CalibrationSummary")).Text;
                CalibrationDialog(editor, dialog =>
                {
                    SetCalibrationFields(dialog, "0.6", "0.9", "mm");
                    Click(dialog, "CancelCalibrationButton");
                });
                Check(((TextBlock)editor.FindName("CalibrationSummary")).Text == uncalibratedSummary,
                    "Cancel in the calibration dialog leaves the working copy uncalibrated");
                CalibrationDialog(editor, dialog =>
                {
                    SetCalibrationFields(dialog, "0.6", "0.9", "mm");
                    SaveScreenshot(dialog, System.IO.Path.Combine(output, "native-calibration-dialog.png"));
                    SetCalibrationFields(dialog, "0", "0.9", "mm");
                    Click(dialog, "ApplyCalibrationButton");
                    Check(dialog.IsVisible && !string.IsNullOrWhiteSpace(((TextBlock)dialog.FindName("CalibrationValidationStatus")).Text),
                        "Calibration rejects a zero physical image width without closing the dialog");
                    SetCalibrationFields(dialog, "0.6", "0.9", "mm");
                    Click(dialog, "ApplyCalibrationButton");
                });
                var calibratedSummary = ((TextBlock)editor.FindName("CalibrationSummary")).Text;
                Check(calibratedSummary != uncalibratedSummary && KnownRectangleMeasurements(((TextBlock)editor.FindName("MeasurementSummary")).Text),
                    "Applying whole-image calibration updates the editor scale and selected-region measurements");
                Click(editor, "UndoButton");
                Check(((TextBlock)editor.FindName("CalibrationSummary")).Text == uncalibratedSummary && surface.Regions.Single().Id == editedRegionId,
                    "Undo removes only the calibration and preserves the drawn region");
                Click(editor, "RedoButton");
                Check(((TextBlock)editor.FindName("CalibrationSummary")).Text == calibratedSummary,
                    "Redo restores the exact working-copy calibration");

                ((TextBox)editor.FindName("BrushRadiusInput")).Text = "20";
                ((RadioButton)editor.FindName("BrushTool")).IsChecked = true;
                Click(editor, "ZoomInButton");
                Check(Near(surface.BrushRadius, 20), "Brush radius is entered in original image pixels");
                var beforeBrush = RegionSnapshot(surface);
                var noOpChanges = 0;
                EventHandler<RegionsChangedEventArgs> changed = (_, _) => noOpChanges++;
                surface.RegionsChanged += changed;
                Stroke(surface, new(200, 150), new(250, 150));
                surface.RegionsChanged -= changed;
                Check(noOpChanges == 0 && SameRegions(surface.Regions, beforeBrush),
                    "Painting entirely inside an existing region creates no edit and preserves exact v1 geometry");
                Stroke(surface, new(350, 200), new(550, 200));
                var painted = surface.Regions.Single();
                Check(painted.Id == editedRegionId && painted.Geometry.Kind == "compound"
                    && Contains(painted.Geometry, 525, 200) && !Contains(painted.Geometry, 525, 225)
                    && Contains(painted.Geometry, 150, 150),
                    "Brush adds to the selected region at source-pixel radius under zoom and retains its identity");
                var afterBrush = RegionSnapshot(surface);
                Click(editor, "UndoButton");
                Check(SameRegions(surface.Regions, beforeBrush), "Undo of a brush gesture restores the exact original parametric rectangle");
                Click(editor, "RedoButton");
                Check(SameRegions(surface.Regions, afterBrush), "Redo of a brush gesture restores exact compound geometry and UUID");
                surface.PointerDown(surface.ImageToViewport(new Point(525, 200)));
                surface.PointerMove(surface.ImageToViewport(new Point(700, 200)));
                surface.CancelPendingOperation();
                Check(SameRegions(surface.Regions, afterBrush), "Cancelling an unfinished brush stroke leaves committed geometry unchanged");

                ((RadioButton)editor.FindName("EraserTool")).IsChecked = true;
                Stroke(surface, new(200, 200), new(200, 200));
                var pierced = surface.Regions.Single();
                Check(pierced.Id == editedRegionId && pierced.Geometry.Rings.Count >= 2
                    && !Contains(pierced.Geometry, 200, 200) && Contains(pierced.Geometry, 150, 150)
                    && RegionMeasurements.Measure(pierced.Geometry).Area < RegionMeasurements.Measure(painted.Geometry).Area,
                    "Eraser creates a real interior hole and decreases area without deleting the enclosing region");
                var afterHole = RegionSnapshot(surface);
                Click(editor, "UndoButton");
                Check(SameRegions(surface.Regions, afterBrush), "Undo of erasing restores the filled interior exactly");
                Click(editor, "RedoButton");
                Check(SameRegions(surface.Regions, afterHole), "Redo restores the interior hole exactly");
                Stroke(surface, new(300, 80), new(300, 320));
                var split = surface.Regions.Single();
                Check(split.Id == editedRegionId && split.Geometry.Rings.Count >= 3
                    && Contains(split.Geometry, 250, 150) && Contains(split.Geometry, 350, 150)
                    && !Contains(split.Geometry, 300, 150) && !Contains(split.Geometry, 200, 200),
                    "Erasing across a region preserves both disconnected components and its earlier hole in one region");

                var firstGeometry = RegionGeometryCodec.Serialize(split.Geometry);
                Click(editor, "NewRegionButton");
                Check(surface.Tool == RegionEditorTool.Brush && surface.SelectedRegionId is null,
                    "New region explicitly starts an independent brush region");
                Stroke(surface, new(800, 550), new(1000, 550));
                var independent = surface.Regions.Single(r => r.Id != editedRegionId);
                Check(surface.Regions.Count == 2 && surface.SelectedRegionId == independent.Id
                    && RegionGeometryCodec.Serialize(surface.Regions.Single(r => r.Id == editedRegionId).Geometry) == firstGeometry,
                    "An independent brush stroke creates a new UUID without changing the previously selected region");
                Check(Enumerable.Range(0, 41).All(i => Contains(independent.Geometry, 800 + i * 5, 550))
                    && Contains(independent.Geometry, 900, 569) && !Contains(independent.Geometry, 900, 571),
                    "A fast brush stroke has no gaps between distant pointer samples and keeps a constant source-pixel radius");
                var afterIndependent = RegionSnapshot(surface);
                Click(editor, "UndoButton");
                Check(surface.Regions.Count == 1 && surface.Regions[0].Id == editedRegionId,
                    "An entire freehand stroke is one undo operation");
                Click(editor, "RedoButton");
                Check(SameRegions(surface.Regions, afterIndependent), "Redo restores the independent stroke with its original UUID");

                surface.SelectRegion(independent.Id);
                ((TextBox)editor.FindName("BrushRadiusInput")).Text = "200";
                ((RadioButton)editor.FindName("EraserTool")).IsChecked = true;
                Stroke(surface, new(800, 550), new(1000, 550));
                Check(surface.Regions.Count == 1 && surface.Regions[0].Id == editedRegionId
                    && RegionGeometryCodec.Serialize(surface.Regions[0].Geometry) == firstGeometry,
                    "An eraser stroke that removes its entire selected region cannot revive it on a later pointer sample or affect another region");
                Click(editor, "UndoButton");
                Check(SameRegions(surface.Regions, afterIndependent), "Undo restores a fully erased region and its original UUID");

                DrawBox(editor, surface, "RectangleTool", new(100, 450), new(400, 650));
                rectangleId = surface.Regions.Single(r => r.Geometry.Kind == "rectangle").Id;
                Click(editor, "FitButton");
                SaveScreenshot(editor, System.IO.Path.Combine(output, "native-editor-brush-eraser.png"));
                Click(editor, "ReadyButton");
                Check(editor.SavedMaterialId is not null && editor.SavedChanges,
                    "The native editor saves compound regions and calibration together as a ready object");
                materialId = editor.SavedMaterialId!.Value;
                savedRegions = RegionSnapshot(surface);
                var saved = store.LoadMaterialDraft(materialId);
                CheckCalibration(saved.Images.Single().Calibration);
                var measurement = RegionMeasurements.Measure(saved.Elements.Single().Regions.Single(r => r.Id == rectangleId).Geometry,
                    saved.Images.Single().Calibration!.PixelSizeXMetres * 1e6,
                    saved.Images.Single().Calibration!.PixelSizeYMetres * 1e6);
                Check(Near(measurement.Area, 30000) && Near(measurement.Perimeter, 700),
                    "A 300 by 200 pixel rectangle calibrated at 0.5 by 1 micrometres per pixel measures 30000 square micrometres and 700 micrometres");
            }
            catch
            {
                SaveScreenshot(editor, System.IO.Path.Combine(output, "native-advanced-editor-failure.png"));
                throw;
            }
            finally { if (editor.IsVisible) CloseEditorForCleanup(editor); }

            editor = new EditorWindow(store, materialId);
            editor.Show();
            Pump();
            try
            {
                CalibrationDialog(editor, dialog =>
                {
                    SetCalibrationFields(dialog, "1.2", "0.9", "mm");
                    Click(dialog, "ApplyCalibrationButton");
                });
                RespondToEditorClose(editor, "PromptCancelButton");
                Check(editor.IsVisible, "Cancel when closing preserves an unsaved calibration in the open editor");
                CheckCalibration(store.LoadMaterialDraft(materialId).Images.Single().Calibration);
                RespondToEditorClose(editor, "PromptDiscardButton");
                Check(!editor.IsVisible, "Discard closes an editor with a staged calibration change");
                CheckCalibration(store.LoadMaterialDraft(materialId).Images.Single().Calibration);
            }
            finally { if (editor.IsVisible) CloseEditorForCleanup(editor); }
        }

        using (var reopened = new CatalogStore(catalogRoot))
        {
            var saved = reopened.LoadMaterialDraft(materialId);
            CheckCalibration(saved.Images.Single().Calibration);
            Check(SameRegions(saved.Elements.Single().Regions.Select(r => new AtlasRegion(r.Id, r.Geometry)), savedRegions),
                "Restart retains exact compound rings, holes, independent regions and unchanged v1 rectangle geometry");
            CheckAdvancedReadOnlyViewer(reopened, editedRegionId, output);
            var backup = System.IO.Path.Combine(temp, "advanced-editor-backup.histosets");
            reopened.CreateBackup(backup);
            var restoredRoot = System.IO.Path.Combine(temp, "advanced-editor-restored");
            CatalogStore.RestoreBackup(backup, restoredRoot);
            using var restored = new CatalogStore(restoredRoot);
            var restoredDraft = restored.LoadMaterialDraft(materialId);
            CheckCalibration(restoredDraft.Images.Single().Calibration);
            Check(SameRegions(restoredDraft.Elements.Single().Regions.Select(r => new AtlasRegion(r.Id, r.Geometry)), savedRegions),
                "Backup and restore retain exact v1 and v2 geometry together with image calibration");
        }
    }

    private static void CheckAdvancedReadOnlyViewer(CatalogStore store, Guid compoundId, string output)
    {
        var viewer = new MainWindow(store);
        viewer.Show();
        try
        {
            ((ListBox)viewer.FindName("ListOfSpecimens")).SelectedIndex = 0;
            WaitImage(viewer);
            ((ListBox)viewer.FindName("ListOfElements")).SelectedIndex = 0;
            viewer.UpdateLayout();
            var path = ((Canvas)viewer.FindName("Desk")).Children.OfType<ShapePath>().Single(p => Equals(p.Tag, compoundId));
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) drawing.DrawGeometry(path.Fill, null, path.Data);
            var bitmap = new RenderTargetBitmap(1200, 900, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            Check(PixelAlpha(bitmap, 200, 200) == 0 && PixelAlpha(bitmap, 300, 150) == 0
                && PixelAlpha(bitmap, 150, 150) > 0 && PixelAlpha(bitmap, 350, 150) > 0,
                "The read-only viewer renders the erased hole and split gap transparently while filling both surviving components");
            var measurements = (TextBox)viewer.FindName("MeasurementsText");
            Check(measurements.IsReadOnly && measurements.IsVisible && KnownRectangleMeasurements(measurements.Text),
                "The read-only viewer presents physical region measurements using the saved calibration unit");
            Click(viewer, "FitButton");
            SaveScreenshot(viewer, System.IO.Path.Combine(output, "native-calibrated-compound-view.png"));
        }
        finally { viewer.Close(); }
    }

    private static void CheckCalibration(ImageCalibration? calibration)
        => Check(calibration is not null && Math.Abs(calibration.PixelSizeXMetres - 0.5e-6) < 1e-15
            && Math.Abs(calibration.PixelSizeYMetres - 1e-6) < 1e-15
            && calibration.InputWidth == 0.6 && calibration.InputHeight == 0.9 && calibration.InputUnit == "mm",
            "Saved whole-image calibration retains separate X/Y scales and the original millimetre input");

    private static void CalibrationDialog(EditorWindow editor, Action<Window> action)
    {
        Exception? failure = null;
        var found = false;
        editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w.GetType().Name == "ImageCalibrationDialog");
            if (dialog is null) { failure = new Exception("Image calibration dialog did not open"); return; }
            found = true;
            try { action(dialog); }
            catch (Exception ex) { failure = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        Click(editor, "CalibrationButton");
        Pump();
        if (failure is not null) throw failure;
        Check(found, "Calibration opens the physical image dimensions dialog");
    }

    private static void SetCalibrationFields(Window dialog, string width, string height, string unit)
    {
        ((TextBox)dialog.FindName("CalibrationWidthInput")).Text = width;
        ((TextBox)dialog.FindName("CalibrationHeightInput")).Text = height;
        ((ComboBox)dialog.FindName("CalibrationUnitList")).SelectedValue = unit;
    }

    private static Dictionary<Guid, string> RegionSnapshot(RegionEditorSurface surface)
        => surface.Regions.ToDictionary(r => r.Id, r => RegionGeometryCodec.Serialize(r.Geometry));

    private static bool Contains(RegionGeometry geometry, double x, double y)
        => RegionGeometryOperations.Contains(geometry, new(x, y));

    private static bool KnownRectangleMeasurements(string text)
    {
        var normalized = text.Replace(',', '.');
        return normalized.Contains("0.03", StringComparison.Ordinal) && normalized.Contains("0.7", StringComparison.Ordinal)
            && (normalized.Contains("mm", StringComparison.Ordinal) || normalized.Contains("мм", StringComparison.Ordinal));
    }

    private static void Stroke(RegionEditorSurface surface, Point start, Point end)
    {
        surface.PointerDown(surface.ImageToViewport(start));
        surface.PointerMove(surface.ImageToViewport(end));
        surface.PointerUp(surface.ImageToViewport(end));
    }

    private static byte PixelAlpha(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static void CreateUniformImage(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 236;
            pixels[i + 1] = 232;
            pixels[i + 2] = 240;
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

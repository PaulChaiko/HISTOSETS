using System.IO;
using System.Windows;
using System.Windows.Controls;
using HistOSets;
using HistOSets.Controls;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;

internal static partial class Program
{
    private static void CheckTraceTrainingSessions(string temp, string output)
    {
        var root = Path.Combine(temp, "trace-training-catalog");
        using var store = new CatalogStore(root);
        var ids = CreateTraceCatalog(store, temp);
        var unchanged = SessionCatalogBytes(root);
        var setup = new TrainingSetupWindow(store);
        setup.Show(); WaitTraining(setup);
        try
        {
            if (Equals(((Button)setup.FindName("StartSessionButton")).Content, "Start session"))
            { Click(setup, "LanguageButton"); WaitTraining(setup); }
            setup.SelectMaterials(ids); setup.SelectMethods([TrainingMethod.TraceElement]); setup.SetOrder(TrainingOrder.ByObject);
            CompleteTrainingTask(setup, setup.BuildPlanAsync());
            Check(setup.Plan?.QuestionCount == 4 && ((Button)setup.FindName("StartSessionButton")).IsEnabled,
                "A tracing-only plan enables session execution for every eligible image and element");
            RunSessionFromSetup(setup, session => CheckRussianTraceSession(session, output));
            Check(SessionCatalogBytes(root) == unchanged,
                "Native tracing, comparison and reference review leave every catalog file byte unchanged");

            RunSessionFromSetup(setup, session =>
            {
                var drawing = TraceView(session);
                Check(session.Session.Results.Count == 0 && drawing.Regions.Count == 0 && drawing.Surface.ReferenceRegions.Count == 0
                    && drawing.Surface.CanEdit && !((Button)session.FindName("CheckAnswerButton")).IsEnabled,
                    "Re-entering a tracing plan creates a blank attempt without previous drawing, feedback or reference contours");
                DrawTraceBox(drawing, RegionEditorTool.Rectangle, new(110, 80), new(160, 130));
                RespondToFinish(session, "ContinueSessionButton");
                Check(!session.Session.IsFinished && drawing.Regions.Count == 1,
                    "Cancelling early completion preserves the unsubmitted learner drawing");
                RespondToFinish(session, "ConfirmFinishButton");
                Check(session.Session.Summary.NotAnswered == 4 && session.Session.Summary.Compared == 0
                    && session.Session.Results.All(r => r.Trace is null),
                    "Early completion does not convert an unfinished drawing into a comparison or a wrong answer");
            });

            Click(setup, "LanguageButton"); WaitTraining(setup);
            setup.SelectMaterials([ids[0]]);
            setup.SelectMethods([TrainingMethod.NameObjectImage, TrainingMethod.TraceElement, TrainingMethod.NameObjectDescription]);
            setup.SetOrder(TrainingOrder.ByMethod);
            CompleteTrainingTask(setup, setup.BuildPlanAsync());
            var mixedPlan = setup.Plan ?? throw new Exception("Mixed training plan was not prepared");
            var eligibilityDetails = string.Join("; ", mixedPlan.Eligibility.Select(item =>
                $"{item.Method}: {item.QuestionCount} question(s), issues [{string.Join(", ", item.Issues.Select(issue => issue.Code))}]"));
            Check(mixedPlan.QuestionCount == 3, "A mixed plan includes recognition, tracing and a dedicated object description"
                + (mixedPlan.QuestionCount == 3 ? "" : $"; actual count {mixedPlan.QuestionCount}; {eligibilityDetails}"));
            RunSessionFromSetup(setup, session => CheckEnglishTraceSession(session, output));

            var catalog = store.CaptureTrainingCatalog(ImageLoader.ReadMetadata);
            var plan = TrainingPlanner.Build(catalog, new([ids[0]], [TrainingMethod.TraceElement], "en"));
            var path = catalog.Materials.Single(m => m.Id == ids[0]).Images.Single().ImagePath
                ?? throw new Exception("Tracing fixture image path is missing");
            var original = File.ReadAllBytes(path);
            try
            {
                File.Delete(path);
                WithSession(catalog, plan, session =>
                {
                    Check(session.Session.CurrentResult?.Outcome == TrainingOutcome.Unavailable
                        && !((Button)session.FindName("CheckAnswerButton")).IsEnabled
                        && session.Session.Summary.Compared == 0 && session.Session.Summary.Incorrect == 0,
                        "A missing tracing image becomes a technical unavailable result with no comparison or wrong-answer count");
                    SaveScreenshot(session, Path.Combine(output, "native-trace-unavailable-en.png"));
                    CompleteSessionTask(session, session.NextAsync());
                    Check(session.Session.Summary.Unavailable == 1 && session.Session.Summary.Accuracy is null,
                        "Missing tracing media remain outside the recognition accuracy denominator");
                });
            }
            finally { File.WriteAllBytes(path, original); }
            Check(SessionCatalogBytes(root) == unchanged,
                "Mixed sessions, repeat attempts, early completion and missing-image handling persist no drawings or catalog edits");
        }
        catch { if (setup.IsVisible) SaveScreenshot(setup, Path.Combine(output, "native-trace-setup-failure.png")); throw; }
        finally
        {
            if (setup.IsVisible)
            {
                if (Equals(((Button)setup.FindName("StartSessionButton")).Content, "Start session"))
                { Click(setup, "LanguageButton"); WaitTraining(setup); }
                setup.Close();
            }
        }
    }

    private static Guid[] CreateTraceCatalog(CatalogStore store, string temp)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var draft = new MaterialDraft { Id = Guid.Parse($"73330000-0000-4000-8000-{i + 1:000000000000}") };
            draft.Texts["ru"] = new() { Name = "Учебный препарат " + (i + 1), Description = "Атласное описание с ответом" };
            draft.Texts["en"] = new() { Name = "Training specimen " + (i + 1), Description = "Answer-bearing atlas description" };
            var path = Path.Combine(temp, "trace-source-" + i + ".png"); CreateSessionImage(path, 420, 280, i + 21);
            var image = new MaterialImageDraft { SourcePath = path, SourceSha256 = HashExchangeFile(path).ToLowerInvariant(), IsPrimary = true };
            draft.Images.Add(image);
            var element = new MaterialElementDraft();
            element.Texts["ru"] = new() { Name = i == 3 ? "Кольцевой профиль и островок" : "Центральный участок" };
            element.Texts["en"] = new() { Name = i == 3 ? "Annular profile and island" : "Central profile" };
            if (i < 3)
                element.Regions.Add(new() { MaterialImageId = image.Id, Geometry = RegionGeometry.Rectangle(100, 70, 100, 100) });
            else
            {
                element.Regions.Add(new() { MaterialImageId = image.Id, Geometry = RegionGeometry.Compound([
                    [new(25, 30), new(185, 30), new(185, 200), new(25, 200)],
                    [new(70, 75), new(135, 75), new(135, 145), new(70, 145)]]) });
                element.Regions.Add(new() { MaterialImageId = image.Id, Geometry = RegionGeometry.Ellipse(260, 90, 65, 100) });
            }
            draft.Elements.Add(element);
            if (i == 0)
            {
                // Only the mixed-session target needs this description. Giving it to distinct
                // specimens would correctly make the recognition answer ambiguous.
                var prompt = new TrainingPromptDraft();
                prompt.Texts["ru"] = "Несколько округлых структур образуют повторяющийся рисунок.";
                prompt.Texts["en"] = "Several rounded structures form a repeating pattern.";
                draft.TrainingPrompts.Add(prompt);
            }
            ids.Add(store.SaveMaterialDraft(draft, ImageLoader.ReadMetadata).Id);
        }
        return ids.ToArray();
    }

    private static void CheckRussianTraceSession(TrainingSessionWindow session, string output)
    {
        var state = session.Session;
        var drawing = TraceView(session);
        var surface = drawing.Surface;
        Check(state.Questions.Count == 4 && state.DeferredQuestionCount == 0 && state.CurrentQuestion?.Method == TrainingMethod.TraceElement
            && drawing.IsVisible && surface.ImageSource is not null && surface.Regions.Count == 0 && surface.ReferenceRegions.Count == 0,
            "A tracing question opens a clean editable image and does not supply its hidden reference to the drawing surface");
        Check(VisibleSessionText(session).Contains(state.CurrentQuestion!.Answer.Name, StringComparison.Ordinal)
            && !((ComboBox)session.FindName("AnswerSelector")).IsVisible
            && !((Button)session.FindName("CheckAnswerButton")).IsEnabled,
            "Tracing names the element to locate, hides the name-answer selector and cannot submit an empty drawing");
        CheckTraceTools(session, drawing);
        session.Height = Math.Min(session.Height, 650); Pump();
        DrawTraceBox(drawing, RegionEditorTool.Rectangle, new(100, 70), new(150, 170));
        var submitted = drawing.GetGeometries().Select(RegionGeometryCodec.Serialize).ToArray();
        Check(surface.ReferenceRegions.Count == 0 && ((Button)session.FindName("CheckAnswerButton")).IsEnabled,
            "A completed learner region enables comparison while the reference remains undisclosed");
        Check(string.IsNullOrWhiteSpace(((TextBlock)session.FindName("OperationStatus")).Text),
            "Completing a valid learner drawing clears the earlier unfinished-polygon validation message");
        SaveScreenshot(session, Path.Combine(output, "native-trace-drawing-ru.png"));
        CompleteSessionTask(session, session.CheckAnswerAsync());
        var first = state.CurrentResult ?? throw new Exception("Trace check produced no result");
        Check(first.Outcome == TrainingOutcome.Compared && first.Trace is not null
            && Near(first.Trace.Comparison.Coverage, .5) && Near(first.Trace.Comparison.Precision ?? -1, 1)
            && Near(first.Trace.Comparison.ExcessFraction ?? -1, 0)
            && state.Summary.Compared == 1 && state.Summary.Correct == 0 && state.Summary.Incorrect == 0 && state.Summary.Accuracy is null,
            "A native half-reference drawing reports 50% coverage, 100% precision and no excess without assigning a pass/fail grade");
        Check(surface.ReferenceRegions.Count == 1 && !surface.CanEdit
            && first.Trace!.Regions.Select(RegionGeometryCodec.Serialize).SequenceEqual(submitted),
            "Checking freezes the exact learner drawing and reveals the reference only in the read-only comparison view");
        CheckTraceOverlayToggles(drawing);
        var frozen = RegionSnapshot(surface);
        surface.CanEdit = true; surface.Tool = RegionEditorTool.Rectangle;
        Stroke(surface, new(250, 30), new(300, 80));
        surface.SelectedRegionId = surface.Regions[0].Id;
        var deletion = surface.DeleteSelected();
        Check(!surface.CanEdit && !deletion && !drawing.ClearDrawing() && !drawing.Undo() && !drawing.Redo()
            && SameRegions(surface.Regions, frozen),
            "A submitted drawing remains locked against pointer edits, deletion, clearing and undo/redo");
        CompleteSessionTask(session, session.CheckAnswerAsync());
        Check(state.Results.Count == 1, "Repeated comparison cannot overwrite a frozen trace response");
        CheckTraceFeedbackVisible(session);
        SaveScreenshot(session, Path.Combine(output, "native-trace-feedback-ru.png"));

        CompleteSessionTask(session, session.NextAsync());
        drawing = TraceView(session);
        Check(drawing.Regions.Count == 0 && drawing.Surface.ReferenceRegions.Count == 0 && drawing.Surface.CanEdit,
            "Advancing to another tracing task clears both the preceding answer and its reference overlay");
        DrawTraceBox(drawing, RegionEditorTool.Rectangle, new(250, 70), new(300, 100));
        CompleteSessionTask(session, session.CheckAnswerAsync());
        var outside = state.CurrentResult!.Trace!.Comparison;
        Check(Near(outside.Coverage, 0) && Near(outside.Precision ?? -1, 0) && Near(outside.ExcessFraction ?? -1, 1),
            "A native drawing completely outside the reference reports zero coverage and precision with 100% excess");

        CompleteSessionTask(session, session.NextAsync());
        drawing = TraceView(session);
        DrawTraceBox(drawing, RegionEditorTool.Rectangle, new(0, 0), new(420, 280));
        CompleteSessionTask(session, session.CheckAnswerAsync());
        var oversized = state.CurrentResult!.Trace!.Comparison;
        Check(Near(oversized.Coverage, 1) && Near(oversized.Precision ?? -1, 10000d / 117600)
            && Near(oversized.ExcessFraction ?? -1, 1 - 10000d / 117600),
            "Covering the entire image reaches full coverage but reports its low precision and large excess area");

        CompleteSessionTask(session, session.NextAsync());
        drawing = TraceView(session);
        Check(state.CurrentQuestion!.Regions.Count == 2 && drawing.Surface.ReferenceRegions.Count == 0,
            "A multi-region tracing target also keeps its compound hole and separate island hidden until completion");
        DrawTraceBox(drawing, RegionEditorTool.Ellipse, new(40, 50), new(90, 110));
        session.SkipQuestion();
        Check(state.CurrentResult?.Outcome == TrainingOutcome.Skipped && state.CurrentResult.Trace is null,
            "Skipping after drawing records a skip without silently comparing the unfinished response");
        CompleteSessionTask(session, session.NextAsync());
        Check(state.IsFinished && state.Summary.Compared == 3 && state.Summary.Skipped == 1 && state.Summary.Answered == 3
            && state.Summary.RecognitionAnswered == 0 && state.Summary.Accuracy is null
            && ((TextBlock)session.FindName("ResultsSummary")).Text.Contains("не рассчитана", StringComparison.Ordinal),
            "Trace-only results separate three comparisons from one skip and do not invent recognition accuracy");
        SaveScreenshot(session, Path.Combine(output, "native-trace-results-ru.png"));
        CompleteSessionTask(session, session.ReviewQuestionAsync(3));
        var review = ReviewTraceView(session);
        Check(review.Surface.ReferenceRegions.Select(r => RegionGeometryCodec.Serialize(r.Geometry))
                .SequenceEqual(state.Questions[3].Regions.Select(r => RegionGeometryCodec.Serialize(r.Geometry)))
            && review.Regions.Count == 0 && !review.Surface.CanEdit,
            "A skipped tracing review preserves every original reference geometry, including its hole and separate ellipse");
        CompleteSessionTask(session, session.ReviewQuestionAsync(0));
        review = ReviewTraceView(session);
        Check(review.GetGeometries().Select(RegionGeometryCodec.Serialize).SequenceEqual(submitted)
            && review.Surface.ReferenceRegions.Count == 1 && !review.Surface.CanEdit,
            "Completed tracing review restores the frozen submitted geometry alongside the correct reference");
        CheckTraceOverlayToggles(review);
        SaveSessionBelowFold(session, Path.Combine(output, "native-trace-review-ru.png"));
        CompleteSessionTask(session, session.ReviewQuestionAsync(2));
        CompleteSessionTask(session, session.ReviewQuestionAsync(0));
        Check(ReviewTraceView(session).GetGeometries().Select(RegionGeometryCodec.Serialize).SequenceEqual(submitted)
            && first.Trace!.Regions.Select(RegionGeometryCodec.Serialize).SequenceEqual(submitted),
            "Repeated review and visibility changes do not mutate the accepted learner answer");
    }

    private static void CheckTraceTools(TrainingSessionWindow session, TrainingDrawingView drawing)
    {
        var surface = drawing.Surface;
        DrawTraceBox(drawing, RegionEditorTool.Rectangle, new(80, 50), new(210, 200));
        var rectangle = RegionSnapshot(surface);
        DrawTraceBox(drawing, RegionEditorTool.Ellipse, new(250, 60), new(310, 120));
        Check(surface.Regions.Count == 2 && surface.Regions.Any(r => r.Geometry.Kind == "ellipse"),
            "The tracing canvas creates independent rectangle and ellipse regions through native pointer gestures");
        ClickTraceCommand(drawing, "DrawingDeleteButton");
        Check(SameRegions(surface.Regions, rectangle),
            "Deleting a selected learner region preserves the other region");
        ClickTraceCommand(drawing, "DrawingUndoButton");
        var restored = surface.Regions.Count == 2;
        ClickTraceCommand(drawing, "DrawingRedoButton");
        Check(restored && SameRegions(surface.Regions, rectangle),
            "Tracing undo and redo restore a deletion with exact region identities and geometry");
        surface.SelectRegion(surface.Regions[0].Id); surface.BrushRadius = 12; SelectTraceTool(drawing, RegionEditorTool.Brush);
        Stroke(surface, new(205, 120), new(250, 120));
        Check(surface.Regions.Count == 1 && Contains(surface.Regions[0].Geometry, 245, 120),
            "A brush stroke expands the selected learner region in source-image coordinates");
        SelectTraceTool(drawing, RegionEditorTool.Eraser); Stroke(surface, new(140, 120), new(140, 120));
        Check(surface.Regions[0].Geometry.Rings.Count >= 2 && !Contains(surface.Regions[0].Geometry, 140, 120)
            && Contains(surface.Regions[0].Geometry, 100, 100),
            "The tracing eraser creates a transparent interior hole without losing the surrounding learner region");
        var hole = RegionSnapshot(surface);
        ClickTraceCommand(drawing, "DrawingUndoButton");
        restored = Contains(surface.Regions[0].Geometry, 140, 120);
        ClickTraceCommand(drawing, "DrawingRedoButton");
        Check(restored && SameRegions(surface.Regions, hole),
            "Undo and redo preserve the exact compound hole created by a learner eraser gesture");
        SelectTraceTool(drawing, RegionEditorTool.Polygon);
        surface.PointerDown(surface.ImageToViewport(new Point(280, 170)));
        surface.PointerDown(surface.ImageToViewport(new Point(330, 170)));
        var invalidSubmission = session.SubmitTraceAsync();
        CompleteSessionTask(session, invalidSubmission);
        Check(drawing.HasPendingOperation && !invalidSubmission.Result && session.Session.Results.Count == 0
            && surface.HasPendingPolygon && SameRegions(surface.Regions, hole),
            "Checking an unfinished two-point polygon preserves its pending vertices and committed drawing without grading");
        surface.PointerDown(surface.ImageToViewport(new Point(310, 225)));
        ClickTraceCommand(drawing, "DrawingFinishPolygonButton");
        Check(!drawing.HasPendingOperation && surface.Regions.Count == 2
            && surface.Regions.Any(r => r.Geometry.Kind == "polygon"),
            "A valid pending polygon can be completed into the same editable learner response");
        var multiple = RegionSnapshot(surface);
        ClickTraceCommand(drawing, "DrawingClearButton");
        var cleared = surface.Regions.Count == 0;
        ClickTraceCommand(drawing, "DrawingUndoButton");
        restored = SameRegions(surface.Regions, multiple);
        ClickTraceCommand(drawing, "DrawingRedoButton");
        Check(cleared && restored && surface.Regions.Count == 0,
            "Clearing a multi-region drawing is one reversible operation and redo restores the empty response");
        SelectTraceTool(drawing, RegionEditorTool.Pan);
        var zoom = surface.Zoom; surface.ZoomBy(1.25); Stroke(surface, new(200, 140), new(210, 145));
        Check(surface.Zoom > zoom && surface.Regions.Count == 0, "Learner zoom and pan change the viewport without creating regions");
        surface.FitToView(); Pump();
    }

    private static void CheckEnglishTraceSession(TrainingSessionWindow session, string output)
    {
        var state = session.Session;
        Check(state.CurrentQuestion?.Method == TrainingMethod.NameObjectImage && state.Questions.Count == 3,
            "A mixed English session executes recognition and tracing in its prepared method order");
        session.SelectAnswer(state.CurrentQuestion!.Answer.Key); session.CheckAnswer();
        CompleteSessionTask(session, session.NextAsync());
        var drawing = TraceView(session);
        Check(state.CurrentQuestion?.Method == TrainingMethod.TraceElement && drawing.IsVisible,
            "The same mixed session advances from a recognition answer to an editable tracing question");
        var heading = ((TextBlock)session.FindName("QuestionHeading")).Text;
        var languageHost = new HomeWindow(sourceDirectory: null, catalogDirectory: "", rememberLocation: false); languageHost.Show(); Pump();
        try
        {
            Click(languageHost, "LanguageButton"); Pump();
            Check(((TextBlock)session.FindName("QuestionHeading")).Text == heading
                && VisualChildren<Button>(drawing).Any(b => Equals(b.Content, "Fit")),
                "An external language change leaves the active tracing task and its tools in the prepared English language");
            Click(languageHost, "LanguageButton"); Pump();
        }
        finally { languageHost.Close(); }
        session.Height = Math.Min(session.Height, 650); Pump();
        DrawTraceBox(drawing, RegionEditorTool.Rectangle, new(100, 70), new(150, 170));
        SaveScreenshot(session, Path.Combine(output, "native-trace-drawing-en.png"));
        CompleteSessionTask(session, session.CheckAnswerAsync());
        Check(((TextBlock)session.FindName("FeedbackText")).Text.Contains("Drawing compared", StringComparison.Ordinal),
            "English feedback identifies a drawing comparison instead of a correct or incorrect recognition answer");
        CheckTraceFeedbackVisible(session);
        SaveScreenshot(session, Path.Combine(output, "native-trace-feedback-en.png"));
        CompleteSessionTask(session, session.NextAsync());
        RespondToFinish(session, "ConfirmFinishButton");
        Check(state.Summary.Correct == 1 && state.Summary.Compared == 1 && state.Summary.NotAnswered == 1
            && state.Summary.Answered == 2 && state.Summary.RecognitionAnswered == 1 && state.Summary.Accuracy == 1,
            "Mixed-session accuracy uses only one checked recognition answer while reporting its geometric comparison separately");
        SaveScreenshot(session, Path.Combine(output, "native-trace-results-en.png"));
        CompleteSessionTask(session, session.ReviewQuestionAsync(1));
        Check(ReviewTraceView(session).Regions.Count == 1 && ReviewTraceView(session).Surface.ReferenceRegions.Count == 1,
            "English mixed-session review restores both the submitted and reference drawing layers");
        SaveSessionBelowFold(session, Path.Combine(output, "native-trace-review-en.png"));
    }

    private static TrainingDrawingView TraceView(TrainingSessionWindow session) => (TrainingDrawingView)session.FindName("TraceDrawingView");
    private static TrainingDrawingView ReviewTraceView(TrainingSessionWindow session) => (TrainingDrawingView)session.FindName("ReviewDrawingView");

    private static void DrawTraceBox(TrainingDrawingView drawing, RegionEditorTool tool, Point start, Point end)
    {
        SelectTraceTool(drawing, tool); Stroke(drawing.Surface, start, end); Pump();
    }

    private static void SelectTraceTool(TrainingDrawingView drawing, RegionEditorTool tool)
    {
        var button = (RadioButton)drawing.FindName("Drawing" + tool + "Button");
        if (!button.IsEnabled) throw new Exception("Tracing tool is disabled: " + tool);
        button.IsChecked = true; Pump();
        if (drawing.Surface.Tool != tool) throw new Exception("Tracing tool did not activate: " + tool);
    }

    private static void ClickTraceCommand(TrainingDrawingView drawing, string name)
    {
        var button = (Button)drawing.FindName(name);
        if (!button.IsEnabled) throw new Exception("Tracing command is disabled: " + name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
    }

    private static void CheckTraceOverlayToggles(TrainingDrawingView drawing)
    {
        var surface = drawing.Surface;
        var before = RegionSnapshot(surface);
        var toggles = VisualChildren<CheckBox>(drawing).Where(c => c.IsVisible).ToArray();
        Check(toggles.Length == 2, "Comparison offers separate native visibility controls for the learner and reference layers");
        foreach (var toggle in toggles)
        {
            toggle.IsChecked = false; Pump();
            Check(surface.ShowRegions != surface.ShowReferenceRegions && SameRegions(surface.Regions, before),
                "Hiding one comparison layer preserves the other layer and the frozen response geometry");
            toggle.IsChecked = true; Pump();
        }
        Check(surface.ShowRegions && surface.ShowReferenceRegions, "Both comparison layers can be restored after independent inspection");
    }

    private static void CheckTraceFeedbackVisible(TrainingSessionWindow session)
    {
        Pump();
        var scroll = (ScrollViewer)session.FindName("ContentScroll");
        var viewport = (FrameworkElement?)scroll.Template.FindName("PART_ScrollContentPresenter", scroll)
            ?? throw new Exception("Trace feedback viewport is missing");
        var feedback = (FrameworkElement)session.FindName("FeedbackPanel");
        var bounds = feedback.TransformToAncestor(viewport).TransformBounds(new Rect(feedback.RenderSize));
        Check(feedback.IsVisible && bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1,
            "Geometric feedback remains visible after checking in a 650-pixel-tall session window");
    }
}

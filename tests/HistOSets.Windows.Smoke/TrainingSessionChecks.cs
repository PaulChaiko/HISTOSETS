using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HistOSets;
using HistOSets.Controls;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;

internal static partial class Program
{
    private static void CheckTrainingSessions(string temp, string output)
    {
        var root = Path.Combine(temp, "training-session-catalog");
        using var store = new CatalogStore(root);
        var ids = CreateSessionCatalog(store, temp);
        var setup = new TrainingSetupWindow(store);
        setup.Show(); WaitTraining(setup);
        try
        {
            if (setup.Catalog is null) throw new Exception("Session fixture did not load");
            // The preceding preparation checks leave the application in Russian.
            if (Equals(((Button)setup.FindName("StartSessionButton")).Content, "Start session"))
            { Click(setup, "LanguageButton"); WaitTraining(setup); }
            setup.SelectMaterials([ids[0]]);
            setup.SelectMethods([TrainingMethod.NameObjectDescription]);
            CompleteTrainingTask(setup, setup.BuildPlanAsync());
            var previousPlan = setup.Plan ?? throw new Exception("No prepared description plan");
            var edited = store.LoadMaterialDraft(ids[0]);
            edited.TrainingPrompts[0].Texts["ru"] += " Просветы различаются по диаметру.";
            store.SaveMaterialDraft(edited, ImageLoader.ReadMetadata);
            CompleteTrainingTask(setup, setup.StartSessionAsync());
            Check(setup.ActiveSession is null && setup.Plan is null && setup.Catalog?.SourceFingerprint != previousPlan.SourceFingerprint,
                "Starting after a source edit invalidates the old plan and requires a fresh explicit preparation");

            var unchanged = SessionCatalogBytes(root);
            setup.SelectMethods(Enum.GetValues<TrainingMethod>()); setup.SetOrder(TrainingOrder.ByMethod);
            CompleteTrainingTask(setup, setup.BuildPlanAsync());
            Check(setup.Plan?.QuestionCount == 6 && ((Button)setup.FindName("StartSessionButton")).IsEnabled,
                "A six-method plan enables recognition execution while preserving the deferred tracing question");
            RunSessionFromSetup(setup, session => CheckRussianSession(session, output));
            Check(SessionCatalogBytes(root) == unchanged,
                "A complete native five-method session leaves every catalog file and its contents unchanged");

            // Starting the same prepared plan again must create a new in-memory attempt.
            RunSessionFromSetup(setup, session =>
            {
                Check(session.Session.CurrentIndex == 0 && session.Session.Results.Count == 0,
                    "Re-entering a prepared session starts at the first question without previous answers");
                session.FinishSession();
                Check(session.Session.Summary.NotAnswered == 5 && session.Session.Summary.Accuracy is null
                    && ((TextBlock)session.FindName("ResultsSummary")).Text.Contains("не рассчитана", StringComparison.Ordinal),
                    "Ending before any answer records unanswered questions and visibly explains why accuracy is not calculated");
            });

            Click(setup, "LanguageButton"); WaitTraining(setup);
            setup.SelectMethods([TrainingMethod.NameObjectImage, TrainingMethod.NameElementRegion,
                TrainingMethod.NameObjectDescription, TrainingMethod.NameElementDescription]);
            CompleteTrainingTask(setup, setup.BuildPlanAsync());
            RunSessionFromSetup(setup, session => CheckEnglishSession(session, output));

            // The description-only material deliberately has no image, regions or ready status.
            setup.SelectMaterials([ids[^1]]); setup.SelectMethods([TrainingMethod.NameObjectDescription, TrainingMethod.NameElementDescription]);
            CompleteTrainingTask(setup, setup.BuildPlanAsync());
            RunSessionFromSetup(setup, session =>
            {
                Check(session.Session.Questions.Count == 2 && session.Session.Questions.All(q => q.MaterialImageId is null)
                    && !((TrainingImageView)session.FindName("MainImageView")).IsVisible,
                    "Description-only object and element questions execute without an image or a ready card");
                foreach (var question in session.Session.Questions)
                {
                    session.SelectAnswer(question.Answer.Key); session.CheckAnswer(); CompleteSessionTask(session, session.NextAsync());
                }
                Check(session.Session.Summary.Correct == 2 && session.Session.IsFinished,
                    "Both description-only scopes accept their canonical searchable-list answers");
            });
            Check(SessionCatalogBytes(root) == unchanged,
                "Re-entry, language changes, early completion and description-only training persist no attempt history or catalog edits");

            var catalog = store.CaptureTrainingCatalog(ImageLoader.ReadMetadata);
            var plan = TrainingPlanner.Build(catalog, new([ids[0]], [TrainingMethod.NameObjectImage], "en"));
            var picture = catalog.Materials.Single(m => m.Id == ids[0]).Images.Single();
            var path = picture.ImagePath ?? throw new Exception("Missing fixture image path");
            var original = File.ReadAllBytes(path);
            try
            {
                CreateSessionImage(path, 420, 280, 17);
                WithSession(catalog, plan, session =>
                {
                    Check(session.Session.CurrentResult?.Outcome == TrainingOutcome.Unavailable
                        && session.Session.Summary.Answered == 0 && session.Session.Summary.Incorrect == 0
                        && !((Button)session.FindName("CheckAnswerButton")).IsEnabled,
                        "An image changed after capture becomes unavailable instead of being graded as a wrong answer");
                    SaveScreenshot(session, Path.Combine(output, "native-session-unavailable-en.png"));
                    CompleteSessionTask(session, session.NextAsync());
                    Check(session.Session.Summary.Unavailable == 1 && session.Session.Summary.Accuracy is null
                        && ((TextBlock)session.FindName("ResultsSummary")).Text.Contains("not calculated", StringComparison.OrdinalIgnoreCase),
                        "A technically unavailable question is counted separately and visibly excluded from the answer denominator");
                });
            }
            finally { File.WriteAllBytes(path, original); }
            Check(SessionCatalogBytes(root) == unchanged,
                "Image failure handling writes no catalog data and never replaces or repairs the source image");
        }
        catch { if (setup.IsVisible) SaveScreenshot(setup, Path.Combine(output, "native-session-setup-failure.png")); throw; }
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

    private static Guid[] CreateSessionCatalog(CatalogStore store, string temp)
    {
        var analog = store.SaveAnalogGroup(null, "Сосудистая сеть", "Vascular network");
        var ids = new List<Guid>();
        var russian = new[] { "Образец альфа", "Образец бета", "Железистый комплекс", "Мышечная пластинка", "Нервный узел", "Покровная ткань" };
        var english = new[] { "Specimen alpha", "Specimen beta", "Glandular complex", "Muscular sheet", "Nerve ganglion", "Surface tissue" };
        for (var i = 0; i < russian.Length; i++)
        {
            var draft = new MaterialDraft { Id = Guid.Parse($"72220000-0000-4000-8000-{i + 1:000000000000}") };
            draft.Texts["ru"] = new() { Name = russian[i], Summary = "Обычный текст с ответом: " + russian[i], Description = "Атласное описание " + russian[i] };
            draft.Texts["en"] = new() { Name = english[i], Summary = "Ordinary answer-bearing summary: " + english[i], Description = "Atlas description " + english[i] };
            if (i < 2) draft.AnalogGroupId = analog;
            var element = new MaterialElementDraft();
            element.Texts["ru"] = new() { Name = i == 0 ? "Кольцевой участок" : "Внутренняя структура " + i };
            element.Texts["en"] = new() { Name = i == 0 ? "Annular profile" : "Inner structure " + i };
            if (i < 5)
            {
                var path = Path.Combine(temp, "session-source-" + i + ".png"); CreateSessionImage(path, 420 + i * 20, 280, i);
                var picture = new MaterialImageDraft { SourcePath = path, SourceSha256 = HashExchangeFile(path).ToLowerInvariant(), IsPrimary = true };
                draft.Images.Add(picture);
                element.Regions.Add(new() { MaterialImageId = picture.Id, Geometry = RegionGeometry.Compound([
                    [new(25, 30), new(185, 30), new(185, 200), new(25, 200)],
                    [new(70, 75), new(135, 75), new(135, 145), new(70, 145)]]) });
                element.Regions.Add(new() { MaterialImageId = picture.Id, Geometry = RegionGeometry.Ellipse(260, 90, 65, 100) });
            }
            if (i is 0 or 5)
            {
                var prompt = new TrainingPromptDraft();
                prompt.Texts["ru"] = i == 0 ? "Разветвлённые тонкие трубки образуют непрерывную систему." : "Несколько тесно прилегающих слоёв отделяют внутреннюю среду от наружной.";
                prompt.Texts["en"] = i == 0 ? "Branching narrow tubes form a continuous system." : "Several closely adjacent layers separate the inner environment from the exterior.";
                draft.TrainingPrompts.Add(prompt);
                var elementPrompt = new TrainingPromptDraft();
                elementPrompt.Texts["ru"] = i == 0 ? "Замкнутая полоса окружает светлый просвет." : "Небольшое плотное образование находится между соседними слоями.";
                elementPrompt.Texts["en"] = i == 0 ? "A closed band surrounds a pale lumen." : "A small dense formation lies between adjacent layers.";
                element.TrainingPrompts.Add(elementPrompt);
            }
            draft.Elements.Add(element);
            ids.Add(store.SaveMaterialDraft(draft, ImageLoader.ReadMetadata).Id);
        }
        return ids.ToArray();
    }

    private static void CheckRussianSession(TrainingSessionWindow session, string output)
    {
        var state = session.Session;
        Check(state.Questions.Count == 5 && state.DeferredQuestionCount == 1 && state.CurrentQuestion?.Method == TrainingMethod.NameObjectImage,
            "Native execution includes the five recognition methods and leaves tracing explicitly deferred");
        var first = state.CurrentQuestion!;
        var image = (TrainingImageView)session.FindName("MainImageView");
        Check(image.IsVisible && image.Surface.ImageSource is not null && image.Surface.Regions.Count == 0
            && !VisibleSessionText(session).Contains(first.Answer.Name, StringComparison.Ordinal)
            && !VisibleSessionText(session).Contains("Образец альфа", StringComparison.Ordinal),
            "The image recognition screen has no answer-bearing title, atlas description or contour overlay");
        Check(state.CurrentAnswers.Any(a => a.Name == "Сосудистая сеть")
            && state.CurrentAnswers.All(a => a.Name is not ("Образец альфа" or "Образец бета")),
            "Analog specimens are represented by one canonical group answer in the actual learner list");
        SaveScreenshot(session, Path.Combine(output, "native-session-image-ru.png"));
        RespondToFinish(session, "ContinueSessionButton", closeWindow: true);
        Check(!state.IsFinished && state.Results.Count == 0, "Continuing from the early-finish dialog preserves the pending question");
        var selector = (ComboBox)session.FindName("AnswerSelector");
        Check(selector.SelectedIndex == -1 && selector.Text.Length == 0 && !((Button)session.FindName("CheckAnswerButton")).IsEnabled,
            "A new question starts with an empty answer field and never preselects a catalog answer");
        selector.ApplyTemplate();
        var edit = (TextBox?)selector.Template.FindName("PART_EditableTextBox", selector) ?? throw new Exception("Answer dropdown is not editable");
        edit.Text = "сеть"; Pump();
        Check(selector.Items.Count == 1 && ((TrainingAnswer)selector.Items[0]).Key == first.Answer.Key,
            "Typing a name fragment filters the answer dropdown to matching canonical choices");
        selector.SelectedItem = selector.Items[0]; Pump();
        Check(selector.SelectedItem is TrainingAnswer selected && selected.Key == first.Answer.Key
            && ((Button)session.FindName("CheckAnswerButton")).IsEnabled,
            "Selecting a filtered native ComboBox item establishes a valid answer despite its text and selection event ordering");
        edit.Text = "текст без выбранного ответа"; Pump();
        session.CheckAnswer();
        Check(state.Results.Count == 0 && !((Button)session.FindName("CheckAnswerButton")).IsEnabled,
            "Editing a selected answer invalidates its identity; stale typed text cannot silently submit the previous choice");
        var wrong = state.CurrentAnswers.First(a => a.Key != first.Answer.Key);
        session.SelectAnswer(wrong.Key); session.CheckAnswer();
        Check(state.CurrentResult?.Outcome == TrainingOutcome.Incorrect
            && ((TextBlock)session.FindName("FeedbackText")).Text.Contains(first.Answer.Name, StringComparison.Ordinal),
            "A submitted wrong choice produces explicit feedback and only then reveals the correct canonical name");
        session.CheckAnswer();
        Check(state.Results.Count == 1 && !((Button)session.FindName("CheckAnswerButton")).IsEnabled,
            "Repeated submission cannot record a second answer or change an already checked result");
        SaveScreenshot(session, Path.Combine(output, "native-session-feedback-ru.png"));
        CompleteSessionTask(session, session.NextAsync());
        var choicesQuestion = state.CurrentQuestion!;
        Check(choicesQuestion.Method == TrainingMethod.ChooseImage
            && VisualChildren<TrainingImageView>((Panel)session.FindName("ImageChoicesPanel")).Count(v => v.IsVisible && v.Surface.ImageSource is not null) == 4,
            "The four-image task displays four decoded, independently selectable images");
        var choice = choicesQuestion.ImageChoices.Single(c => c.Answer.Key == choicesQuestion.Answer.Key);
        session.SelectImageChoice(choice.MaterialImageId);
        SaveScreenshot(session, Path.Combine(output, "native-session-four-images-ru.png"));
        SaveSessionBelowFold(session, Path.Combine(output, "native-session-four-images-lower-ru.png"));
        session.CheckAnswer();
        Check(state.CurrentResult?.Outcome == TrainingOutcome.Correct && state.CurrentResult.SelectedMaterialImageId == choice.MaterialImageId,
            "The selected offered image is checked by identity against its canonical answer class");
        CompleteSessionTask(session, session.NextAsync());
        CheckRegionSessionView(session);
        SaveScreenshot(session, Path.Combine(output, "native-session-element-ru.png"));
        session.SkipQuestion();
        Check(state.CurrentResult?.Outcome == TrainingOutcome.Skipped && state.Summary.Incorrect == 1,
            "An explicit skip is recorded separately from the preceding wrong answer");
        CompleteSessionTask(session, session.NextAsync());
        Check(state.CurrentQuestion?.Method == TrainingMethod.NameObjectDescription
            && ((TextBlock)session.FindName("DescriptionText")).Text == state.CurrentQuestion.Description && !image.IsVisible,
            "An object-description task presents the dedicated learning variant without an unrelated image");
        SaveScreenshot(session, Path.Combine(output, "native-session-description-ru.png"));
        session.SelectAnswer(state.CurrentQuestion!.Answer.Key); session.CheckAnswer();
        Check(state.CurrentResult?.Outcome == TrainingOutcome.Correct && state.CurrentResult.SelectedAnswer?.Name == "Сосудистая сеть",
            "The canonical analog-group answer is accepted for a special object description");
        CompleteSessionTask(session, session.NextAsync());
        Check(state.CurrentQuestion?.Method == TrainingMethod.NameElementDescription && !image.IsVisible,
            "The fifth recognition method executes an element description with the element answer pool");
        session.SelectAnswer(state.CurrentQuestion!.Answer.Key); session.CheckAnswer(); CompleteSessionTask(session, session.NextAsync());
        Check(state.IsFinished && state.Summary.Correct == 3 && state.Summary.Incorrect == 1 && state.Summary.Skipped == 1
            && state.Summary.NotAnswered == 0 && state.Summary.Accuracy == .75
            && ((FrameworkElement)session.FindName("ResultsPanel")).IsVisible
            && ((TextBlock)session.FindName("ResultsSummary")).Text.Contains("3 из 4", StringComparison.Ordinal),
            "Final results distinguish correct, incorrect and skipped outcomes and visibly calculate accuracy over four answered questions only");
        CompleteSessionTask(session, session.ReviewQuestionAsync(0));
        Check(((TextBlock)session.FindName("ReviewAnswerText")).Text.Contains(first.Answer.Name, StringComparison.Ordinal)
            && ((TrainingImageView)session.FindName("ReviewImageView")).Surface.ImageSource is not null,
            "Completed-question review displays the reference answer and reloads its image");
        SaveScreenshot(session, Path.Combine(output, "native-session-results-ru.png"));
        SaveSessionBelowFold(session, Path.Combine(output, "native-session-review-ru.png"));
    }

    private static void CheckEnglishSession(TrainingSessionWindow session, string output)
    {
        var state = session.Session;
        Check(state.Plan.Configuration.Locale == "en" && state.CurrentQuestion?.Answer.Name == "Vascular network",
            "English execution uses its prepared answer names and descriptions");
        var frozenHeading = ((TextBlock)session.FindName("QuestionHeading")).Text;
        var languageHost = new HomeWindow(sourceDirectory: null, catalogDirectory: "", rememberLocation: false); languageHost.Show(); Pump();
        try
        {
            Click(languageHost, "LanguageButton"); Pump();
            Check(((TextBlock)session.FindName("QuestionHeading")).Text == frozenHeading
                && Equals(VisualChildren<Button>((TrainingImageView)session.FindName("MainImageView")).Single(b => Equals(b.Content, "Fit")).Content, "Fit"),
                "An external application-language change leaves the active session and its image controls in their fixed English language");
            Click(languageHost, "LanguageButton"); Pump();
        }
        finally { languageHost.Close(); }
        session.SelectAnswer(state.CurrentQuestion!.Answer.Key); session.CheckAnswer(); CompleteSessionTask(session, session.NextAsync());
        CheckRegionSessionView(session);
        SaveScreenshot(session, Path.Combine(output, "native-session-element-en.png"));
        session.SelectAnswer(state.CurrentQuestion!.Answer.Key); session.CheckAnswer(); CompleteSessionTask(session, session.NextAsync());
        Check(((TextBlock)session.FindName("DescriptionText")).Text == "Branching narrow tubes form a continuous system.",
            "An English description uses the exact enabled English variant rather than the longer Russian translation");
        SaveScreenshot(session, Path.Combine(output, "native-session-description-en.png"));
        RespondToFinish(session, "ConfirmFinishButton");
        Check(state.IsFinished && state.Summary.Correct == 2 && state.Summary.NotAnswered == 2 && state.Summary.Skipped == 0,
            "Confirmed early completion retains checked results and marks remaining questions as unanswered, not skipped");
        CompleteSessionTask(session, session.ReviewQuestionAsync(1));
        Check(((TrainingImageView)session.FindName("ReviewImageView")).Surface.Regions.Count == 2,
            "Result review restores the element's entire saved reference-region group");
        SaveScreenshot(session, Path.Combine(output, "native-session-results-en.png"));
        SaveSessionBelowFold(session, Path.Combine(output, "native-session-review-en.png"));
    }

    private static void CheckRegionSessionView(TrainingSessionWindow session)
    {
        var question = session.Session.CurrentQuestion!;
        var surface = ((TrainingImageView)session.FindName("MainImageView")).Surface;
        var original = question.Regions.Select(r => RegionGeometryCodec.Serialize(r.Geometry)).ToArray();
        Check(question.Method == TrainingMethod.NameElementRegion && surface.Regions.Count == 2
            && surface.Regions.Select(r => RegionGeometryCodec.Serialize(r.Geometry)).SequenceEqual(original)
            && !VisibleSessionText(session).Contains(question.Answer.Name, StringComparison.Ordinal),
            "Element recognition displays all saved regions including the compound hole, without exposing the element name");
        surface.ActualSize(); var beforeZoom = surface.Zoom; surface.ZoomBy(1.25); Pump();
        Check(surface.Zoom > beforeZoom && !surface.CanEdit && surface.Tool == RegionEditorTool.Pan,
            "The native learner image supports zoom and pan with editing disabled");
        surface.SelectedRegionId = surface.Regions[0].Id;
        var deleted = surface.DeleteSelected();
        surface.PointerDown(new Point(80, 80)); surface.PointerMove(new Point(100, 100)); surface.PointerUp(new Point(100, 100)); surface.FitToView(); Pump();
        Check(!deleted && surface.Regions.Select(r => RegionGeometryCodec.Serialize(r.Geometry)).SequenceEqual(original),
            "Learner pointer navigation and deletion commands cannot modify the reference contours");
    }

    private static void RunSessionFromSetup(TrainingSetupWindow setup, Action<TrainingSessionWindow> action)
    {
        Exception? failure = null; var found = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            if (setup.ActiveSession is not { IsVisible: true } session) return;
            timer.Stop(); found = true;
            try { CompleteSessionTask(session, session.InitializeAsync()); action(session); }
            catch (Exception error) { failure = error; }
            finally { CloseSessionForCleanup(session); }
        };
        timer.Start();
        try { CompleteTrainingTask(setup, setup.StartSessionAsync()); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
        Check(found && setup.ActiveSession is null && !setup.IsBusy,
            "The preparation screen launches a real modal session and becomes usable again after its results close");
    }

    private static void WithSession(TrainingCatalog catalog, TrainingPlan plan, Action<TrainingSessionWindow> action)
    {
        var session = new TrainingSessionWindow(catalog, plan); session.Show();
        try { CompleteSessionTask(session, session.InitializeAsync()); action(session); }
        finally { CloseSessionForCleanup(session); }
    }

    private static void RespondToFinish(TrainingSessionWindow session, string buttonName, bool closeWindow = false)
    {
        Exception? failure = null; var found = false;
        session.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var prompt = session.FinishConfirmation;
            try
            {
                if (prompt is null) throw new Exception("Early-completion confirmation did not open");
                VisualChildren<Button>(prompt).Single(b => b.Name == buttonName).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); found = true;
            }
            catch (Exception error) { failure = error; prompt?.Close(); }
        }));
        if (closeWindow) session.Close(); else Click(session, "FinishButton");
        Pump();
        if (failure is not null) throw failure;
        Check(found, "Early-completion confirmation accepts " + buttonName);
    }

    private static void CloseSessionForCleanup(TrainingSessionWindow session)
    {
        if (!session.IsVisible) return;
        CompleteSessionTask(session, Task.CompletedTask);
        if (!session.Session.IsFinished) session.FinishSession();
        session.Close(); Pump();
    }

    private static void CompleteSessionTask(TrainingSessionWindow session, Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while ((!task.IsCompleted || session.IsBusy) && DateTime.UtcNow < deadline) Pump();
        if (!task.IsCompleted || session.IsBusy) throw new Exception("Recognition session remained busy");
        task.GetAwaiter().GetResult(); Pump();
    }

    private static void SaveSessionBelowFold(TrainingSessionWindow session, string path)
    {
        var scroll = (ScrollViewer)session.FindName("ContentScroll");
        scroll.ScrollToBottom(); Pump(); SaveScreenshot(session, path); scroll.ScrollToTop(); Pump();
    }

    private static string VisibleSessionText(Window session) => session.Title + "\n"
        + string.Join("\n", VisualChildren<TextBlock>(session).Where(t => t.IsVisible).Select(t => t.Text));

    private static string SessionCatalogBytes(string root) => string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path =>
        {
            var relative = Path.GetRelativePath(root, path);
            // CatalogStore owns this runtime lock with FileShare.None. Keep its presence in the
            // inventory, but hash every other file, including SQLite, images and metadata.
            return relative + ":" + (relative == "catalog.lock" ? "<exclusive runtime lock>" : HashExchangeFile(path));
        }));

    private static void CreateSessionImage(string path, int width, int height, int variant)
    {
        // Distinct patterned images make image choices and transparent contour holes visually reviewable.
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dx = (x + variant * 13) % 92 - 46; var dy = (y + variant * 7) % 74 - 37;
            var radius = dx * dx / 1.8 + dy * dy;
            var dark = radius < 280; var band = radius is >= 280 and < 530;
            var index = (y * width + x) * 4;
            pixels[index] = (byte)(dark ? 130 + variant % 5 * 9 : band ? 199 : 239 - variant % 4 * 6);
            pixels[index + 1] = (byte)(dark ? 72 + variant % 4 * 18 : band ? 135 + variant % 4 * 10 : 220 + variant % 5 * 4);
            pixels[index + 2] = (byte)(dark ? 101 + variant % 5 * 11 : band ? 214 - variant % 5 * 10 : 249 - variant % 4 * 8);
            pixels[index + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}

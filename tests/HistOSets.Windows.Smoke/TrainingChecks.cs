using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HistOSets;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;

internal static partial class Program
{
    private static void CheckTrainingWorkflow(string temp, string output)
    {
        var root = Path.Combine(temp, "training-native-catalog");
        Guid materialId;
        Guid tagId;
        Guid[] allIds;
        using (var store = new CatalogStore(root))
        {
            var topic = store.LoadTaxonomy().Groups.Single(g => g.Code == "topic").Id;
            tagId = store.SaveTag(null, topic, "Учебные описания", "Learning descriptions");
            var analog = store.SaveAnalogGroup(null, "Учебная группа", "Learning group");
            var ids = new List<Guid>();
            for (var i = 0; i < 5; i++)
            {
                var image = Path.Combine(temp, "training-source-" + i + ".png");
                CreateUniformImage(image, 320 + i * 20, 240);
                var draft = new MaterialDraft { Id = Guid.Parse($"71110000-0000-4000-8000-{i + 1:000000000000}") };
                draft.Texts["ru"] = new() { Name = "Препарат " + (i + 1), Summary = "Обычное краткое описание", Description = "Описание для просмотра, содержащее название препарата." };
                draft.Texts["en"] = new() { Name = "Specimen " + (i + 1), Summary = "Ordinary summary", Description = "The atlas description that can contain the specimen name." };
                var picture = new MaterialImageDraft { SourcePath = image, SourceSha256 = HashExchangeFile(image).ToLowerInvariant(), IsPrimary = true };
                draft.Images.Add(picture);
                var element = new MaterialElementDraft();
                element.Texts["ru"] = new() { Name = "Внутренняя структура", Summary = "Обычное описание элемента", Description = "Подробное описание внутренней структуры." };
                element.Texts["en"] = new() { Name = "Inner structure", Summary = "Element summary", Description = "Detailed description of the inner structure." };
                element.Regions.Add(new() { MaterialImageId = picture.Id, Geometry = RegionGeometry.Rectangle(40, 40, 100, 80) });
                draft.Elements.Add(element); draft.Status = "ready";
                if (i < 2) draft.AnalogGroupId = analog;
                if (i == 0) draft.TagIds.Add(tagId);
                ids.Add(store.SaveMaterialDraft(draft, ImageLoader.ReadMetadata).Id);
            }
            materialId = ids[0]; allIds = ids.ToArray();
            var editor = new EditorWindow(store, materialId); editor.Show(); Pump();
            try
            {
                if (Equals(((Button)editor.FindName("SaveDraftButton")).Content, "Save draft")) Click(editor, "LanguageButton");
                InModal<TrainingPromptWindow>(editor, "MaterialTrainingButton", dialog =>
                {
                    Check(((ListBox)dialog.FindName("TrainingPromptList")).Items.Count == 0 && ((TextBox)dialog.FindName("PromptRuText")).Text == "",
                        "The learning-description editor starts empty and never copies ordinary atlas descriptions containing answers");
                    Click(dialog, "AddPromptButton"); ((TextBox)dialog.FindName("PromptRuText")).Text = "Эти изменения будут отменены.";
                    RespondToPromptCancel(dialog, "ContinuePromptEditingButton");
                    Check(dialog.IsVisible && ((TextBox)dialog.FindName("PromptRuText")).Text == "Эти изменения будут отменены.",
                        "Continue editing in the prompt discard warning keeps the modal working copy intact");
                    RespondToPromptCancel(dialog, "DiscardPromptEditsButton");
                });
                InModal<TrainingPromptWindow>(editor, "MaterialTrainingButton", dialog =>
                {
                    Check(((ListBox)dialog.FindName("TrainingPromptList")).Items.Count == 0,
                        "Discarding prompt-dialog changes does not alter the parent card working copy");
                    Click(dialog, "AddPromptButton"); SetPromptText(dialog, "Тонкие слои образуют прозрачную поверхность.", "Thin layers form a transparent surface.");
                    Click(dialog, "AddPromptButton"); SetPromptText(dialog, "На поверхности видны параллельные пласты.", "");
                    Click(dialog, "MovePromptUpButton");
                    Check(((TextBox)dialog.FindName("PromptRuText")).Text == "На поверхности видны параллельные пласты.",
                        "Reordering learning variants retains the selected variant and its independent language fields");
                    Click(dialog, "MovePromptDownButton");
                    ((CheckBox)dialog.FindName("PromptEnabled")).IsChecked = false;
                    Click(dialog, "UndoPromptButton");
                    Check(((CheckBox)dialog.FindName("PromptEnabled")).IsChecked == true,
                        "Undo inside the learning-description dialog restores a variant's enabled state");
                    Click(dialog, "RemovePromptButton"); Click(dialog, "UndoPromptButton");
                    Check(((ListBox)dialog.FindName("TrainingPromptList")).Items.Count == 2,
                        "Undo inside the learning-description dialog restores a removed variant");
                    SaveScreenshot(dialog, Path.Combine(output, "native-training-prompts-ru.png"));
                    Click(dialog, "LanguageButton");
                    SaveScreenshot(dialog, Path.Combine(output, "native-training-prompts-en.png"));
                    Click(dialog, "LanguageButton");
                    Click(dialog, "ApplyPromptsButton");
                });
                Check(store.LoadMaterialDraft(materialId).TrainingPrompts.Count == 0,
                    "Applying learning descriptions changes only the parent working copy until the card is saved");
                Click(editor, "UndoButton");
                InModal<TrainingPromptWindow>(editor, "MaterialTrainingButton", dialog =>
                {
                    Check(((ListBox)dialog.FindName("TrainingPromptList")).Items.Count == 0,
                        "One parent undo removes the complete applied learning-description change");
                    Click(dialog, "CancelPromptsButton");
                });
                Click(editor, "RedoButton");
                ((TabControl)editor.FindName("FormTabs")).SelectedItem = editor.FindName("ElementsTab");
                ((ListBox)editor.FindName("ElementList")).SelectedIndex = 0;
                InModal<TrainingPromptWindow>(editor, "ElementTrainingButton", dialog =>
                {
                    Click(dialog, "AddPromptButton"); SetPromptText(dialog, "Найдите округлый участок в глубине поля.", "Locate the rounded part deep within the field.");
                    Click(dialog, "ApplyPromptsButton");
                });
                Click(editor, "ReadyButton");
                var saved = store.LoadMaterialDraft(materialId);
                Check(saved.Status == "ready" && saved.TrainingPrompts.Count == 2 && saved.Elements[0].TrainingPrompts.Count == 1
                    && saved.TrainingPrompts[0].Texts["en"] == "Thin layers form a transparent surface."
                    && saved.TrainingPrompts[1].Texts["en"] == ""
                    && saved.Texts["ru"].Description == "Описание для просмотра, содержащее название препарата.",
                    "Native save persists independent material and element variants without requiring every variant to have an English translation");
            }
            catch { SaveScreenshot(editor, Path.Combine(output, "native-training-editor-failure.png")); throw; }
            finally { if (editor.IsVisible) CloseEditorForCleanup(editor); }
        }
        using (var reopened = new CatalogStore(root))
        {
            var saved = reopened.LoadMaterialDraft(materialId);
            Check(saved.TrainingPrompts.Count == 2 && saved.Elements[0].TrainingPrompts.Single().Texts["en"] == "Locate the rounded part deep within the field.",
                "Reopening the catalog restores bilingual descriptions for both training scopes");
        }
        var home = new HomeWindow(sourceDirectory: null, catalogDirectory: root, rememberLocation: false); home.Show();
        try
        {
            WaitHome(home);
            if (Equals(((Button)home.FindName("ViewButton")).Content, "View")) Click(home, "LanguageButton");
            Check(((Button)home.FindName("SelfTestButton")).IsEnabled, "The home screen enables training preparation for its selected catalog");
            var before = ExchangeCatalogSnapshot(root);
            InModal<TrainingSetupWindow>(home, "SelfTestButton", setup =>
            {
                WaitTraining(setup);
                Check(setup.Catalog?.Materials.Count == 5 && setup.Plan is null && ((ItemsControl)setup.FindName("MethodList")).Items.Count == 6,
                    "Training preparation loads all cards and exposes all six methods without creating or starting an exercise");
                setup.SetTagFilters([tagId]); Pump();
                Check(((ItemsControl)setup.FindName("MaterialList")).Items.Count == 1,
                    "Training material selection can be narrowed by catalog tags");
                setup.SetTagFilters([]); ((TextBox)setup.FindName("SearchBox")).Text = "Specimen 1"; Pump();
                Check(((ItemsControl)setup.FindName("MaterialList")).Items.Count == 1,
                    "Training chooser searches across translations while displaying the current language");
                ((TextBox)setup.FindName("SearchBox")).Text = allIds[2].ToString("D"); Pump();
                Check(((ItemsControl)setup.FindName("MaterialList")).Items.Count == 1,
                    "An exact material UUID remains searchable independently of its localized names");
                ((TextBox)setup.FindName("SearchBox")).Text = "";
                setup.SelectMaterials([materialId]); setup.SelectMethods([TrainingMethod.NameObjectDescription]);
                CompleteTrainingTask(setup, setup.BuildPlanAsync());
                Check(setup.Plan?.Questions.Count(q => q.Method == TrainingMethod.NameObjectDescription) == 2,
                    "Russian preparation generates one independent question for each enabled Russian description variant");
                SaveScreenshot(setup, Path.Combine(output, "native-training-plan-ru.png"));
                Click(setup, "LanguageButton"); WaitTraining(setup);
                Check(setup.Plan is null, "Changing the training language invalidates the previous prepared plan");
                CompleteTrainingTask(setup, setup.BuildPlanAsync());
                Check(setup.Plan?.Questions.Count(q => q.Method == TrainingMethod.NameObjectDescription) == 1
                    && setup.Plan.Eligibility.Any(e => e.Issues.Any(i => i.Code == TrainingIssueCode.MissingDescriptionTranslation)),
                    "English preparation excludes a missing translation and explains why without substituting Russian text");
                SaveScreenshot(setup, Path.Combine(output, "native-training-plan-en.png"));
                setup.SelectMaterials(allIds); setup.SelectMethods([TrainingMethod.ChooseImage]); setup.SetOrder(TrainingOrder.ByObject);
                Check(setup.Plan is null, "Changing selected cards, methods or question order invalidates the prepared plan");
                CompleteTrainingTask(setup, setup.BuildPlanAsync());
                Check(setup.Plan is { QuestionCount: > 0 } && setup.Plan.Questions.All(q => q.ImageChoices.Count == 4
                    && q.ImageChoices.Select(c => c.Answer.Key).Distinct().Count() == 4),
                    "Image-choice preparation offers four distinct answer classes and never uses an analog as a false distractor");
                setup.SelectMaterials([materialId]); setup.SelectMethods(Enum.GetValues<TrainingMethod>());
                CompleteTrainingTask(setup, setup.BuildPlanAsync());
                Check(setup.Plan?.Eligibility.Count == 6 && setup.Plan.Questions.Any(q => q.Method == TrainingMethod.TraceElement),
                    "All six methods receive eligibility results and tracing is prepared from saved regions without starting an exercise");
                ((TabControl)setup.FindName("TrainingTabs")).SelectedItem = setup.FindName("SetupTab");
                SaveScreenshot(setup, Path.Combine(output, "native-training-setup-en.png"));
                var setupScroll = VisualChildren<ScrollViewer>(setup).Single(view => ReferenceEquals(view.Content, setup.FindName("SetupPanel")));
                setupScroll.ScrollToBottom(); Pump();
                SaveScreenshot(setup, Path.Combine(output, "native-training-methods-en.png"));
                setupScroll.ScrollToTop(); Pump();
                Click(setup, "LanguageButton"); WaitTraining(setup);
                SaveScreenshot(setup, Path.Combine(output, "native-training-setup-ru.png"));
                setupScroll.ScrollToBottom(); Pump();
                SaveScreenshot(setup, Path.Combine(output, "native-training-methods-ru.png"));
                setupScroll.ScrollToTop(); Pump();
            });
            Check(ExchangeCatalogSnapshot(root) == before,
                "Native training selection, previews, language changes and plan building leave all catalog bytes and file paths unchanged");
        }
        finally { home.Close(); }
    }

    private static void SetPromptText(Window dialog, string ru, string en)
    { ((TextBox)dialog.FindName("PromptRuText")).Text = ru; ((TextBox)dialog.FindName("PromptEnText")).Text = en; }
    private static void RespondToPromptCancel(TrainingPromptWindow dialog, string responseButton)
    {
        Exception? failure = null; var found = false;
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var prompt = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w.GetType().Name == "TrainingPromptDiscardPrompt");
            try
            {
                if (prompt is null) throw new Exception("Training discard warning did not open");
                var button = VisualChildren<Button>(prompt).Single(b => b.Name == responseButton);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); found = true;
            }
            catch (Exception error) { failure = error; prompt?.Close(); }
        }));
        Click(dialog, "CancelPromptsButton"); Pump();
        if (failure is not null) throw failure;
        Check(found, "Learning-description discard warning accepts " + responseButton);
    }
    private static void CloseTrainingPromptForCleanup(TrainingPromptWindow dialog)
    {
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var warning = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w.GetType().Name == "TrainingPromptDiscardPrompt");
            var discard = warning is null ? null : VisualChildren<Button>(warning).SingleOrDefault(b => b.Name == "DiscardPromptEditsButton");
            discard?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        dialog.Close(); Pump();
    }
    private static void WaitTraining(TrainingSetupWindow setup)
    {
        Pump(); var deadline = DateTime.UtcNow.AddSeconds(45);
        while (setup.IsBusy && DateTime.UtcNow < deadline) Pump();
        if (setup.IsBusy) throw new Exception("Training preparation stayed busy: " + ((TextBlock)setup.FindName("OperationStatus")).Text);
        Pump();
    }
    private static void CompleteTrainingTask(TrainingSetupWindow setup, Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Pump();
        if (!task.IsCompleted) throw new Exception("Training task timed out");
        task.GetAwaiter().GetResult(); WaitTraining(setup);
    }
}

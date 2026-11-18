using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HistOSets.Controls;
using HistOSets.Core;
using HistOSets.Services;

namespace HistOSets;

/// <summary>An in-memory recognition and drawing session with an immutable question language.</summary>
public partial class TrainingSessionWindow : Window
{
    private readonly TrainingCatalog catalog;
    private readonly string locale;
    private readonly List<(TrainingImageChoice Choice, RadioButton Selector, TrainingImageView View)> imageChoices = [];
    private readonly List<TrainingImageView> reviewChoices = [];
    private ICollectionView? answerView;
    private TrainingAnswer? selectedAnswer;
    private Guid? selectedImage;
    private LoadedImage? currentTraceImage;
    private string answerQuery = "";
    private bool initialized;
    private bool updating;
    private bool busy;
    private bool comparing;
    private bool traceValidationMessage;
    private bool closed;
    private int generation;
    private Task? initializationTask;
    private sealed record ResultRow(int Index, string Label, string Answer);

    public TrainingSession Session { get; }
    public bool IsBusy => busy;
    public Window? FinishConfirmation { get; private set; }

    public TrainingSessionWindow(TrainingCatalog catalog, TrainingPlan plan, TrainingSession? preparedSession = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(plan);
        if (catalog.CatalogId != plan.CatalogId || catalog.SourceFingerprint != plan.SourceFingerprint)
            throw new ArgumentException("The training catalog and plan must describe the same snapshot.", nameof(plan));
        if (preparedSession is not null && (!ReferenceEquals(preparedSession.Plan, plan) || preparedSession.IsFinished
            || preparedSession.CurrentIndex != 0 || preparedSession.Results.Count != 0))
            throw new ArgumentException("A prepared session must be unused and belong to this exact plan.", nameof(preparedSession));
        this.catalog = catalog;
        locale = plan.Configuration.Locale.ToLowerInvariant();
        Session = preparedSession ?? new(plan);
        InitializeComponent();
        MainImageView.SetLocale(locale);
        ReviewImageView.SetLocale(locale);
        TraceDrawingView.SetLocale(locale);
        ReviewDrawingView.SetLocale(locale);
        TraceDrawingView.DrawingChanged += TraceDrawing_Changed;
        AnswerSelector.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(Answer_TextChanged));
        initialized = true;
        Localize();
        RefreshCommands();
        Loaded += Window_Loaded;
        Closed += (_, _) =>
        {
            closed = true;
            generation++;
            ClearQuestionMedia();
            ClearReviewMedia();
            answerView = null;
            AnswerSelector.ItemsSource = null;
        };
    }

    private string T(string ru, string en) => locale == "en" ? en : ru;

    private void Localize()
    {
        Title = T("HISTOSETS — самопроверка", "HISTOSETS — self-test");
        Heading.Text = T("Самопроверка", "Self-test");
        LanguageHint.Text = T("Язык занятия: русский. Чтобы изменить язык, завершите занятие и вернитесь к настройке.",
            "Session language: English. To change it, finish this session and return to setup.");
        AnswerLabel.Text = T("Ваш ответ", "Your answer");
        AnswerHint.Text = T("Введите часть названия для поиска и выберите ответ из списка. Проверка выполняется только кнопкой «Проверить ответ».",
            "Type part of a name to search, then select an answer from the list. Your choice is checked only when you select Check answer.");
        CheckAnswerButton.Content = T("Проверить ответ", "Check answer");
        SkipButton.Content = T("Пропустить", "Skip");
        FinishButton.Content = T("Завершить занятие", "Finish session");
        CloseButton.Content = T("Закрыть результаты", "Close results");
        ResultsHeading.Text = T("Результаты занятия", "Session results");
        ResultsHint.Text = T("Выберите задание для разбора. Пропущенные, недоступные и оставшиеся без ответа задания показаны отдельно. Результаты сохраняются только в этом окне.",
            "Select a question to review. Skipped, unavailable and unanswered questions are counted separately. Results are kept only in this window.");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        await InitializeAsync();
    }

    public Task InitializeAsync() => initializationTask ??= LoadCurrentQuestionAsync();

    private async Task LoadCurrentQuestionAsync()
    {
        if (closed) return;
        if (Session.IsFinished) { ShowResults(); return; }
        var request = ++generation;
        var question = Session.CurrentQuestion!;
        SetBusy(true);
        updating = true;
        try
        {
            ClearQuestionMedia();
            selectedAnswer = null;
            selectedImage = null;
            answerQuery = "";
            AnswerSelector.ItemsSource = null;
            AnswerSelector.Text = "";
            FeedbackPanel.Visibility = Visibility.Collapsed;
            FeedbackText.Text = "";
            QuestionPanel.Visibility = Visibility.Visible;
            ResultsPanel.Visibility = Visibility.Collapsed;
            MethodText.Text = MethodLabel(question.Method);
            QuestionHeading.Text = QuestionText(question);
            DescriptionText.Text = question.Description ?? "";
            DescriptionPanel.Visibility = IsDescription(question) ? Visibility.Visible : Visibility.Collapsed;
            NamedAnswerPanel.Visibility = question.Method is TrainingMethod.ChooseImage or TrainingMethod.TraceElement ? Visibility.Collapsed : Visibility.Visible;
            answerView = new ListCollectionView(Session.CurrentAnswers.ToList());
            answerView.Filter = value => value is TrainingAnswer answer && (answerQuery.Length == 0 || SearchText(answer.Name).Contains(answerQuery, StringComparison.Ordinal));
            AnswerSelector.ItemsSource = answerView;
            AnswerSelector.SelectedIndex = -1;
            AnswerSelector.Text = "";
            ProgressText.Text = T($"Задание {Session.CurrentIndex + 1} из {Session.Questions.Count}",
                $"Question {Session.CurrentIndex + 1} of {Session.Questions.Count}");
            NextButton.Content = Session.CurrentIndex + 1 == Session.Questions.Count
                ? T("Показать результаты", "Show results") : T("Следующее задание", "Next question");
            ContentScroll.ScrollToTop();
        }
        finally { updating = false; }

        try
        {
            if (question.Method == TrainingMethod.ChooseImage)
            {
                ImageChoicesPanel.Visibility = Visibility.Visible;
                CreateQuestionImageChoices(question);
                OperationStatus.Text = T("Проверка и загрузка четырёх изображений…", "Verifying and loading four images…");
                var loaded = await Task.WhenAll(question.ImageChoices.Select(choice => Task.Run(() => TrainingImageLoader.Load(ResolveImage(choice)))));
                if (!IsCurrent(request)) return;
                for (var i = 0; i < loaded.Length; i++) imageChoices[i].View.ShowImage(loaded[i]);
            }
            else if (question.MaterialImageId is not null)
            {
                OperationStatus.Text = T("Проверка и загрузка изображения…", "Verifying and loading image…");
                var loaded = await Task.Run(() => TrainingImageLoader.Load(ResolveImage(question)));
                if (!IsCurrent(request)) return;
                if (question.Method == TrainingMethod.TraceElement)
                {
                    currentTraceImage = loaded;
                    TraceDrawingView.Visibility = Visibility.Visible;
                    TraceDrawingView.ShowImage(loaded);
                }
                else
                {
                    MainImageView.Visibility = Visibility.Visible;
                    MainImageView.ShowImage(loaded, question.Method == TrainingMethod.NameElementRegion ? question.Regions : null);
                }
            }
            if (!IsCurrent(request)) return;
            OperationStatus.Text = "";
        }
        catch (Exception ex)
        {
            if (!IsCurrent(request)) return;
            ClearQuestionMedia();
            Session.MarkUnavailable(MediaFailure(ex));
            OperationStatus.Text = T("Техническая недоступность не считается ошибочным ответом.", "Technical unavailability is not counted as an incorrect answer.");
            RenderFeedback();
        }
        finally
        {
            if (IsCurrent(request)) SetBusy(false);
        }
    }

    private static bool IsDescription(TrainingQuestion question) => question.Method is TrainingMethod.NameObjectDescription or TrainingMethod.NameElementDescription;
    private bool IsCurrent(int request) => !closed && request == generation;

    private TrainingImage ResolveImage(TrainingQuestion question)
        => ResolveImage(question.MaterialId, question.MaterialImageId ?? throw new InvalidOperationException("Image is missing."));

    private TrainingImage ResolveImage(TrainingImageChoice choice)
    {
        var image = ResolveImage(choice.MaterialId, choice.MaterialImageId);
        if (image.ImageId != choice.ImageId || !string.Equals(image.Sha256, choice.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Image snapshot does not match.");
        return image;
    }

    private TrainingImage ResolveImage(Guid materialId, Guid materialImageId)
        => catalog.Materials.FirstOrDefault(material => material.Id == materialId)?.Images.FirstOrDefault(image => image.MaterialImageId == materialImageId)
            ?? throw new InvalidOperationException("Image is missing from the snapshot.");

    private string MediaFailure(Exception error)
    {
        if (error is TrainingImageLoadException imageError)
            return imageError.Error switch
            {
                TrainingImageError.Changed => T("Изображение изменилось после подготовки занятия. Вернитесь к настройке и сформируйте новый план.",
                    "The image changed after this session was prepared. Return to setup and build a new plan."),
                TrainingImageError.InvalidImage => T("Изображение не удалось безопасно прочитать.", "The image could not be read safely."),
                _ => T("Изображение недоступно. Проверьте доступ к файлам и сформируйте новый план.",
                    "The image is unavailable. Check access to the files and build a new plan.")
            };
        return T("Материалы задания не удалось загрузить. Вернитесь к настройке и сформируйте новый план.",
            "The question media could not be loaded. Return to setup and build a new plan.");
    }

    private void CreateQuestionImageChoices(TrainingQuestion question)
    {
        for (var i = 0; i < question.ImageChoices.Count; i++)
        {
            var choice = question.ImageChoices[i];
            var selector = new RadioButton
            {
                Content = T($"Изображение {i + 1}", $"Image {i + 1}"),
                GroupName = "TrainingImageChoice", FontSize = 17, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8), IsEnabled = false
            };
            var view = new TrainingImageView { Height = 230 };
            view.SetLocale(locale);
            var body = new StackPanel();
            body.Children.Add(selector);
            body.Children.Add(view);
            var border = new Border
            {
                Child = body, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(219, 210, 233)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 10)
            };
            selector.Checked += (_, _) =>
            {
                if (updating || busy || Session.CurrentResult is not null || Session.IsFinished) return;
                selectedImage = choice.MaterialImageId;
                RefreshCommands();
            };
            imageChoices.Add((choice, selector, view));
            ImageChoicesPanel.Children.Add(border);
        }
    }

    private void ClearQuestionMedia()
    {
        traceValidationMessage = false;
        currentTraceImage = null;
        TraceDrawingView.Clear();
        TraceDrawingView.Visibility = Visibility.Collapsed;
        MainImageView.Clear();
        MainImageView.Visibility = Visibility.Collapsed;
        foreach (var item in imageChoices) item.View.Clear();
        imageChoices.Clear();
        ImageChoicesPanel.Children.Clear();
        ImageChoicesPanel.Visibility = Visibility.Collapsed;
    }

    private void ClearReviewMedia()
    {
        ReviewDrawingView.Clear();
        ReviewDrawingView.Visibility = Visibility.Collapsed;
        ReviewImageView.Clear();
        ReviewImageView.Visibility = Visibility.Collapsed;
        foreach (var view in reviewChoices) view.Clear();
        reviewChoices.Clear();
        ReviewChoicesPanel.Children.Clear();
        ReviewChoicesPanel.Visibility = Visibility.Collapsed;
    }

    private static string SearchText(string value) => value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('ё', 'е').Trim();

    private void Answer_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || updating || busy || Session.IsFinished || Session.CurrentResult is not null) return;
        var text = e.OriginalSource is TextBox editor ? editor.Text : AnswerSelector.Text;
        // WPF can update the editable text before raising SelectionChanged when a list item is chosen.
        if (AnswerSelector.SelectedItem is TrainingAnswer choice && string.Equals(text, choice.Name, StringComparison.Ordinal))
        {
            selectedAnswer = choice;
            RefreshCommands();
            return;
        }
        updating = true;
        try
        {
            // Typed text is a search query, never an answer and never a stale previous selection.
            selectedAnswer = null;
            AnswerSelector.SelectedItem = null;
            AnswerSelector.Text = text;
            answerQuery = SearchText(text);
            answerView?.Refresh();
            if (AnswerSelector.IsKeyboardFocusWithin) AnswerSelector.IsDropDownOpen = true;
        }
        finally { updating = false; }
        RefreshCommands();
    }

    private void Answer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updating || busy || Session.IsFinished || Session.CurrentResult is not null) return;
        if (AnswerSelector.SelectedItem is TrainingAnswer answer)
        {
            updating = true;
            try
            {
                selectedAnswer = answer;
                AnswerSelector.Text = answer.Name;
            }
            finally { updating = false; }
        }
        else selectedAnswer = null;
        RefreshCommands();
    }

    public void SelectAnswer(string key)
    {
        if (busy || closed || Session.IsFinished || Session.CurrentResult is not null) return;
        var answer = Session.CurrentAnswers.FirstOrDefault(candidate => candidate.Key == key)
            ?? throw new ArgumentException("Select an answer from the current question's list.", nameof(key));
        updating = true;
        try
        {
            answerQuery = "";
            answerView?.Refresh();
            selectedAnswer = answer;
            AnswerSelector.SelectedItem = answer;
            AnswerSelector.Text = answer.Name;
        }
        finally { updating = false; }
        RefreshCommands();
    }

    public void SelectImageChoice(Guid materialImageId)
    {
        if (busy || closed || Session.IsFinished || Session.CurrentResult is not null) return;
        var item = imageChoices.FirstOrDefault(value => value.Choice.MaterialImageId == materialImageId);
        if (item.Selector is null) throw new ArgumentException("Select an offered image.", nameof(materialImageId));
        item.Selector.IsChecked = true;
        selectedImage = materialImageId;
        RefreshCommands();
    }

    private bool HasAnswer => Session.CurrentQuestion?.Method switch
    {
        TrainingMethod.ChooseImage => selectedImage is not null,
        TrainingMethod.TraceElement => TraceDrawingView.CanSubmit || TraceDrawingView.HasPendingOperation,
        _ => selectedAnswer is not null && ReferenceEquals(AnswerSelector.SelectedItem, selectedAnswer)
            && string.Equals(AnswerSelector.Text, selectedAnswer.Name, StringComparison.Ordinal)
    };

    public void CheckAnswer()
    {
        if (busy || closed || Session.IsFinished || Session.CurrentResult is not null) return;
        if (Session.CurrentQuestion!.Method == TrainingMethod.TraceElement) { _ = SubmitTraceAsync(); return; }
        if (!HasAnswer) return;
        if (Session.CurrentQuestion!.Method == TrainingMethod.ChooseImage) Session.SubmitImage(selectedImage!.Value);
        else Session.SubmitAnswer(selectedAnswer!.Key);
        AnswerSelector.IsDropDownOpen = false;
        RenderFeedback();
    }

    public Task CheckAnswerAsync()
    {
        if (Session.CurrentQuestion?.Method == TrainingMethod.TraceElement) return SubmitTraceAsync();
        CheckAnswer();
        return Task.CompletedTask;
    }

    private void TraceDrawing_Changed(object? sender, EventArgs e)
    {
        if (traceValidationMessage && !busy && !closed && !Session.IsFinished && Session.CurrentResult is null
            && Session.CurrentQuestion?.Method == TrainingMethod.TraceElement && !TraceDrawingView.IsReadOnly)
        {
            traceValidationMessage = false;
            OperationStatus.Text = "";
        }
        RefreshCommands();
    }

    /// <summary>Finish a valid polygon and compare one frozen drawn answer without blocking input rendering.</summary>
    public async Task<bool> SubmitTraceAsync()
    {
        if (busy || closed || Session.IsFinished || Session.CurrentResult is not null
            || Session.CurrentQuestion?.Method != TrainingMethod.TraceElement || currentTraceImage is null) return false;
        if (!TraceDrawingView.TryFinishPendingDrawing())
        {
            traceValidationMessage = true;
            OperationStatus.Text = T("Завершите текущую область: для полигона нужны как минимум три вершины. Незавершённый рисунок не отправлен.",
                "Finish the current region: a polygon needs at least three vertices. The unfinished drawing has not been submitted.");
            RefreshCommands();
            return false;
        }
        if (!TraceDrawingView.CanSubmit)
        {
            traceValidationMessage = true;
            OperationStatus.Text = T("Нанесите хотя бы одну область, затем нажмите «Сравнить с эталоном».",
                "Draw at least one region, then select Compare with reference.");
            RefreshCommands();
            return false;
        }
        var request = generation;
        traceValidationMessage = false;
        comparing = true;
        SetBusy(true);
        TraceDrawingView.SetReadOnly();
        OperationStatus.Text = T("Сравнение областей…", "Comparing regions…");
        try
        {
            await Session.SubmitTraceAsync(TraceDrawingView.GetGeometries());
            if (!IsCurrent(request)) return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or OperationCanceledException)
        {
            if (!IsCurrent(request)) return false;
            TraceDrawingView.SetReadOnly(false);
            traceValidationMessage = true;
            OperationStatus.Text = T("Не удалось сравнить области. Рисунок сохранён: проверьте контуры, упростите их или пропустите задание.",
                "The regions could not be compared. Your drawing is kept: check or simplify its contours, or skip this question.");
            return false;
        }
        finally
        {
            comparing = false;
            if (IsCurrent(request)) SetBusy(false);
        }
        OperationStatus.Text = "";
        RenderFeedback();
        return true;
    }

    public void SkipQuestion()
    {
        if (busy || closed || Session.IsFinished || Session.CurrentResult is not null) return;
        Session.Skip();
        AnswerSelector.IsDropDownOpen = false;
        RenderFeedback();
    }

    private void RenderFeedback()
    {
        if (Session.CurrentResult is not { } result) return;
        traceValidationMessage = false;
        if (result.Question.Method == TrainingMethod.TraceElement && currentTraceImage is not null)
        {
            TraceDrawingView.ShowComparison(currentTraceImage, result.Trace?.Regions ?? [], result.Question.Regions);
            OperationStatus.Text = result.Trace is not null
                ? T("Рисунок зафиксирован. Переключайте слои ответа и эталона для сравнения.",
                    "The drawing is locked. Toggle the drawing and reference layers to compare their regions.")
                : T("Обведение не отправлено. Показаны эталонные области.", "No drawing was submitted. Reference regions are shown.");
        }
        FeedbackPanel.Visibility = Visibility.Visible;
        FeedbackText.Text = OutcomeLabel(result.Outcome);
        if (result.Outcome == TrainingOutcome.Unavailable) FeedbackText.Text += ". " + result.Reason;
        else FeedbackText.Text += "\n" + AnswerSummary(result);
        FeedbackPanel.Background = new SolidColorBrush(result.Outcome switch
        {
            TrainingOutcome.Correct => Color.FromRgb(229, 243, 232),
            TrainingOutcome.Incorrect => Color.FromRgb(249, 230, 230),
            _ => Color.FromRgb(238, 231, 247)
        });
        RefreshCommands();
        var request = generation;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsCurrent(request) || Session.IsFinished || Session.CurrentResult is null) return;
            // Measure the newly visible feedback before requesting its scroll position.
            ContentScroll.UpdateLayout();
            if (IsCurrent(request) && !Session.IsFinished && Session.CurrentResult is not null)
                FeedbackPanel.BringIntoView();
        }));
    }

    public async Task NextAsync()
    {
        if (busy || closed || Session.IsFinished || Session.CurrentResult is null) return;
        Session.Advance();
        if (Session.IsFinished) ShowResults();
        else await LoadCurrentQuestionAsync();
    }

    public void FinishSession()
    {
        if (closed || comparing || Session.IsFinished) return;
        generation++;
        busy = false;
        Session.End();
        ShowResults();
    }

    private void ShowResults()
    {
        generation++;
        busy = false;
        updating = true;
        try
        {
            ClearQuestionMedia();
            AnswerSelector.IsDropDownOpen = false;
            AnswerSelector.ItemsSource = null;
            answerView = null;
            selectedAnswer = null;
            selectedImage = null;
            QuestionPanel.Visibility = Visibility.Collapsed;
            ResultsPanel.Visibility = Visibility.Visible;
            ProgressText.Text = T("Занятие завершено", "Session finished");
            var summary = Session.Summary;
            ResultsSummary.Text = T($"Всего заданий: {summary.Total}\nРаспознавание — верно: {summary.Correct}   ·   неверно: {summary.Incorrect}\nСравнено обведений: {summary.Compared}\nПропущено: {summary.Skipped}   ·   Недоступно: {summary.Unavailable}   ·   Без ответа: {summary.NotAnswered}",
                $"Total questions: {summary.Total}\nRecognition — correct: {summary.Correct}   ·   incorrect: {summary.Incorrect}\nDrawings compared: {summary.Compared}\nSkipped: {summary.Skipped}   ·   Unavailable: {summary.Unavailable}   ·   Unanswered: {summary.NotAnswered}");
            var accuracy = summary.Accuracy?.ToString("P1", CultureInfo.GetCultureInfo(locale == "en" ? "en-US" : "ru-RU"));
            ResultsSummary.Text += "\n" + (accuracy is null
                ? T("Доля верных ответов не рассчитана: нет проверенных ответов на распознавание.", "Accuracy is not calculated: there are no checked recognition answers.")
                : T($"Среди ответов на распознавание верно: {summary.Correct} из {summary.RecognitionAnswered} ({accuracy}).",
                    $"Correct among recognition answers: {summary.Correct} of {summary.RecognitionAnswered} ({accuracy})."));
            if (Session.Questions.Any(question => question.Method == TrainingMethod.TraceElement))
                ResultsSummary.Text += "\n" + T("Обведения показаны отдельно: проценты сравнения доступны в разборе, общая оценка не выставляется.",
                    "Drawings are reported separately: comparison percentages are available in review, with no overall grade.");
            OperationStatus.Text = T("База данных не изменялась. Закрытие окна удалит результаты этого занятия.",
                "The catalog has not been changed. Closing this window discards this session's results.");
            ResultsList.ItemsSource = Session.Results.Select((result, index) => new ResultRow(index,
                $"{index + 1}. {MethodLabel(result.Question.Method)} · {OutcomeLabel(result.Outcome)}", ResultListSummary(result))).ToArray();
            ContentScroll.ScrollToTop();
        }
        finally { updating = false; }
        RefreshCommands();
        if (Session.Results.Count > 0) _ = ReviewQuestionAsync(0);
    }

    private string AnswerSummary(TrainingResult result)
    {
        if (result.Question.Method == TrainingMethod.TraceElement)
            return result.Trace is { } trace ? TraceSummary(trace.Comparison)
                : T("Обведение не отправлено. В разборе показаны только эталонные области.",
                    "No drawing was submitted. Review shows reference regions only.");
        var selected = result.SelectedAnswer?.Name ?? T("ответ не выбран", "no answer selected");
        if (result.SelectedMaterialImageId is { } id)
        {
            var number = result.Question.ImageChoices.Select((choice, index) => (choice, index)).First(item => item.choice.MaterialImageId == id).index + 1;
            selected = T($"изображение {number} — {selected}", $"image {number} — {selected}");
        }
        var expected = result.Question.Answer.Name;
        if (result.Question.Method == TrainingMethod.ChooseImage)
        {
            var number = result.Question.ImageChoices.Select((choice, index) => (choice, index)).First(item => item.choice.Answer.Key == result.Question.Answer.Key).index + 1;
            expected = T($"изображение {number} — {expected}", $"image {number} — {expected}");
        }
        return T($"Ваш ответ: {selected}\nЭталонный ответ: {expected}", $"Your answer: {selected}\nExpected answer: {expected}");
    }

    private string ResultListSummary(TrainingResult result)
    {
        if (result.Trace is not { } trace) return AnswerSummary(result);
        return T($"Покрыто эталона: {FormatPercent(trace.Comparison.Coverage)} · Ваше выделение вне эталона: {FormatPercent(trace.Comparison.ExcessFraction)}",
            $"Reference covered: {FormatPercent(trace.Comparison.Coverage)} · Your selection outside the reference: {FormatPercent(trace.Comparison.ExcessFraction)}");
    }

    private string FormatPercent(double? fraction) => fraction?.ToString("P1",
        CultureInfo.GetCultureInfo(locale == "en" ? "en-US" : "ru-RU")) ?? T("не рассчитано", "not calculated");

    private string TraceSummary(RegionComparisonResult comparison)
    {
        return T($"Покрыто эталона: {FormatPercent(comparison.Coverage)}\nВаше выделение внутри эталона: {FormatPercent(comparison.Precision)}\nВаше выделение вне эталона: {FormatPercent(comparison.ExcessFraction)}\nПокрытие рассчитано от площади эталона; две другие доли — от площади вашего выделения. Оценка «верно/неверно» не выставляется.",
            $"Reference covered: {FormatPercent(comparison.Coverage)}\nYour selection inside the reference: {FormatPercent(comparison.Precision)}\nYour selection outside the reference: {FormatPercent(comparison.ExcessFraction)}\nCoverage uses reference area; the other two percentages use your selected area. No correct/incorrect grade is assigned.");
    }

    public async Task ReviewQuestionAsync(int index)
    {
        if (closed || !Session.IsFinished) return;
        var results = Session.Results;
        if (index < 0 || index >= results.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var result = results[index];
        var question = result.Question;
        var request = ++generation;
        updating = true;
        try
        {
            ResultsList.SelectedIndex = index;
            ClearReviewMedia();
            ReviewHeading.Text = T($"Задание {index + 1}. ", $"Question {index + 1}. ") + QuestionText(question);
            ReviewAnswerText.Text = OutcomeLabel(result.Outcome) + "\n" + AnswerSummary(result);
            ReviewDescriptionPanel.Visibility = IsDescription(question) ? Visibility.Visible : Visibility.Collapsed;
            ReviewDescriptionText.Text = question.Description ?? "";
            ReviewStatus.Text = result.Reason ?? "";
        }
        finally { updating = false; }
        SetBusy(true);
        try
        {
            if (question.Method == TrainingMethod.ChooseImage)
            {
                ReviewStatus.Text = T("Загрузка изображений для разбора…", "Loading images for review…");
                var loaded = await Task.WhenAll(question.ImageChoices.Select(choice => Task.Run(() => TrainingImageLoader.Load(ResolveImage(choice)))));
                if (!IsCurrent(request)) return;
                ReviewChoicesPanel.Visibility = Visibility.Visible;
                for (var i = 0; i < loaded.Length; i++)
                {
                    var choice = question.ImageChoices[i];
                    var label = T($"Изображение {i + 1}", $"Image {i + 1}");
                    if (choice.Answer.Key == question.Answer.Key) label += T(" · эталон", " · expected");
                    if (choice.MaterialImageId == result.SelectedMaterialImageId) label += T(" · ваш выбор", " · your choice");
                    var view = new TrainingImageView { Height = 220 };
                    view.SetLocale(locale);
                    view.ShowImage(loaded[i]);
                    var body = new StackPanel();
                    body.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
                    body.Children.Add(view);
                    reviewChoices.Add(view);
                    ReviewChoicesPanel.Children.Add(new Border { Child = body, Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 10), Background = Brushes.White, CornerRadius = new CornerRadius(8) });
                }
            }
            else if (question.MaterialImageId is not null)
            {
                ReviewStatus.Text = T("Загрузка изображения для разбора…", "Loading image for review…");
                var loaded = await Task.Run(() => TrainingImageLoader.Load(ResolveImage(question)));
                if (!IsCurrent(request)) return;
                if (question.Method == TrainingMethod.TraceElement)
                {
                    ReviewDrawingView.Visibility = Visibility.Visible;
                    ReviewDrawingView.ShowComparison(loaded, result.Trace?.Regions ?? [], question.Regions);
                }
                else
                {
                    ReviewImageView.Visibility = Visibility.Visible;
                    ReviewImageView.ShowImage(loaded, question.Method == TrainingMethod.NameElementRegion ? question.Regions : null);
                }
            }
            if (IsCurrent(request)) ReviewStatus.Text = result.Reason ?? "";
        }
        catch (Exception ex)
        {
            if (!IsCurrent(request)) return;
            ClearReviewMedia();
            ReviewStatus.Text = MediaFailure(ex) + T(" Результат ответа сохранён без изменений.", " The recorded answer result has not changed.");
        }
        finally { if (IsCurrent(request)) SetBusy(false); }
    }

    private string MethodLabel(TrainingMethod method) => method switch
    {
        TrainingMethod.NameObjectImage => T("Объект по изображению", "Object from an image"),
        TrainingMethod.ChooseImage => T("Изображение по названию", "Image from a name"),
        TrainingMethod.NameElementRegion => T("Элемент по области", "Element from a region"),
        TrainingMethod.NameObjectDescription => T("Объект по описанию", "Object from a description"),
        TrainingMethod.NameElementDescription => T("Элемент по описанию", "Element from a description"),
        TrainingMethod.TraceElement => T("Обведение элемента", "Trace an element"),
        _ => T("Задание", "Question")
    };

    private string QuestionText(TrainingQuestion question) => question.Method switch
    {
        TrainingMethod.NameObjectImage => T("Какой объект показан на изображении?", "Which object is shown in the image?"),
        TrainingMethod.ChooseImage => T("Выберите изображение: ", "Select the image: ") + question.Answer.Name,
        TrainingMethod.NameElementRegion => T("Какой элемент выделен на изображении?", "Which element is highlighted in the image?"),
        TrainingMethod.NameObjectDescription => T("Какой объект соответствует описанию?", "Which object matches the description?"),
        TrainingMethod.NameElementDescription => T("Какой элемент соответствует описанию?", "Which element matches the description?"),
        TrainingMethod.TraceElement => T("Обведите все области элемента: ", "Trace all regions of the element: ") + question.Answer.Name,
        _ => T("Задание", "Question")
    };

    private string OutcomeLabel(TrainingOutcome outcome) => outcome switch
    {
        TrainingOutcome.Correct => T("Верно", "Correct"),
        TrainingOutcome.Incorrect => T("Неверно", "Incorrect"),
        TrainingOutcome.Compared => T("Обведение сравнено", "Drawing compared"),
        TrainingOutcome.Skipped => T("Пропущено", "Skipped"),
        TrainingOutcome.Unavailable => T("Недоступно", "Unavailable"),
        _ => T("Без ответа", "Unanswered")
    };

    private void SetBusy(bool value) { busy = value; RefreshCommands(); }

    private void RefreshCommands()
    {
        if (!initialized) return;
        var finished = Session.IsFinished;
        var pending = !finished && Session.CurrentResult is null;
        CheckAnswerButton.Content = Session.CurrentQuestion?.Method == TrainingMethod.TraceElement
            ? T("Сравнить с эталоном", "Compare with reference") : T("Проверить ответ", "Check answer");
        AnswerSelector.IsEnabled = !busy && pending;
        foreach (var item in imageChoices) item.Selector.IsEnabled = !busy && pending;
        CheckAnswerButton.Visibility = SkipButton.Visibility = FinishButton.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
        CheckAnswerButton.IsEnabled = !busy && pending && HasAnswer;
        SkipButton.IsEnabled = !busy && pending;
        FinishButton.IsEnabled = !comparing;
        NextButton.Visibility = !finished && !pending ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = !busy && !pending;
        CloseButton.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool ConfirmFinish()
    {
        if (comparing)
        {
            OperationStatus.Text = T("Дождитесь завершения сравнения областей.", "Wait for the region comparison to finish.");
            return false;
        }
        if (FinishConfirmation is not null) { FinishConfirmation.Activate(); return false; }
        var accepted = false;
        var remaining = Session.RemainingQuestionCount;
        var text = new TextBlock
        {
            Text = T($"Завершить занятие? Заданий без ответа: {remaining}. Они будут отмечены отдельно. Уже полученные результаты останутся доступны для разбора.",
                $"Finish this session? Unanswered questions: {remaining}. They will be marked separately. Your existing results will remain available for review."),
            TextWrapping = TextWrapping.Wrap, FontSize = 16, Margin = new Thickness(0, 0, 0, 18)
        };
        var resume = new Button { Name = "ContinueSessionButton", Content = T("Продолжить занятие", "Continue session"), IsCancel = true, IsDefault = true };
        var finish = new Button { Name = "ConfirmFinishButton", Content = T("Завершить и показать результаты", "Finish and show results"), Margin = new Thickness(0) };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(resume);
        buttons.Children.Add(finish);
        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(text);
        content.Children.Add(buttons);
        var dialog = new Window
        {
            Title = T("Завершение занятия", "Finish session"), Content = content, Owner = this,
            Width = Math.Min(620, SystemParameters.WorkArea.Width), SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(244, 241, 248)), ShowInTaskbar = false
        };
        resume.Click += (_, _) => dialog.DialogResult = false;
        finish.Click += (_, _) => { accepted = true; dialog.DialogResult = true; };
        FinishConfirmation = dialog;
        try { dialog.ShowDialog(); }
        finally { FinishConfirmation = null; }
        return accepted;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (Session.IsFinished) return;
        e.Cancel = true;
        if (ConfirmFinish()) FinishSession();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || AnswerSelector.IsDropDownOpen) return;
        if (TraceDrawingView.HasPendingOperation)
        {
            TraceDrawingView.CancelPendingDrawing();
            e.Handled = true;
            return;
        }
        e.Handled = true;
        if (Session.IsFinished) Close();
        else if (ConfirmFinish()) FinishSession();
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAnswerAsync();
    private void Skip_Click(object sender, RoutedEventArgs e) => SkipQuestion();
    private async void Next_Click(object sender, RoutedEventArgs e) => await NextAsync();
    private void Finish_Click(object sender, RoutedEventArgs e) { if (ConfirmFinish()) FinishSession(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Result_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!updating && ResultsList.SelectedItem is ResultRow row) await ReviewQuestionAsync(row.Index);
    }
}

namespace HistOSets.Core;

public enum TrainingOutcome { Correct, Incorrect, Skipped, Unavailable, NotAnswered, Compared }

/// <summary>A frozen drawing and its geometric comparison, without a pass/fail threshold.</summary>
public sealed class TrainingTraceAnswer
{
    internal TrainingTraceAnswer(IEnumerable<RegionGeometry> regions, RegionComparisonResult comparison)
    {
        Regions = TrainingFrozen.List(regions);
        Comparison = comparison;
    }

    public IReadOnlyList<RegionGeometry> Regions { get; }
    public RegionComparisonResult Comparison { get; }
}

/// <summary>A completed question result. The prepared question and the submitted choice never change.</summary>
public sealed record TrainingResult(TrainingQuestion Question, TrainingOutcome Outcome,
    TrainingAnswer? SelectedAnswer = null, Guid? SelectedMaterialImageId = null, string? Reason = null,
    TrainingTraceAnswer? Trace = null);

/// <summary>Counts describe all executable questions; Accuracy is the recognition-only ratio, not a drawing grade.</summary>
public sealed class TrainingSummary
{
    internal TrainingSummary(int total, IReadOnlyList<TrainingResult> results)
    {
        Total = total;
        Correct = results.Count(r => r.Outcome == TrainingOutcome.Correct);
        Incorrect = results.Count(r => r.Outcome == TrainingOutcome.Incorrect);
        Compared = results.Count(r => r.Outcome == TrainingOutcome.Compared);
        Skipped = results.Count(r => r.Outcome == TrainingOutcome.Skipped);
        Unavailable = results.Count(r => r.Outcome == TrainingOutcome.Unavailable);
        NotAnswered = results.Count(r => r.Outcome == TrainingOutcome.NotAnswered);
        Pending = total - results.Count;
    }

    public int Total { get; }
    public int Correct { get; }
    public int Incorrect { get; }
    public int Compared { get; }
    public int RecognitionAnswered => Correct + Incorrect;
    public int Answered => RecognitionAnswered + Compared;
    public int Skipped { get; }
    public int Unavailable { get; }
    public int NotAnswered { get; }
    public int Pending { get; }
    public double? Accuracy => RecognitionAnswered == 0 ? null : (double)Correct / RecognitionAnswered;
}

/// <summary>
/// An in-memory training session over an immutable prepared plan. An answer is submitted once,
/// feedback remains current until Advance, and no attempt data is written to the catalog.
/// Recognition is checked by answer identity; tracing receives quantitative geometric feedback.
/// </summary>
public sealed class TrainingSession
{
    private readonly List<TrainingResult> results = [];
    private readonly IReadOnlyDictionary<string, TrainingAnswer> objectAnswers;
    private readonly IReadOnlyDictionary<string, TrainingAnswer> elementAnswers;
    private int currentIndex;

    public TrainingSession(TrainingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan;
        Questions = TrainingFrozen.List(plan.Questions);
        if (Questions.Count == 0)
            throw new ArgumentException("The plan contains no executable questions.", nameof(plan));
        if (plan.Questions.Any(q => string.IsNullOrWhiteSpace(q.Key))
            || plan.Questions.Select(q => q.Key).Distinct(StringComparer.Ordinal).Count() != plan.QuestionCount)
            throw new ArgumentException("Prepared question keys must be nonempty and unique.", nameof(plan));

        objectAnswers = BuildAnswerIndex(plan.ObjectAnswers, nameof(plan));
        elementAnswers = BuildAnswerIndex(plan.ElementAnswers, nameof(plan));
        foreach (var question in Questions)
        {
            if (question.Method == TrainingMethod.ChooseImage)
            {
                if (question.ImageChoices.Count != 4
                    || question.ImageChoices.Any(c => c.MaterialImageId == Guid.Empty)
                    || question.ImageChoices.Select(c => c.MaterialImageId).Distinct().Count() != 4
                    || question.ImageChoices.Any(c => string.IsNullOrWhiteSpace(c.Sha256))
                    || question.ImageChoices.Select(c => c.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4
                    || question.ImageChoices.Select(c => c.Answer.Key).Distinct(StringComparer.Ordinal).Count() != 4
                    || question.ImageChoices.Count(c => c.Answer.Key == question.Answer.Key) != 1
                    || question.ImageChoices.Any(c => !objectAnswers.ContainsKey(c.Answer.Key)))
                    throw new ArgumentException("An image question must contain four distinct, named choices with one correct answer.", nameof(plan));
            }
            else
            {
                var answers = question.Method == TrainingMethod.TraceElement ? elementAnswers : AnswerIndex(question.Method);
                if (!answers.ContainsKey(question.Answer.Key))
                    throw new ArgumentException("The correct answer must belong to the question's prepared answer pool.", nameof(plan));
                if (question.Method == TrainingMethod.TraceElement)
                {
                    if (question.MaterialId == Guid.Empty || !question.ElementId.HasValue || question.ElementId == Guid.Empty
                        || !question.MaterialImageId.HasValue || question.MaterialImageId == Guid.Empty
                        || question.Regions.Count == 0 || question.Regions.Any(r => r is null || r.Id == Guid.Empty || r.MaterialImageId != question.MaterialImageId)
                        || question.Regions.Select(r => r.Id).Distinct().Count() != question.Regions.Count)
                        throw new ArgumentException("A tracing question requires one element, one image and distinct matching reference regions.", nameof(plan));
                    RegionComparison.Compare(question.Regions.Select(r => r.Geometry), []);
                }
            }
        }
    }

    public TrainingPlan Plan { get; }
    public IReadOnlyList<TrainingQuestion> Questions { get; }
    /// <summary>Retained for callers prepared for deferred methods; all six methods are now executable.</summary>
    public int DeferredQuestionCount => 0;
    public int CurrentIndex => currentIndex;
    public bool IsFinished => currentIndex >= Questions.Count;
    public TrainingQuestion? CurrentQuestion => IsFinished ? null : Questions[currentIndex];
    public TrainingResult? CurrentResult => !IsFinished && results.Count > currentIndex ? results[currentIndex] : null;

    /// <summary>A frozen result snapshot, safe to retain while the session advances.</summary>
    public IReadOnlyList<TrainingResult> Results => TrainingFrozen.List(results);
    public TrainingSummary Summary => new(Questions.Count, results);
    public int RemainingQuestionCount => Questions.Count - results.Count;
    public IReadOnlyList<TrainingAnswer> CurrentAnswers => CurrentQuestion?.Method switch
    {
        TrainingMethod.NameObjectImage or TrainingMethod.NameObjectDescription => Plan.ObjectAnswers,
        TrainingMethod.NameElementRegion or TrainingMethod.NameElementDescription => Plan.ElementAnswers,
        _ => Array.Empty<TrainingAnswer>()
    };

    /// <summary>Accepts a canonical key selected from CurrentAnswers, never arbitrary typed display text.</summary>
    public TrainingResult SubmitAnswer(string key)
    {
        var question = RequirePendingQuestion();
        if (question.Method == TrainingMethod.ChooseImage)
            throw new InvalidOperationException("This question requires an image choice.");
        if (question.Method == TrainingMethod.TraceElement)
            throw new InvalidOperationException("This question requires a drawn answer.");
        ArgumentNullException.ThrowIfNull(key);
        if (!AnswerIndex(question.Method).TryGetValue(key, out var selected))
            throw new ArgumentException("Select an answer from this question's prepared list.", nameof(key));
        return Complete(new(question, selected.Key == question.Answer.Key ? TrainingOutcome.Correct : TrainingOutcome.Incorrect,
            SelectedAnswer: selected));
    }

    /// <summary>Accepts only a material-image UUID offered in the current question.</summary>
    public TrainingResult SubmitImage(Guid materialImageId)
    {
        var question = RequirePendingQuestion();
        if (question.Method != TrainingMethod.ChooseImage)
            throw new InvalidOperationException("This question does not accept an image choice.");
        var selected = question.ImageChoices.SingleOrDefault(c => c.MaterialImageId == materialImageId)
            ?? throw new ArgumentException("Select an image offered in the current question.", nameof(materialImageId));
        return Complete(new(question, selected.Answer.Key == question.Answer.Key ? TrainingOutcome.Correct : TrainingOutcome.Incorrect,
            selected.Answer, selected.MaterialImageId));
    }

    /// <summary>Freezes one completed drawing and compares union areas; it does not invent a correct/incorrect grade.</summary>
    public TrainingResult SubmitTrace(IEnumerable<RegionGeometry> regions)
    {
        var (question, frozenRegions) = PrepareTrace(regions);
        return Complete(new(question, TrainingOutcome.Compared, Trace: CompareTrace(question, frozenRegions)));
    }

    /// <summary>Computes geometry off the caller's context and commits only while the captured question remains pending.</summary>
    public async Task<TrainingResult> SubmitTraceAsync(IEnumerable<RegionGeometry> regions, CancellationToken cancellationToken = default)
    {
        var (question, frozenRegions) = PrepareTrace(regions);
        cancellationToken.ThrowIfCancellationRequested();
        // Keep completion asynchronous even for tiny drawings, while retaining the caller's UI context.
        var answer = await Task.Run(() => CompareTrace(question, frozenRegions), cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(question, RequirePendingQuestion()))
            throw new InvalidOperationException("The tracing question changed while its answer was being compared.");
        return Complete(new(question, TrainingOutcome.Compared, Trace: answer));
    }

    private (TrainingQuestion Question, IReadOnlyList<RegionGeometry> Regions) PrepareTrace(IEnumerable<RegionGeometry> regions)
    {
        var question = RequirePendingQuestion();
        if (question.Method != TrainingMethod.TraceElement)
            throw new InvalidOperationException("This question does not accept a drawn answer.");
        ArgumentNullException.ThrowIfNull(regions);
        var frozenRegions = TrainingFrozen.List(regions);
        if (frozenRegions.Count == 0)
            throw new ArgumentException("A drawn answer requires at least one filled region.", nameof(regions));
        return (question, frozenRegions);
    }

    private static TrainingTraceAnswer CompareTrace(TrainingQuestion question, IReadOnlyList<RegionGeometry> regions)
    {
        var comparison = RegionComparison.Compare(question.Regions.Select(r => r.Geometry), regions);
        if (comparison.DrawnArea <= 0)
            throw new ArgumentException("A drawn answer requires positive filled area.", nameof(regions));
        return new(regions, comparison);
    }

    public TrainingResult Skip() => Complete(new(RequirePendingQuestion(), TrainingOutcome.Skipped));

    /// <summary>Records a technical inability to display the question separately from a learner's answer.</summary>
    public TrainingResult MarkUnavailable(string reason)
    {
        var question = RequirePendingQuestion();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return Complete(new(question, TrainingOutcome.Unavailable, Reason: reason.Trim()));
    }

    public void Advance()
    {
        if (IsFinished || CurrentResult is null)
            throw new InvalidOperationException("Complete the current question before advancing.");
        currentIndex++;
    }

    /// <summary>Ends early without inventing answers. Previously submitted feedback remains in Results.</summary>
    public void End()
    {
        if (IsFinished) return;
        for (var i = results.Count; i < Questions.Count; i++)
            results.Add(new(Questions[i], TrainingOutcome.NotAnswered));
        currentIndex = Questions.Count;
    }

    private TrainingQuestion RequirePendingQuestion()
    {
        if (IsFinished) throw new InvalidOperationException("The session is finished.");
        if (CurrentResult is not null) throw new InvalidOperationException("The current question has already been completed.");
        return Questions[currentIndex];
    }

    private TrainingResult Complete(TrainingResult result)
    {
        results.Add(result);
        return result;
    }

    private IReadOnlyDictionary<string, TrainingAnswer> AnswerIndex(TrainingMethod method) => method switch
    {
        TrainingMethod.NameObjectImage or TrainingMethod.NameObjectDescription => objectAnswers,
        TrainingMethod.NameElementRegion or TrainingMethod.NameElementDescription => elementAnswers,
        _ => throw new ArgumentException("The plan contains an unsupported named-answer method.", nameof(method))
    };

    private static IReadOnlyDictionary<string, TrainingAnswer> BuildAnswerIndex(IReadOnlyList<TrainingAnswer> answers, string parameterName)
    {
        var index = new Dictionary<string, TrainingAnswer>(StringComparer.Ordinal);
        foreach (var answer in answers)
            if (string.IsNullOrWhiteSpace(answer.Key) || string.IsNullOrWhiteSpace(answer.Name) || !index.TryAdd(answer.Key, answer))
                throw new ArgumentException("Prepared answer keys must be unique, with nonempty keys and names.", parameterName);
        return index;
    }
}

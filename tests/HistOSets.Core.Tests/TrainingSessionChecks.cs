using HistOSets.Core;

internal static class TrainingSessionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        void Reject<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { check(true, "Session: " + message); return; }
            throw new Exception($"Expected {typeof(T).Name}: {message}");
        }

        static Guid Id(int value) => new(value, 0, 0, new byte[8]);
        static Dictionary<string, string> Names(string ru, string en) => new() { ["ru"] = ru, ["en"] = en };
        static TrainingImage Image(int id) => new(Id(id), Id(id + 1000), "image-" + id, true, 0, 100, 100, true);
        static TrainingDescription Description(int id, string ru, string en) => new(Id(id), Names(ru, en), true, 0);
        var group = new TrainingAnalogGroup(Id(500), Names("Кожа", "Skin"));
        var region = new TrainingRegion(Id(301), Id(101), RegionGeometry.Rectangle(10, 10, 25, 40));
        var epithelium = new TrainingElement(Id(401), Names("Эпителий", "Epithelium"), 0, [region],
            [Description(601, "Сплошной пласт тесно расположенных клеток.", "A continuous sheet of closely arranged cells.")]);
        var vessel = new TrainingElement(Id(402), Names("Сосуд", "Vessel"), 0, [], []);
        var material = new TrainingMaterial(Id(1), 1, "ready", Names("Поле первое", "First field"), group.Id, [], [Image(101)],
            [epithelium], [Description(602, "Покровный орган с многослойным плоским ороговевающим пластом.",
                "An integumentary organ with a stratified squamous keratinizing covering.")]);
        var analog = new TrainingMaterial(Id(5), 1, "ready", Names("Поле второе", "Second field"), group.Id, [], [Image(105)], [], []);
        var liver = new TrainingMaterial(Id(2), 1, "ready", Names("Печень", "Liver"), null, [], [Image(102)], [vessel], []);
        var kidney = new TrainingMaterial(Id(3), 1, "ready", Names("Почка", "Kidney"), null, [], [Image(103)], [], []);
        var spleen = new TrainingMaterial(Id(4), 1, "ready", Names("Селезёнка", "Spleen"), null, [], [Image(104)], [], []);
        var catalog = new TrainingCatalog(Id(10000), 5, "original-source", [material, analog, liver, kidney, spleen], [group]);
        var methods = Enum.GetValues<TrainingMethod>();
        var plan = TrainingPlanner.Build(catalog, new([material.Id], methods, "ru", TrainingOrder.ByMethod, 37));
        TrainingPlan Slice(params TrainingQuestion[] questions) => new(catalog, plan.Configuration, questions,
            plan.Eligibility, plan.ObjectAnswers, plan.ElementAnswers);
        var session = new TrainingSession(plan);
        check(plan.QuestionCount == 6 && session.Questions.Count == 6 && session.DeferredQuestionCount == 0
            && session.Questions.Select(q => q.Key).SequenceEqual(plan.Questions.Select(q => q.Key)),
            "Session: all six methods execute in the prepared order with no deferred tracing");
        check(session.CurrentIndex == 0 && session.CurrentQuestion == session.Questions[0] && session.CurrentResult is null
            && !session.IsFinished && session.Results.Count == 0 && session.Summary.Total == 6 && session.Summary.Pending == 6
            && session.Summary.Accuracy is null && session.RemainingQuestionCount == 6,
            "Session: initial state has one pending question and no fabricated answer or accuracy");
        check(session.CurrentAnswers.Count == 4 && session.CurrentAnswers.Count(a => a.Name == "Кожа") == 1
            && session.CurrentAnswers.All(a => a.Name is not ("Поле первое" or "Поле второе")),
            "Session: object answers expose one canonical analog group and full-catalog alternatives");
        Reject<InvalidOperationException>(session.Advance, "cannot bypass an unanswered question without explicit skip");
        Reject<ArgumentException>(() => session.SubmitAnswer("Кожа"), "typed display text is not accepted as a selected answer key");
        Reject<ArgumentException>(() => session.SubmitAnswer(plan.ElementAnswers[0].Key), "object question rejects an element key from another answer pool");
        Reject<ArgumentException>(() => session.SubmitAnswer(session.CurrentQuestion!.Answer.Key.ToUpperInvariant()), "canonical keys are matched exactly without guessing");
        Reject<ArgumentNullException>(() => session.SubmitAnswer(null!), "null key is rejected without a result");
        Reject<InvalidOperationException>(() => session.SubmitImage(Id(101)), "named question rejects an image submission");
        Reject<ArgumentException>(() => session.MarkUnavailable(" \t "), "technical unavailability requires a reason");
        check(session.Results.Count == 0 && session.CurrentIndex == 0 && session.RemainingQuestionCount == 6,
            "Session: rejected input leaves the current question pending and the result list unchanged");

        var first = session.SubmitAnswer(session.CurrentQuestion!.Answer.Key);
        var retainedResults = session.Results;
        var retainedSummary = session.Summary;
        check(first.Outcome == TrainingOutcome.Correct && first.SelectedAnswer?.Key == "analog:" + group.Id.ToString("D")
            && first.SelectedMaterialImageId is null && session.CurrentResult == first && session.CurrentIndex == 0
            && session.CurrentQuestion == first.Question && session.Summary.Accuracy == 1,
            "Session: a canonical analog answer is correct and feedback waits for an explicit advance");
        Reject<InvalidOperationException>(() => session.SubmitAnswer(first.Question.Answer.Key), "repeated submission cannot duplicate or replace an answer");
        Reject<InvalidOperationException>(() => session.Skip(), "skip cannot overwrite a submitted answer");
        Reject<InvalidOperationException>(() => session.MarkUnavailable("missing"), "later media failure cannot overwrite submitted feedback");
        Reject<NotSupportedException>(() => ((IList<TrainingResult>)retainedResults).Clear(), "result snapshots cannot be mutated through a collection cast");
        Reject<NotSupportedException>(() => ((IList<TrainingQuestion>)session.Questions).Clear(), "executable queue remains immutable");
        session.Advance();
        var imageQuestion = session.CurrentQuestion!;
        check(imageQuestion.Method == TrainingMethod.ChooseImage && session.CurrentIndex == 1 && session.CurrentResult is null
            && session.CurrentAnswers.Count == 0 && imageQuestion.ImageChoices.Count == 4,
            "Session: image selection presents four prepared choices after advancing");
        Reject<InvalidOperationException>(() => session.SubmitAnswer(imageQuestion.Answer.Key), "image question rejects named-answer submission");
        Reject<ArgumentException>(() => session.SubmitImage(Id(9999)), "an image absent from the current four choices is rejected");
        Reject<ArgumentException>(() => session.SubmitImage(imageQuestion.ImageChoices[0].ImageId), "raw image UUID cannot stand in for a material-image choice UUID");
        var wrongImage = imageQuestion.ImageChoices.First(c => c.Answer.Key != imageQuestion.Answer.Key);
        var imageResult = session.SubmitImage(wrongImage.MaterialImageId);
        check(imageResult.Outcome == TrainingOutcome.Incorrect && imageResult.SelectedMaterialImageId == wrongImage.MaterialImageId
            && imageResult.SelectedAnswer == wrongImage.Answer && imageResult.Question.Answer == imageQuestion.Answer,
            "Session: incorrect image result preserves the selected image and the expected canonical answer for feedback");
        check(retainedResults.Count == 1 && retainedResults[0] == first && retainedSummary.Answered == 1
            && retainedSummary.Pending == 5 && session.Results.Count == 2,
            "Session: previously retained results and summaries do not change as more questions are completed");
        session.Advance();
        var skip = session.Skip();
        check(skip.Question.Method == TrainingMethod.NameElementRegion && skip.Outcome == TrainingOutcome.Skipped
            && skip.SelectedAnswer is null && skip.SelectedMaterialImageId is null && session.CurrentIndex == 2,
            "Session: skipping records no answer and still exposes one feedback step");
        session.Advance();
        var unavailable = session.MarkUnavailable("  Source image cannot be read.  ");
        check(unavailable.Outcome == TrainingOutcome.Unavailable && unavailable.Reason == "Source image cannot be read."
            && unavailable.SelectedAnswer is null && session.Summary.Answered == 2 && session.Summary.Accuracy == .5,
            "Session: technical unavailability and skipping are excluded from the answered-only accuracy denominator");
        session.End();
        check(session.IsFinished && session.CurrentQuestion is null && session.CurrentResult is null && session.CurrentAnswers.Count == 0
            && session.CurrentIndex == 6 && session.Results.Count == 6 && session.Results[3] == unavailable
            && session.Results.Skip(4).All(r => r.Outcome == TrainingOutcome.NotAnswered),
            "Session: ending during feedback preserves that result and marks only still-pending questions unanswered");
        check(session.Summary.Total == 6 && session.Summary.Correct == 1 && session.Summary.Incorrect == 1
            && session.Summary.Answered == 2 && session.Summary.Skipped == 1 && session.Summary.Unavailable == 1
            && session.Summary.NotAnswered == 2 && session.Summary.Pending == 0 && session.RemainingQuestionCount == 0
            && session.Summary.Accuracy == .5,
            "Session: final categories account for every executable question without grading missing answers");
        var finalResults = session.Results;
        session.End();
        check(session.Results.SequenceEqual(finalResults), "Session: repeated early-end request is idempotent");
        Reject<InvalidOperationException>(session.Advance, "finished session cannot advance beyond its final result");
        Reject<InvalidOperationException>(() => session.SubmitAnswer(first.Question.Answer.Key), "finished session cannot receive another answer");
        Reject<InvalidOperationException>(() => session.Skip(), "finished session cannot acquire another skipped result");

        foreach (var question in session.Questions.Where(q => q.Method != TrainingMethod.TraceElement))
        {
            var correct = new TrainingSession(Slice(question));
            TrainingResult result;
            if (question.Method == TrainingMethod.ChooseImage)
                result = correct.SubmitImage(question.ImageChoices.Single(c => c.Answer.Key == question.Answer.Key).MaterialImageId);
            else result = correct.SubmitAnswer(question.Answer.Key);
            check(result.Outcome == TrainingOutcome.Correct && !correct.IsFinished && correct.CurrentResult == result,
                $"Session: {question.Method} accepts its offered correct answer and retains final-question feedback");
            correct.Advance();
            check(correct.IsFinished && correct.Results.Count == 1 && correct.Summary.Correct == 1 && correct.Summary.NotAnswered == 0,
                $"Session: {question.Method} finishes normally only after advancing past feedback");
            var incorrect = new TrainingSession(Slice(question));
            result = question.Method == TrainingMethod.ChooseImage
                ? incorrect.SubmitImage(question.ImageChoices.First(c => c.Answer.Key != question.Answer.Key).MaterialImageId)
                : incorrect.SubmitAnswer(incorrect.CurrentAnswers.First(a => a.Key != question.Answer.Key).Key);
            check(result.Outcome == TrainingOutcome.Incorrect && incorrect.Summary.Accuracy == 0,
                $"Session: {question.Method} records an offered wrong answer without inventing a partial grade");
        }

        var analogPlan = TrainingPlanner.Build(catalog, new([material.Id, analog.Id], [TrainingMethod.NameObjectImage], "ru", TrainingOrder.ByObject));
        var analogSession = new TrainingSession(analogPlan);
        var analogKey = analogSession.CurrentQuestion!.Answer.Key;
        analogSession.SubmitAnswer(analogKey);
        analogSession.Advance();
        check(analogSession.SubmitAnswer(analogKey).Outcome == TrainingOutcome.Correct,
            "Session: different members of an analog group accept the same canonical group answer");
        var elementQuestion = session.Questions.Single(q => q.Method == TrainingMethod.NameElementRegion);
        var variantElement = new TrainingQuestion("variant", TrainingMethod.NameElementDescription, material.Id,
            new(elementQuestion.Answer.Key, "ЭПИТЕЛИЙ!"), elementId: epithelium.Id, description: "An alternate prompt.");
        var variantSession = new TrainingSession(Slice(variantElement));
        check(variantSession.SubmitAnswer(elementQuestion.Answer.Key).Outcome == TrainingOutcome.Correct,
            "Session: normalized element answer classes remain valid when another card uses different display spelling");
        var elementSession = new TrainingSession(Slice(elementQuestion));
        Reject<ArgumentException>(() => elementSession.SubmitAnswer(plan.ObjectAnswers[0].Key), "element question rejects object keys even though they belong to the catalog");
        check(elementSession.CurrentQuestion!.Regions.Single().Geometry == region.Geometry,
            "Session: execution preserves the exact reference geometry from the prepared snapshot");

        var traceQuestion = plan.Questions.Single(q => q.Method == TrainingMethod.TraceElement);
        var traceOnly = new TrainingSession(Slice(traceQuestion));
        check(traceOnly.Questions.Count == 1 && traceOnly.DeferredQuestionCount == 0 && traceOnly.CurrentAnswers.Count == 0,
            "Session: tracing-only preparation starts with a drawing task and no named alternatives");
        Reject<InvalidOperationException>(() => traceOnly.SubmitAnswer(traceQuestion.Answer.Key), "tracing task rejects a named answer");
        Reject<InvalidOperationException>(() => traceOnly.SubmitImage(Id(101)), "tracing task rejects an image choice");
        Reject<InvalidOperationException>(() => elementSession.SubmitTrace([region.Geometry]), "recognition task rejects a drawing");
        Reject<ArgumentNullException>(() => traceOnly.SubmitTrace(null!), "null drawing is rejected");
        Reject<ArgumentException>(() => traceOnly.SubmitTrace([]), "empty drawing is rejected without silently recording a skip");
        var zeroArea = RegionGeometry.Polygon([new(0, 0), new(10, 10), new(20, 20)]);
        Reject<ArgumentException>(() => traceOnly.SubmitTrace([zeroArea]), "a contour without filled area cannot be submitted");
        check(traceOnly.Results.Count == 0 && traceOnly.CurrentResult is null && traceOnly.Summary.Pending == 1,
            "Session: rejected drawing submissions leave the question pending");
        var sourceDrawing = new List<RegionGeometry> { region.Geometry };
        var traced = traceOnly.SubmitTrace(sourceDrawing);
        sourceDrawing.Clear();
        check(traced.Outcome == TrainingOutcome.Compared && traced.Trace is not null && traced.Trace.Regions.Count == 1
            && traced.Trace.Regions[0] == region.Geometry && traced.SelectedAnswer is null && traced.SelectedMaterialImageId is null
            && traced.Reason is null && traced.Trace.Comparison.Coverage == 1 && traced.Trace.Comparison.Precision == 1
            && traced.Trace.Comparison.ExcessFraction == 0 && traceOnly.CurrentResult == traced && !traceOnly.IsFinished,
            "Session: a submitted drawing is frozen with numeric feedback and no invented correct/incorrect outcome");
        Reject<NotSupportedException>(() => ((IList<RegionGeometry>)traced.Trace!.Regions).Clear(),
            "retained drawing cannot be changed through a collection cast");
        Reject<NotSupportedException>(() => ((IList<AtlasPoint>)traced.Trace!.Regions[0].Points).Add(new(0, 0)),
            "retained geometry cannot be changed through its point collection");
        Reject<InvalidOperationException>(() => traceOnly.SubmitTrace([RegionGeometry.Rectangle(0, 0, 10, 10)]),
            "drawing resubmission cannot replace comparison feedback");
        Reject<InvalidOperationException>(() => traceOnly.Skip(), "a compared drawing cannot become a skipped answer");
        Reject<InvalidOperationException>(() => traceOnly.MarkUnavailable("changed"), "later image changes cannot erase a submitted comparison");
        check(traceOnly.Summary.Answered == 1 && traceOnly.Summary.Compared == 1 && traceOnly.Summary.RecognitionAnswered == 0
            && traceOnly.Summary.Correct == 0 && traceOnly.Summary.Incorrect == 0 && traceOnly.Summary.Accuracy is null,
            "Session: a tracing-only result counts as answered and has no recognition accuracy percentage");
        traceOnly.Advance();
        check(traceOnly.IsFinished && traceOnly.Results.Single() == traced && traceOnly.Summary.Pending == 0,
            "Session: final tracing feedback completes after explicit advance");
        Reject<InvalidOperationException>(() => traceOnly.SubmitTrace([region.Geometry]), "finished session rejects another drawing");

        var asyncTrace = new TrainingSession(Slice(traceQuestion));
        using var canceledComparison = new CancellationTokenSource();
        canceledComparison.Cancel();
        Reject<OperationCanceledException>(() => asyncTrace.SubmitTraceAsync([region.Geometry], canceledComparison.Token).GetAwaiter().GetResult(),
            "cancelled background comparison cannot record an answer");
        Reject<ArgumentException>(() => asyncTrace.SubmitTraceAsync([]).GetAwaiter().GetResult(),
            "background comparison applies the same nonempty drawing requirement");
        check(asyncTrace.Results.Count == 0 && asyncTrace.CurrentResult is null,
            "Session: cancellation and rejected asynchronous input preserve the pending question");
        var asyncResult = asyncTrace.SubmitTraceAsync([region.Geometry]).GetAwaiter().GetResult();
        check(asyncResult.Outcome == TrainingOutcome.Compared && asyncResult.Trace!.Comparison.Coverage == 1
            && asyncTrace.CurrentResult == asyncResult && asyncTrace.Summary.Compared == 1,
            "Session: asynchronous drawing comparison records the same geometry feedback exactly once");
        Reject<InvalidOperationException>(() => asyncTrace.SubmitTraceAsync([region.Geometry]).GetAwaiter().GetResult(),
            "a second asynchronous drawing cannot overwrite completed feedback");

        var previousContext = SynchronizationContext.Current;
        using (var completionContext = new ControlledSynchronizationContext())
        {
            SynchronizationContext.SetSynchronizationContext(completionContext);
            try
            {
                var endedDuringComparison = new TrainingSession(Slice(traceQuestion));
                var endedTask = endedDuringComparison.SubmitTraceAsync([region.Geometry]);
                endedDuringComparison.End();
                check(!endedTask.IsCompleted && endedDuringComparison.Summary.NotAnswered == 1,
                    "Session: early completion can occur before the queued drawing comparison is recorded");
                completionContext.RunNext();
                Reject<InvalidOperationException>(() => endedTask.GetAwaiter().GetResult(),
                    "late background comparison rejects an already-ended session");
                check(endedDuringComparison.Results.Count == 1 && endedDuringComparison.Summary.NotAnswered == 1
                    && endedDuringComparison.Summary.Compared == 0 && endedDuringComparison.Results.Single().Trace is null,
                    "Session: late completion cannot append a drawing or overwrite an unanswered result after ending");

                var advancedDuringComparison = new TrainingSession(Slice(traceQuestion, elementQuestion));
                var advancedTask = advancedDuringComparison.SubmitTraceAsync([region.Geometry]);
                var preservedSkip = advancedDuringComparison.Skip();
                advancedDuringComparison.Advance();
                completionContext.RunNext();
                Reject<InvalidOperationException>(() => advancedTask.GetAwaiter().GetResult(),
                    "late comparison rejects a different current question even while that question is pending");
                check(advancedDuringComparison.Results.Count == 1 && advancedDuringComparison.Results.Single() == preservedSkip
                    && advancedDuringComparison.CurrentQuestion == elementQuestion && advancedDuringComparison.CurrentResult is null
                    && advancedDuringComparison.Summary.Compared == 0,
                    "Session: late drawing completion preserves an explicit skip and cannot answer the next question");

                var canceledBeforeCommit = new TrainingSession(Slice(traceQuestion));
                using var lateCancellation = new CancellationTokenSource();
                var canceledTask = canceledBeforeCommit.SubmitTraceAsync([region.Geometry], lateCancellation.Token);
                completionContext.RunNext(lateCancellation.Cancel);
                Reject<OperationCanceledException>(() => canceledTask.GetAwaiter().GetResult(),
                    "cancellation after calculation and before its queued continuation prevents recording");
                check(canceledBeforeCommit.Results.Count == 0 && canceledBeforeCommit.CurrentResult is null
                    && canceledBeforeCommit.Summary.Pending == 1,
                    "Session: an in-flight canceled calculation leaves its drawing question pending");

                var overlap = new TrainingSession(Slice(traceQuestion));
                var submissions = new[] { overlap.SubmitTraceAsync([region.Geometry]), overlap.SubmitTraceAsync([region.Geometry]) };
                completionContext.RunNext();
                completionContext.RunNext();
                check(submissions.Count(task => task.IsCompletedSuccessfully) == 1 && submissions.Count(task => task.IsFaulted) == 1
                    && overlap.Results.Count == 1 && overlap.Summary.Compared == 1,
                    "Session: overlapping calculations serialized on the caller context record exactly one drawing");
                Reject<InvalidOperationException>(() => submissions.Single(task => task.IsFaulted).GetAwaiter().GetResult(),
                    "the later overlapping continuation cannot replace the first submitted comparison");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }

        var partialTrace = new TrainingSession(Slice(traceQuestion));
        var partial = partialTrace.SubmitTrace([RegionGeometry.Rectangle(10, 10, 12.5, 40)]);
        check(partial.Outcome == TrainingOutcome.Compared && partial.Trace!.Comparison.Coverage == .5
            && partial.Trace.Comparison.Precision == 1 && partial.Trace.Comparison.ExcessArea == 0
            && partialTrace.Summary.Incorrect == 0,
            "Session: partial coverage remains quantitative without an arbitrary failure threshold");
        var missedTrace = new TrainingSession(Slice(traceQuestion));
        var missed = missedTrace.SubmitTrace([RegionGeometry.Rectangle(60, 60, 10, 10)]);
        check(missed.Outcome == TrainingOutcome.Compared && missed.Trace!.Comparison.Coverage == 0
            && missed.Trace.Comparison.Precision == 0 && missed.Trace.Comparison.ExcessFraction == 1
            && missedTrace.Summary.Answered == 1 && missedTrace.Summary.Accuracy is null,
            "Session: a disjoint filled answer retains its zero intersection and remains a comparison");

        var mixed = new TrainingSession(Slice(session.Questions[0], traceQuestion, elementQuestion));
        mixed.SubmitAnswer(mixed.CurrentQuestion!.Answer.Key);
        mixed.Advance();
        mixed.SubmitTrace([region.Geometry]);
        mixed.Advance();
        mixed.SubmitAnswer(mixed.CurrentAnswers.First(a => a.Key != mixed.CurrentQuestion!.Answer.Key).Key);
        mixed.Advance();
        check(mixed.Summary.Total == 3 && mixed.Summary.Answered == 3 && mixed.Summary.Compared == 1
            && mixed.Summary.RecognitionAnswered == 2 && mixed.Summary.Correct == 1 && mixed.Summary.Incorrect == 1
            && mixed.Summary.Accuracy == .5,
            "Session: mixed-session accuracy divides correct recognition answers only by checked recognition answers");
        var traceEnded = new TrainingSession(Slice(traceQuestion, elementQuestion));
        var traceBeforeEnd = traceEnded.SubmitTrace([region.Geometry]);
        traceEnded.End();
        check(traceEnded.Results[0] == traceBeforeEnd && traceEnded.Results[1].Outcome == TrainingOutcome.NotAnswered
            && traceEnded.Summary.Compared == 1 && traceEnded.Summary.NotAnswered == 1 && traceEnded.Summary.Accuracy is null,
            "Session: early completion retains submitted drawing and leaves later questions unanswered");
        var pendingTraceEnded = new TrainingSession(Slice(traceQuestion));
        pendingTraceEnded.End();
        check(pendingTraceEnded.Results.Single().Outcome == TrainingOutcome.NotAnswered
            && pendingTraceEnded.Results.Single().Trace is null && pendingTraceEnded.Summary.Compared == 0,
            "Session: ending before drawing submission cannot fabricate a comparison");
        var skippedTrace = new TrainingSession(Slice(traceQuestion));
        var traceSkip = skippedTrace.Skip();
        skippedTrace.Advance();
        check(traceSkip.Trace is null && skippedTrace.Summary.Skipped == 1 && skippedTrace.Summary.Answered == 0,
            "Session: skipped tracing retains no submitted geometry or numeric comparison");
        var unavailableTrace = new TrainingSession(Slice(traceQuestion));
        var traceFailure = unavailableTrace.MarkUnavailable("The tracing image changed.");
        unavailableTrace.Advance();
        check(traceFailure.Trace is null && unavailableTrace.Summary.Unavailable == 1 && unavailableTrace.Summary.Compared == 0
            && unavailableTrace.Summary.Incorrect == 0,
            "Session: a tracing-media failure remains distinct from both recognition errors and compared drawings");

        var ended = new TrainingSession(plan);
        ended.End();
        check(ended.Results.Count == 6 && ended.Results.All(r => r.Outcome == TrainingOutcome.NotAnswered)
            && ended.Summary.NotAnswered == 6 && ended.Summary.Accuracy is null,
            "Session: ending before any response reports unanswered questions and no percentage");
        var skipped = new TrainingSession(plan);
        while (!skipped.IsFinished) { skipped.Skip(); skipped.Advance(); }
        check(skipped.Summary.Skipped == 6 && skipped.Summary.Answered == 0 && skipped.Summary.Accuracy is null,
            "Session: an entirely skipped session has no accuracy denominator");
        var unavailableSession = new TrainingSession(Slice(imageQuestion));
        unavailableSession.MarkUnavailable("The displayed file changed.");
        unavailableSession.Advance();
        check(unavailableSession.Summary.Unavailable == 1 && unavailableSession.Summary.Incorrect == 0
            && unavailableSession.Summary.Accuracy is null,
            "Session: unavailable media cannot be reported as a learner error");

        var englishPlan = TrainingPlanner.Build(catalog, new([material.Id], methods, "en", TrainingOrder.Random, 91));
        var english = new TrainingSession(englishPlan);
        var repeated = new TrainingSession(englishPlan);
        check(english.Plan.Configuration.Locale == "en" && english.Questions.All(q => q.Answer.Name is "Skin" or "Epithelium")
            && english.Questions.Select(q => q.Key).SequenceEqual(repeated.Questions.Select(q => q.Key))
            && english.Questions.SelectMany(q => q.ImageChoices).SequenceEqual(repeated.Questions.SelectMany(q => q.ImageChoices)),
            "Session: a fixed plan retains its selected language, random queue and image ordering on every run");
        while (!english.IsFinished)
        {
            if (english.CurrentQuestion!.Method == TrainingMethod.ChooseImage)
            {
                var selected = english.CurrentQuestion.ImageChoices.Single(c => c.Answer.Key == english.CurrentQuestion.Answer.Key).MaterialImageId;
                english.SubmitImage(selected); repeated.SubmitImage(selected);
            }
            else if (english.CurrentQuestion.Method == TrainingMethod.TraceElement)
            {
                english.SubmitTrace(english.CurrentQuestion.Regions.Select(r => r.Geometry));
                repeated.SubmitTrace(repeated.CurrentQuestion!.Regions.Select(r => r.Geometry));
            }
            else
            {
                english.SubmitAnswer(english.CurrentQuestion.Answer.Key);
                repeated.SubmitAnswer(repeated.CurrentQuestion!.Answer.Key);
            }
            english.Advance(); repeated.Advance();
        }
        check(english.Results.Zip(repeated.Results).All(pair => pair.First.Outcome == pair.Second.Outcome
                && pair.First.Question == pair.Second.Question && pair.First.SelectedAnswer == pair.Second.SelectedAnswer
                && pair.First.SelectedMaterialImageId == pair.Second.SelectedMaterialImageId
                && (pair.First.Trace is null ? pair.Second.Trace is null : pair.First.Trace.Regions.SequenceEqual(pair.Second.Trace!.Regions)
                    && pair.First.Trace.Comparison.IntersectionArea == pair.Second.Trace.Comparison.IntersectionArea))
            && english.Summary.Correct == 5 && english.Summary.Compared == 1 && english.Summary.Accuracy == 1,
            "Session: identical choices against the same frozen plan produce identical results");
        check(plan.QuestionCount == 6 && plan.SourceFingerprint == "original-source" && plan.CatalogRevision == 5
            && catalog.Materials[0].Elements[0].Regions[0].Geometry == region.Geometry,
            "Session: completing attempts leaves the original catalog snapshot and prepared plan unchanged");

        Reject<ArgumentNullException>(() => new TrainingSession(null!), "missing plan cannot start a session");
        Reject<ArgumentException>(() => new TrainingSession(Slice()), "empty plan cannot start a session");
        Reject<ArgumentException>(() => new TrainingSession(Slice(session.Questions[0], session.Questions[0])),
            "duplicate question identities are rejected before execution");
        var unsupported = new TrainingQuestion("unsupported", (TrainingMethod)100, material.Id, session.Questions[0].Answer);
        Reject<ArgumentException>(() => new TrainingSession(Slice(unsupported)), "unknown method cannot be silently interpreted as a supported one");
        var missingAnswer = new TrainingQuestion("unlisted", TrainingMethod.NameObjectDescription, material.Id,
            new("outside", "Outside the list"), description: "A prompt.");
        Reject<ArgumentException>(() => new TrainingSession(Slice(missingAnswer)), "question with no selectable correct answer cannot start");
        TrainingQuestion TraceVariant(Guid? elementId, Guid? imageId, IEnumerable<TrainingRegion> regions,
            TrainingAnswer? answer = null, Guid? materialId = null) => new("trace-variant", TrainingMethod.TraceElement,
                materialId ?? material.Id, answer ?? traceQuestion.Answer, elementId, imageId, regions: regions);
        Reject<ArgumentException>(() => new TrainingSession(Slice(TraceVariant(null, Id(101), [region]))),
            "tracing question requires an element identity");
        Reject<ArgumentException>(() => new TrainingSession(Slice(TraceVariant(epithelium.Id, Guid.Empty, [region]))),
            "tracing question requires an image identity");
        Reject<ArgumentException>(() => new TrainingSession(Slice(TraceVariant(epithelium.Id, Id(101), [region], materialId: Guid.Empty))),
            "tracing question requires a material identity");
        Reject<ArgumentException>(() => new TrainingSession(Slice(TraceVariant(epithelium.Id, Id(101), []))),
            "tracing question requires reference regions");
        Reject<ArgumentException>(() => new TrainingSession(Slice(TraceVariant(epithelium.Id, Id(101), [region with { MaterialImageId = Id(102) }]))),
            "tracing reference regions must belong to the question image");
        Reject<ArgumentException>(() => new TrainingSession(Slice(TraceVariant(epithelium.Id, Id(101), [region, region]))),
            "tracing reference region identities cannot be repeated");
        Reject<ArgumentException>(() => new TrainingSession(Slice(TraceVariant(epithelium.Id, Id(101), [region with { Geometry = zeroArea }]))),
            "tracing reference must have positive filled area");
        Reject<ArgumentException>(() => new TrainingSession(Slice(TraceVariant(epithelium.Id, Id(101), [region], new("missing", "Missing")))),
            "tracing target must belong to the prepared canonical element pool");
        var duplicatedPool = new TrainingPlan(catalog, plan.Configuration, session.Questions, [],
            plan.ObjectAnswers.Concat([plan.ObjectAnswers[0]]), plan.ElementAnswers);
        Reject<ArgumentException>(() => new TrainingSession(duplicatedPool), "duplicate answer keys are rejected before execution");
        var incompleteChoice = new TrainingQuestion("too-few", TrainingMethod.ChooseImage, material.Id, imageQuestion.Answer,
            imageChoices: imageQuestion.ImageChoices.Take(3));
        Reject<ArgumentException>(() => new TrainingSession(Slice(incompleteChoice)), "image question must offer all four choices");
        var duplicatedImage = new TrainingQuestion("duplicate-image", TrainingMethod.ChooseImage, material.Id, imageQuestion.Answer,
            imageChoices: imageQuestion.ImageChoices.Take(3).Append(imageQuestion.ImageChoices[0]));
        Reject<ArgumentException>(() => new TrainingSession(Slice(duplicatedImage)), "duplicate image choices cannot create ambiguous submission identities");
        var repeatedHashChoices = imageQuestion.ImageChoices.ToArray();
        repeatedHashChoices[1] = repeatedHashChoices[1] with { Sha256 = repeatedHashChoices[0].Sha256.ToUpperInvariant() };
        var repeatedHash = new TrainingQuestion("duplicate-hash", TrainingMethod.ChooseImage, material.Id, imageQuestion.Answer,
            imageChoices: repeatedHashChoices);
        Reject<ArgumentException>(() => new TrainingSession(Slice(repeatedHash)), "identical file hashes cannot create visually duplicated image alternatives");
        var emptyHashChoices = imageQuestion.ImageChoices.ToArray();
        emptyHashChoices[0] = emptyHashChoices[0] with { Sha256 = " " };
        var emptyHash = new TrainingQuestion("empty-hash", TrainingMethod.ChooseImage, material.Id, imageQuestion.Answer,
            imageChoices: emptyHashChoices);
        Reject<ArgumentException>(() => new TrainingSession(Slice(emptyHash)), "unverifiable image choice without a hash cannot start");
    }

    private sealed class ControlledSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Add((callback, state));

        internal void RunNext(Action? beforeContinuation = null)
        {
            if (!callbacks.TryTake(out var continuation, TimeSpan.FromSeconds(10)))
                throw new TimeoutException("A drawing comparison did not queue its continuation within ten seconds.");
            beforeContinuation?.Invoke();
            continuation.Callback(continuation.State);
        }

        public void Dispose() => callbacks.Dispose();
    }
}

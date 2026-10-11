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
        check(plan.QuestionCount == 6 && session.Questions.Count == 5 && session.DeferredQuestionCount == 1
            && session.Questions.Select(q => q.Key).SequenceEqual(plan.Questions.Where(q => q.Method != TrainingMethod.TraceElement).Select(q => q.Key)),
            "Session: five recognition methods execute in the prepared order while tracing remains deferred");
        check(session.CurrentIndex == 0 && session.CurrentQuestion == session.Questions[0] && session.CurrentResult is null
            && !session.IsFinished && session.Results.Count == 0 && session.Summary.Total == 5 && session.Summary.Pending == 5
            && session.Summary.Accuracy is null && session.RemainingQuestionCount == 5,
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
        check(session.Results.Count == 0 && session.CurrentIndex == 0 && session.RemainingQuestionCount == 5,
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
            && retainedSummary.Pending == 4 && session.Results.Count == 2,
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
            && session.CurrentIndex == 5 && session.Results.Count == 5 && session.Results[3] == unavailable
            && session.Results[4].Outcome == TrainingOutcome.NotAnswered,
            "Session: ending during feedback preserves that result and marks only still-pending questions unanswered");
        check(session.Summary.Total == 5 && session.Summary.Correct == 1 && session.Summary.Incorrect == 1
            && session.Summary.Answered == 2 && session.Summary.Skipped == 1 && session.Summary.Unavailable == 1
            && session.Summary.NotAnswered == 1 && session.Summary.Pending == 0 && session.RemainingQuestionCount == 0
            && session.Summary.Accuracy == .5,
            "Session: final categories account for every executable question without counting deferred tracing or grading missing answers");
        var finalResults = session.Results;
        session.End();
        check(session.Results.SequenceEqual(finalResults), "Session: repeated early-end request is idempotent");
        Reject<InvalidOperationException>(session.Advance, "finished session cannot advance beyond its final result");
        Reject<InvalidOperationException>(() => session.SubmitAnswer(first.Question.Answer.Key), "finished session cannot receive another answer");
        Reject<InvalidOperationException>(() => session.Skip(), "finished session cannot acquire another skipped result");

        foreach (var question in session.Questions)
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

        var ended = new TrainingSession(plan);
        ended.End();
        check(ended.Results.Count == 5 && ended.Results.All(r => r.Outcome == TrainingOutcome.NotAnswered)
            && ended.Summary.NotAnswered == 5 && ended.Summary.Accuracy is null,
            "Session: ending before any response reports unanswered questions and no percentage");
        var skipped = new TrainingSession(plan);
        while (!skipped.IsFinished) { skipped.Skip(); skipped.Advance(); }
        check(skipped.Summary.Skipped == 5 && skipped.Summary.Answered == 0 && skipped.Summary.Accuracy is null,
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
            else
            {
                english.SubmitAnswer(english.CurrentQuestion.Answer.Key);
                repeated.SubmitAnswer(repeated.CurrentQuestion!.Answer.Key);
            }
            english.Advance(); repeated.Advance();
        }
        check(english.Results.SequenceEqual(repeated.Results) && english.Summary.Correct == 5,
            "Session: identical choices against the same frozen plan produce identical results");
        check(plan.QuestionCount == 6 && plan.SourceFingerprint == "original-source" && plan.CatalogRevision == 5
            && catalog.Materials[0].Elements[0].Regions[0].Geometry == region.Geometry,
            "Session: completing attempts leaves the original catalog snapshot and prepared plan unchanged");

        Reject<ArgumentNullException>(() => new TrainingSession(null!), "missing plan cannot start a session");
        Reject<ArgumentException>(() => new TrainingSession(Slice()), "empty plan cannot start a session");
        Reject<ArgumentException>(() => new TrainingSession(Slice(plan.Questions.Single(q => q.Method == TrainingMethod.TraceElement))),
            "tracing-only preparation cannot masquerade as an executable recognition session");
        Reject<ArgumentException>(() => new TrainingSession(Slice(session.Questions[0], session.Questions[0])),
            "duplicate question identities are rejected before execution");
        var unsupported = new TrainingQuestion("unsupported", (TrainingMethod)100, material.Id, session.Questions[0].Answer);
        Reject<ArgumentException>(() => new TrainingSession(Slice(unsupported)), "unknown method cannot be silently interpreted as a supported one");
        var missingAnswer = new TrainingQuestion("unlisted", TrainingMethod.NameObjectDescription, material.Id,
            new("outside", "Outside the list"), description: "A prompt.");
        Reject<ArgumentException>(() => new TrainingSession(Slice(missingAnswer)), "question with no selectable correct answer cannot start");
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
}

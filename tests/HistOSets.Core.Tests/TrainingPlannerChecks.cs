using HistOSets.Core;

internal static class TrainingPlannerChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static Guid Id(int value) => new(value, 0, 0, new byte[8]);
        static Dictionary<string, string> Ru(string value) => new() { ["ru"] = value };
        static TrainingImage Image(int id, string hash, bool usable = true, bool primary = true, int order = 0)
            => new(Id(id), Id(id + 1000), hash, primary, order, 100, 100, usable, usable ? null : "missing");
        static TrainingDescription Description(int id, string value, bool enabled = true)
            => new(Id(id), Ru(value), enabled, 0);
        static TrainingMaterial Material(int id, string name, TrainingImage[]? images = null,
            TrainingElement[]? elements = null, TrainingDescription[]? descriptions = null, Guid? analog = null,
            string status = "draft") => new(Id(id), 1, status, Ru(name), analog, [], images ?? [], elements ?? [], descriptions ?? []);
        static TrainingCatalog Catalog(IEnumerable<TrainingMaterial> materials, IEnumerable<TrainingAnalogGroup>? analogs = null)
            => new(Id(99999), 5, "source-fingerprint", materials, analogs ?? []);
        static TrainingPlan Plan(TrainingCatalog catalog, IEnumerable<Guid> ids, IEnumerable<TrainingMethod> methods,
            string locale = "ru", TrainingOrder order = TrainingOrder.Random, int seed = 123)
            => TrainingPlanner.Build(catalog, new(ids, methods, locale, order, seed));
        static bool Has(TrainingPlan plan, TrainingIssueCode code) => plan.Eligibility.SelectMany(e => e.Issues).Any(i => i.Code == code);
        var a = Material(1, "Кожа", [Image(101, "sha-a")]);
        var b = Material(2, "Печень", [Image(102, "sha-b")]);
        var c = Material(3, "Почка", [Image(103, "sha-c")]);
        var d = Material(4, "Селезёнка", [Image(104, "sha-d")]);
        var catalog = Catalog([a, b, c, d]);
        var choice = Plan(catalog, [a.Id], [TrainingMethod.ChooseImage]);
        check(choice.Questions.Count == 1 && choice.Questions[0].ImageChoices.Count == 4,
            "Training: distractors come from full catalog while selected material alone defines target");
        check(choice.Questions[0].ImageChoices.Select(i => i.Sha256).Distinct().Count() == 4
            && choice.Questions[0].ImageChoices.Select(i => i.Answer.Key).Distinct().Count() == 4,
            "Training: four image options have distinct file content and correct-answer classes");
        check(choice.ObjectAnswers.Count == 4 && choice.Questions[0].MaterialId == a.Id && choice.SourceFingerprint == "source-fingerprint",
            "Training: named choice pool and snapshot fingerprint are captured without attempt state");
        var tooSmall = Plan(Catalog([a, b, c]), [a.Id], [TrainingMethod.ChooseImage]);
        check(tooSmall.Questions.Count == 0 && Has(tooSmall, TrainingIssueCode.InsufficientImageChoices),
            "Training: fewer than four valid image classes explains exclusion instead of making impossible task");
        var sameHash = Material(4, "Селезёнка", [Image(104, "sha-c")]);
        var ambiguousImage = Plan(Catalog([c, sameHash]), [c.Id], [TrainingMethod.NameObjectImage]);
        check(ambiguousImage.QuestionCount == 0 && Has(ambiguousImage, TrainingIssueCode.AmbiguousImage),
            "Training: identical image bytes assigned to different named answers cannot form an object naming task");
        check(Plan(Catalog([a, b, c, sameHash]), [a.Id], [TrainingMethod.ChooseImage]).QuestionCount == 0,
            "Training: distinct names on identical images cannot serve as separate image alternatives");
        var group = new TrainingAnalogGroup(Id(500), Ru("Кожа"));
        var a1 = Material(1, "Поле первое", [Image(101, "sha-a")], analog: group.Id);
        var a2 = Material(5, "Поле второе", [Image(105, "sha-e")], analog: group.Id);
        var analogPlan = Plan(Catalog([a1, a2, b, c, d], [group]), [a1.Id, a2.Id], [TrainingMethod.ChooseImage]);
        check(analogPlan.Questions.Count == 2 && analogPlan.Questions.All(q => q.Answer.Name == "Кожа"
            && q.ImageChoices.Count(i => i.Answer.Key == q.Answer.Key) == 1),
            "Training: analog cards use localized group answer and never create a second correct image option");
        check(Plan(Catalog([a1, a2, b, c], [group]), [a1.Id], [TrainingMethod.ChooseImage]).QuestionCount == 0,
            "Training: analog group counts once when finding four answer classes");
        var alt1 = Material(1, "Поле первое", [Image(101, "sha-b")], analog: group.Id);
        var alt2 = Material(5, "Поле второе", [Image(105, "sha-e")], analog: group.Id);
        check(Plan(Catalog([alt1, alt2, b, c, d], [group]), [d.Id], [TrainingMethod.ChooseImage]).QuestionCount == 1,
            "Training: image matching can select another analog file to avoid duplicate distractor hashes");
        var duplicateName = Material(5, "  КОЖА! ", [Image(105, "sha-e")]);
        var ambiguous = Plan(Catalog([a, duplicateName, b, c, d]), [a.Id, duplicateName.Id], [TrainingMethod.NameObjectImage]);
        check(ambiguous.QuestionCount == 0 && Has(ambiguous, TrainingIssueCode.AmbiguousName),
            "Training: identical normalized names with different object identities require disambiguation");
        var untranslated = Plan(catalog, [a.Id], [TrainingMethod.NameObjectImage], "en");
        check(untranslated.QuestionCount == 0 && Has(untranslated, TrainingIssueCode.MissingName),
            "Training: English session never silently uses Russian answer labels");
        var missingGroupTranslation = Plan(Catalog([a1], [new(group.Id, new Dictionary<string, string> { ["en"] = "Skin" })]),
            [a1.Id], [TrainingMethod.NameObjectImage]);
        check(Has(missingGroupTranslation, TrainingIssueCode.MissingAnalogName), "Training: missing analog translation is explained independently");
        var fallbackImage = Material(1, "Кожа", [Image(101, "bad", false), Image(111, "good", primary: false)]);
        check(Plan(Catalog([fallbackImage]), [fallbackImage.Id], [TrainingMethod.NameObjectImage]).Questions.Single().MaterialImageId == Id(111),
            "Training: unusable primary image falls back to first usable image");
        var descriptions = new[] { Description(201, "Орган с дольчатым строением и центральными венами."),
            Description(202, "Орган с дольчатым строением и центральными венами!"),
            Description(203, "Печень имеет дольчатое строение."), Description(204, "Другой учебный текст", false),
            new TrainingDescription(Id(205), new Dictionary<string, string> { ["en"] = "A lobulated organ." }, true, 0) };
        var partial = Material(2, "Печень", descriptions: descriptions);
        var descriptionPlan = Plan(Catalog([partial]), [partial.Id], [TrainingMethod.NameObjectDescription, TrainingMethod.NameObjectImage]);
        check(descriptionPlan.Questions.Count == 1 && descriptionPlan.Questions[0].DescriptionId == Id(201)
            && descriptionPlan.Questions[0].MaterialImageId is null,
            "Training: draft without image supports its valid dedicated description variant");
        check(Has(descriptionPlan, TrainingIssueCode.DuplicateDescription) && Has(descriptionPlan, TrainingIssueCode.AnswerInDescription)
            && Has(descriptionPlan, TrainingIssueCode.DisabledDescription) && Has(descriptionPlan, TrainingIssueCode.MissingDescriptionTranslation)
            && Has(descriptionPlan, TrainingIssueCode.NoUsableImage),
            "Training: duplicate, leaking, disabled, untranslated and unavailable-image reasons remain distinct");
        var conflictPrompt = "Орган с выраженной соединительнотканной стромой.";
        var descriptionConflict = Plan(Catalog([Material(1, "Кожа", descriptions: [Description(211, conflictPrompt)]),
            Material(2, "Печень", descriptions: [Description(212, conflictPrompt)])]), [a.Id], [TrainingMethod.NameObjectDescription]);
        check(descriptionConflict.QuestionCount == 0 && Has(descriptionConflict, TrainingIssueCode.AmbiguousDescription),
            "Training: same description leading to different correct answers is excluded even outside target selection");
        var sameAnswer = Plan(Catalog([Material(1, "Поле первое", descriptions: [Description(211, conflictPrompt)], analog: group.Id),
            Material(5, "Поле второе", descriptions: [Description(212, conflictPrompt)], analog: group.Id)], [group]),
            [a1.Id, a2.Id], [TrainingMethod.NameObjectDescription]);
        check(sameAnswer.QuestionCount == 2, "Training: same description with one shared analog answer is not falsely ambiguous");
        var compound = RegionGeometry.Compound(new[] { new[] { new AtlasPoint(0, 0), new(80, 0), new(80, 80), new(0, 80) },
            new[] { new AtlasPoint(20, 20), new(60, 20), new(60, 60), new(20, 60) } });
        var regions = new[] { new TrainingRegion(Id(301), Id(101), compound),
            new TrainingRegion(Id(302), Id(101), RegionGeometry.Rectangle(5, 5, 10, 10)) };
        var element = new TrainingElement(Id(401), Ru("Эпителий"), 0, regions, [Description(221, "Пласт тесно расположенных клеток.")]);
        var annotated = Material(1, "", [Image(101, "sha-a")], [element]);
        var regionPlan = Plan(Catalog([annotated]), [annotated.Id], [TrainingMethod.NameElementRegion, TrainingMethod.TraceElement, TrainingMethod.NameElementDescription]);
        check(regionPlan.QuestionCount == 3 && regionPlan.Questions.Where(q => q.Method != TrainingMethod.NameElementDescription)
            .All(q => q.Regions.Count == 2 && q.Regions[0].Geometry == compound),
            "Training: all compound/overlapping regions of element on image form one unchanged reference without calibration or object name");
        var invisibleElement = new TrainingElement(Id(402), Ru("Сосуд"), 0,
            [new(Id(303), Id(101), RegionGeometry.Rectangle(200, 200, 50, 50))], []);
        check(Has(Plan(Catalog([Material(1, "Кожа", [Image(101, "sha-a")], [invisibleElement])]), [a.Id],
            [TrainingMethod.TraceElement]), TrainingIssueCode.NoVisibleRegions),
            "Training: region wholly outside image cannot create tracing task");
        var overshoot = new TrainingElement(Id(402), Ru("Сосуд"), 0,
            [new(Id(303), Id(101), RegionGeometry.Rectangle(-20, 20, 50, 50))], []);
        var overshootPlan = Plan(Catalog([Material(1, "Кожа", [Image(101, "sha-a")], [overshoot])]), [a.Id],
            [TrainingMethod.NameElementRegion, TrainingMethod.TraceElement]);
        check(overshootPlan.QuestionCount == 1 && overshootPlan.Questions[0].Method == TrainingMethod.NameElementRegion
            && Has(overshootPlan, TrainingIssueCode.RegionOutsideImage),
            "Training: partially external region can be named but cannot require tracing invisible reference area");
        var hollowElement = new TrainingElement(Id(403), Ru("Полость"), 0, [new(Id(304), Id(101),
            RegionGeometry.Compound(new[] { new[] { new AtlasPoint(-20, -20), new(120, -20), new(120, 120), new(-20, 120) },
                new[] { new AtlasPoint(-10, -10), new(110, -10), new(110, 110), new(-10, 110) } }))], []);
        check(Has(Plan(Catalog([Material(1, "Кожа", [Image(101, "sha-a")], [hollowElement])]), [a.Id],
            [TrainingMethod.TraceElement]), TrainingIssueCode.NoVisibleRegions),
            "Training: bounding box overlap without filled visible area is correctly excluded");
        var descriptionElementOnly = Material(1, "", elements: [element]);
        check(Plan(Catalog([descriptionElementOnly]), [a.Id], [TrainingMethod.NameElementDescription]).QuestionCount == 1,
            "Training: element description needs neither image nor object title");
        var methods = new[] { TrainingMethod.NameObjectImage, TrainingMethod.ChooseImage };
        var random = Plan(catalog, catalog.Materials.Select(m => m.Id), methods);
        var reverse = Plan(Catalog(catalog.Materials.Reverse()), catalog.Materials.Reverse().Select(m => m.Id), methods.Reverse());
        check(random.Questions.Select(q => q.Key).SequenceEqual(reverse.Questions.Select(q => q.Key))
            && random.Questions.SelectMany(q => q.ImageChoices).SequenceEqual(reverse.Questions.SelectMany(q => q.ImageChoices)),
            "Training: fixed seed is repeatable despite input material and method ordering");
        var differentSeed = Plan(catalog, catalog.Materials.Select(m => m.Id), methods, seed: 456);
        check(!random.Questions.Select(q => q.Key).SequenceEqual(differentSeed.Questions.Select(q => q.Key)),
            "Training: changed seed changes random question order");
        var byMethod = Plan(catalog, catalog.Materials.Select(m => m.Id), methods, order: TrainingOrder.ByMethod);
        var byObject = Plan(catalog, catalog.Materials.Select(m => m.Id), methods, order: TrainingOrder.ByObject);
        check(byMethod.Questions.Select(q => q.Method).SequenceEqual(byMethod.Questions.Select(q => q.Method).Order())
            && byObject.Questions.Select(q => q.MaterialId).SequenceEqual(byObject.Questions.Select(q => q.MaterialId).Order()),
            "Training: by-method and by-object orders keep their documented grouping");
        check(Plan(catalog, [Id(999)], methods).Eligibility.All(e => !e.IsEligible && e.Issues[0].Code == TrainingIssueCode.MissingMaterial),
            "Training: removed target UUID has an explicit eligibility reason");
        check(Plan(catalog, [], methods).Questions.Count == 0 && Plan(catalog, [a.Id], []).Questions.Count == 0,
            "Training: empty material or method selection has an empty preview");
        var mutableNames = Ru("Исходное имя"); var mutableImages = new List<TrainingImage> { Image(888, "sha-original") };
        var frozen = new TrainingMaterial(Id(888), 1, "draft", mutableNames, null, [], mutableImages, [], []);
        mutableNames["ru"] = "Изменённое имя"; mutableImages.Clear();
        check(frozen.Names["ru"] == "Исходное имя" && frozen.Images.Count == 1, "Training: snapshot defensively copies incoming maps and lists");
        var mutationBlocked = false;
        try { ((IList<TrainingQuestion>)random.Questions).Clear(); } catch (NotSupportedException) { mutationBlocked = true; }
        check(mutationBlocked && catalog.Materials[0].Images[0].IsPrimary, "Training: prepared plan is immutable and leaves source unchanged");
        check(TrainingPlanner.Normalize("  СЕЛЕЗЁНКА — образец №1! ") == "селезенка образец no1",
            "Training: literal normalization handles Unicode compatibility, punctuation, case and whitespace");
    }
}

using System.Text;
using Clipper2Lib;

namespace HistOSets.Core;

/// <summary>Pure session preparation. It never reads files, alters the catalog or evaluates a user's answer.</summary>
public static class TrainingPlanner
{
    public static TrainingPlan Build(TrainingCatalog catalog, TrainingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Locale is not ("ru" or "en")) throw new ArgumentException("Training locale must be ru or en.");
        if (!Enum.IsDefined(configuration.Order) || configuration.Methods.Any(m => !Enum.IsDefined(m)))
            throw new ArgumentException("Unsupported training method or order.");
        var locale = configuration.Locale;
        var materials = catalog.Materials.OrderBy(m => m.Id).ToDictionary(m => m.Id);
        var analogs = catalog.AnalogGroups.ToDictionary(g => g.Id);
        var objects = new Dictionary<Guid, TrainingAnswer>();
        var objectIssues = new Dictionary<Guid, TrainingIssue>();
        foreach (var material in materials.Values)
        {
            var name = Name(material.Names, locale);
            if (name.Length == 0) { objectIssues[material.Id] = new(TrainingIssueCode.MissingName); continue; }
            if (material.AnalogGroupId is { } groupId)
            {
                var groupName = analogs.TryGetValue(groupId, out var group) ? Name(group.Names, locale) : "";
                if (groupName.Length == 0) { objectIssues[material.Id] = new(TrainingIssueCode.MissingAnalogName); continue; }
                objects[material.Id] = new("analog:" + groupId.ToString("D"), groupName);
            }
            else objects[material.Id] = new("material:" + material.Id.ToString("D"), name);
        }
        var ambiguousNames = objects.Values.GroupBy(a => Normalize(a.Name), StringComparer.Ordinal)
            .Where(g => g.Select(a => a.Key).Distinct().Skip(1).Any()).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var id in objects.Where(p => ambiguousNames.Contains(Normalize(p.Value.Name))).Select(p => p.Key).ToArray())
        {
            objectIssues[id] = new(TrainingIssueCode.AmbiguousName, objects[id].Name);
            objects.Remove(id);
        }
        var elements = materials.Values.SelectMany(m => m.Elements).Where(e => Name(e.Names, locale).Length > 0)
            .GroupBy(e => e.Id).ToDictionary(g => g.Key, g => new TrainingAnswer("element:" + Normalize(Name(g.First().Names, locale)), Name(g.First().Names, locale)));
        var objectDescriptions = DescriptionAmbiguities(materials.Values.SelectMany(m =>
            objects.TryGetValue(m.Id, out var answer) ? m.Descriptions.Select(d => (d, answer.Key)) : []), locale);
        var elementDescriptions = DescriptionAmbiguities(materials.Values.SelectMany(m => m.Elements).SelectMany(e =>
            elements.TryGetValue(e.Id, out var answer) ? e.Descriptions.Select(d => (d, answer.Key)) : []), locale);
        var primaryImages = materials.Values.ToDictionary(m => m.Id, m => m.Images.Where(Usable)
            .OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder).ThenBy(i => i.MaterialImageId).FirstOrDefault());
        var imagePool = materials.Values.Where(m => objects.ContainsKey(m.Id) && primaryImages[m.Id] is not null)
            .Select(m => Choice(m.Id, primaryImages[m.Id]!, objects[m.Id])).ToArray();
        // Every usable view of the target answer class is potentially correct, including
        // secondary images and images of other analog members outside the selected cards.
        var imageHashesByAnswer = materials.Values.GroupBy(m => m.AnalogGroupId is { } groupId
                ? "analog:" + groupId.ToString("D") : "material:" + m.Id.ToString("D"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(m => m.Images.Where(Usable))
                .Select(i => i.Sha256.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var ambiguousImageHashes = materials.Values.Where(m => objects.ContainsKey(m.Id))
            .SelectMany(m => m.Images.Where(Usable).Select(i => (Hash: i.Sha256.ToLowerInvariant(), Answer: objects[m.Id].Key)))
            .GroupBy(i => i.Hash, StringComparer.Ordinal).Where(g => g.Select(i => i.Answer).Distinct().Skip(1).Any())
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var questions = new List<TrainingQuestion>();
        var eligibility = new List<TrainingEligibility>();
        foreach (var id in configuration.MaterialIds.Order())
        foreach (var method in configuration.Methods.Order())
        {
            var start = questions.Count;
            var issues = new List<TrainingIssue>();
            if (!materials.TryGetValue(id, out var material)) issues.Add(new(TrainingIssueCode.MissingMaterial));
            else if (method is TrainingMethod.NameObjectImage or TrainingMethod.ChooseImage or TrainingMethod.NameObjectDescription)
            {
                if (!objects.TryGetValue(id, out var answer)) issues.Add(objectIssues[id]);
                else if (method == TrainingMethod.NameObjectDescription)
                    AddDescriptions(material.Descriptions, answer, null, material, method, objectDescriptions, issues, questions, locale);
                else if (primaryImages[id] is not { } image)
                    issues.Add(new(TrainingIssueCode.NoUsableImage, ImageIssues(material)));
                else if (method == TrainingMethod.NameObjectImage)
                {
                    if (ambiguousImageHashes.Contains(image.Sha256.ToLowerInvariant()))
                        issues.Add(new(TrainingIssueCode.AmbiguousImage, MaterialImageId: image.MaterialImageId));
                    else questions.Add(new(Key(method, id, image.MaterialImageId), method, id, answer, materialImageId: image.MaterialImageId));
                }
                else
                {
                    var choices = ImageChoices(Choice(id, image, answer), imagePool, imageHashesByAnswer[answer.Key], configuration.Seed);
                    if (choices.Count != 4) issues.Add(new(TrainingIssueCode.InsufficientImageChoices, choices.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    else questions.Add(new(Key(method, id, image.MaterialImageId), method, id, answer,
                        materialImageId: image.MaterialImageId, imageChoices: choices));
                }
            }
            else
            {
                if (material.Elements.Count == 0) issues.Add(new(TrainingIssueCode.NoElements));
                foreach (var element in material.Elements.OrderBy(e => e.SortOrder).ThenBy(e => e.Id))
                {
                    if (!elements.TryGetValue(element.Id, out var answer))
                    { issues.Add(new(TrainingIssueCode.MissingName, ElementId: element.Id)); continue; }
                    if (method == TrainingMethod.NameElementDescription)
                        AddDescriptions(element.Descriptions, answer, element.Id, material, method, elementDescriptions, issues, questions, locale);
                    else
                    {
                        var elementStart = questions.Count;
                        foreach (var group in element.Regions.GroupBy(r => r.MaterialImageId).OrderBy(g => g.Key))
                        {
                            var image = material.Images.FirstOrDefault(i => i.MaterialImageId == group.Key);
                            if (image is null || !Usable(image))
                            { issues.Add(new(TrainingIssueCode.NoUsableImage, image?.Issue ?? "", element.Id, group.Key)); continue; }
                            var regions = group.OrderBy(r => r.Id).ToArray();
                            if (!Visible(regions, image, out var extendsOutsideImage))
                            { issues.Add(new(TrainingIssueCode.NoVisibleRegions, ElementId: element.Id, MaterialImageId: group.Key)); continue; }
                            if (method == TrainingMethod.TraceElement && extendsOutsideImage)
                            { issues.Add(new(TrainingIssueCode.RegionOutsideImage, ElementId: element.Id, MaterialImageId: group.Key)); continue; }
                            questions.Add(new(Key(method, id, element.Id, group.Key), method, id, answer,
                                element.Id, group.Key, regions: regions));
                        }
                        if (questions.Count == elementStart && !issues.Any(i => i.ElementId == element.Id))
                            issues.Add(new(TrainingIssueCode.NoVisibleRegions, ElementId: element.Id));
                    }
                }
            }
            eligibility.Add(new(id, method, questions.Count - start, issues));
        }
        var ordered = questions.OrderBy(q => q.Key, StringComparer.Ordinal).ToList();
        if (configuration.Order == TrainingOrder.Random) Shuffle(ordered, new StableRandom(configuration.Seed));
        else if (configuration.Order == TrainingOrder.ByMethod)
            ordered = ordered.OrderBy(q => q.Method).ThenBy(q => q.MaterialId).ThenBy(q => q.Key, StringComparer.Ordinal).ToList();
        else ordered = ordered.OrderBy(q => q.MaterialId).ThenBy(q => q.Method).ThenBy(q => q.Key, StringComparer.Ordinal).ToList();
        return new(catalog, configuration, ordered, eligibility,
            objects.Values.DistinctBy(a => a.Key).OrderBy(a => Normalize(a.Name), StringComparer.Ordinal).ThenBy(a => a.Key, StringComparer.Ordinal),
            elements.Values.DistinctBy(a => a.Key).OrderBy(a => Normalize(a.Name), StringComparer.Ordinal));
    }

    /// <summary>Literal matching only: punctuation, case and whitespace are normalized, without linguistic guesses.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(); var space = false;
        foreach (var character in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character)) { if (space && builder.Length > 0) builder.Append(' '); builder.Append(character == 'ё' ? 'е' : character); space = false; }
            else space = true;
        }
        return builder.ToString();
    }

    private static string Name(IReadOnlyDictionary<string, string> names, string locale)
    {
        // Old catalogs can contain distinct "ru"/"RU" keys. Exact spelling wins; never substitute another language.
        var text = names.TryGetValue(locale, out var exact) ? exact
            : names.OrderBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault(p => string.Equals(p.Key, locale, StringComparison.OrdinalIgnoreCase)).Value;
        return !string.IsNullOrEmpty(text) && Normalize(text).Length > 0 ? text.Trim() : "";
    }
    private static bool Usable(TrainingImage image) => image.IsUsable && image.Width > 0 && image.Height > 0 && image.Sha256.Length > 0;
    private static string ImageIssues(TrainingMaterial material) => string.Join("; ", material.Images.Select(i => i.Issue).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct());
    private static string Key(TrainingMethod method, Guid material, params Guid[] parts)
        => ((int)method).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + material.ToString("D") + ":" + string.Join(":", parts.Select(p => p.ToString("D")));
    private static TrainingImageChoice Choice(Guid material, TrainingImage image, TrainingAnswer answer)
        => new(material, image.MaterialImageId, image.ImageId, image.Sha256.ToLowerInvariant(), answer);

    private static HashSet<string> DescriptionAmbiguities(IEnumerable<(TrainingDescription Description, string Answer)> variants, string locale)
        => variants.Where(v => v.Description.IsEnabled && Name(v.Description.Texts, locale).Length > 0)
            .GroupBy(v => Normalize(Name(v.Description.Texts, locale)), StringComparer.Ordinal)
            .Where(g => g.Select(v => v.Answer).Distinct().Skip(1).Any()).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

    private static void AddDescriptions(IReadOnlyList<TrainingDescription> descriptions, TrainingAnswer answer,
        Guid? elementId, TrainingMaterial material, TrainingMethod method, HashSet<string> ambiguous,
        List<TrainingIssue> issues, List<TrainingQuestion> questions, string locale)
    {
        if (descriptions.Count == 0) { issues.Add(new(TrainingIssueCode.NoDescription, ElementId: elementId)); return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var description in descriptions.OrderBy(d => d.SortOrder).ThenBy(d => d.Id))
        {
            TrainingIssueCode? issue = null;
            var text = Name(description.Texts, locale); var normalized = Normalize(text);
            if (!description.IsEnabled) issue = TrainingIssueCode.DisabledDescription;
            else if (text.Length == 0) issue = TrainingIssueCode.MissingDescriptionTranslation;
            else if (ambiguous.Contains(normalized)) issue = TrainingIssueCode.AmbiguousDescription;
            else if (Leaks(normalized, answer.Name) || (elementId is null && Leaks(normalized, Name(material.Names, locale))))
                issue = TrainingIssueCode.AnswerInDescription;
            else if (!seen.Add(normalized)) issue = TrainingIssueCode.DuplicateDescription;
            if (issue is { } code) issues.Add(new(code, ElementId: elementId, DescriptionId: description.Id));
            else questions.Add(new(Key(method, material.Id, description.Id), method, material.Id, answer,
                elementId, descriptionId: description.Id, description: text));
        }
    }
    private static bool Leaks(string text, string answer)
    {
        var normalized = Normalize(answer);
        return normalized.Length > 0 && (" " + text + " ").Contains(" " + normalized + " ", StringComparison.Ordinal);
    }

    private static bool Visible(IReadOnlyList<TrainingRegion> regions, TrainingImage image, out bool extendsOutsideImage)
    {
        extendsOutsideImage = false;
        try
        {
            var paths = new PathsD();
            foreach (var region in regions) paths.AddRange(RegionGeometryOperations.NormalizedPaths(region.Geometry));
            // Each region is a filled set in its own right. Union preserves holes within a region
            // while preventing overlapping regions from cancelling one another by even-odd fill.
            var subject = Clipper.Union(paths, new PathsD(), FillRule.NonZero, RegionGeometryOperations.Precision);
            var clip = new PathsD { new PathD { new(0, 0), new(image.Width, 0), new(image.Width, image.Height), new(0, image.Height) } };
            var visible = Clipper.Intersect(subject, clip, FillRule.NonZero, RegionGeometryOperations.Precision);
            extendsOutsideImage = Math.Abs(Clipper.Area(Clipper.Difference(subject, clip, FillRule.NonZero, RegionGeometryOperations.Precision))) > 0;
            return double.IsFinite(Clipper.Area(visible)) && Math.Abs(Clipper.Area(visible)) > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ClipperLibException) { return false; }
    }

    private static IReadOnlyList<TrainingImageChoice> ImageChoices(TrainingImageChoice target,
        IReadOnlyList<TrainingImageChoice> pool, IReadOnlySet<string> targetImageHashes, int seed)
    {
        var groups = pool.Where(c => c.Answer.Key != target.Answer.Key && !targetImageHashes.Contains(c.Sha256))
            .GroupBy(c => c.Answer.Key).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(c => c.MaterialId).ThenBy(c => c.MaterialImageId).ToArray()).ToList();
        var random = new StableRandom(seed ^ StableHash(target.Answer.Key));
        Shuffle(groups, random);
        var byHash = new Dictionary<string, (int Group, TrainingImageChoice Choice)>(StringComparer.Ordinal);
        bool Match(int group, HashSet<string> seen)
        {
            foreach (var choice in groups[group])
            {
                if (!seen.Add(choice.Sha256)) continue;
                if (!byHash.TryGetValue(choice.Sha256, out var previous) || Match(previous.Group, seen))
                { byHash[choice.Sha256] = (group, choice); return true; }
            }
            return false;
        }
        for (var i = 0; i < groups.Count && byHash.Count < 3; i++) Match(i, new(StringComparer.Ordinal));
        var result = new List<TrainingImageChoice> { target };
        result.AddRange(byHash.Values.OrderBy(v => v.Group).Select(v => v.Choice));
        Shuffle(result, random);
        return TrainingFrozen.List(result);
    }

    private static int StableHash(string value)
    {
        unchecked { uint hash = 2166136261; foreach (var c in value) { hash ^= c; hash *= 16777619; } return (int)hash; }
    }
    private static void Shuffle<T>(IList<T> items, StableRandom random)
    {
        for (var i = items.Count - 1; i > 0; i--) { var j = random.Next(i + 1); (items[i], items[j]) = (items[j], items[i]); }
    }
    private sealed class StableRandom(int seed)
    {
        private uint state = unchecked((uint)seed) ^ 0x9e3779b9u;
        internal int Next(int maximum)
        {
            // Fixed algorithm, independent of runtime Random changes; zero is a valid caller seed.
            state = unchecked(state + 0x6d2b79f5u);
            var z = state; z = unchecked((z ^ (z >> 15)) * (z | 1)); z ^= unchecked(z + (z ^ (z >> 7)) * (z | 61));
            return (int)((z ^ (z >> 14)) % (uint)maximum);
        }
    }
}

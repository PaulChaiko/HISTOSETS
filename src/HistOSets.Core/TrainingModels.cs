using System.Collections.ObjectModel;

namespace HistOSets.Core;

public enum TrainingMethod { NameObjectImage, ChooseImage, NameElementRegion, TraceElement, NameObjectDescription, NameElementDescription }
public enum TrainingOrder { Random, ByMethod, ByObject }
public enum TrainingIssueCode
{
    MissingMaterial, MissingName, MissingAnalogName, AmbiguousName, AmbiguousImage, NoUsableImage, NoElements,
    NoVisibleRegions, RegionOutsideImage, InsufficientImageChoices, NoDescription, DisabledDescription,
    MissingDescriptionTranslation, AnswerInDescription, AmbiguousDescription, DuplicateDescription,
    RegionComparisonUnavailable
}

internal static class TrainingFrozen
{
    internal static IReadOnlyList<T> List<T>(IEnumerable<T> source) => Array.AsReadOnly(source.ToArray());
    internal static IReadOnlyDictionary<string, string> Map(IReadOnlyDictionary<string, string> source)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(source, StringComparer.Ordinal));
}

/// <summary>An immutable, language-independent snapshot; the fingerprint is produced and checked by Storage.</summary>
public sealed class TrainingCatalog(Guid catalogId, long catalogRevision, string sourceFingerprint,
    IEnumerable<TrainingMaterial> materials, IEnumerable<TrainingAnalogGroup> analogGroups)
{
    public Guid CatalogId { get; } = catalogId;
    public long CatalogRevision { get; } = catalogRevision;
    public string SourceFingerprint { get; } = sourceFingerprint;
    public IReadOnlyList<TrainingMaterial> Materials { get; } = TrainingFrozen.List(materials);
    public IReadOnlyList<TrainingAnalogGroup> AnalogGroups { get; } = TrainingFrozen.List(analogGroups);
}

public sealed class TrainingMaterial(Guid id, int revision, string status, IReadOnlyDictionary<string, string> names,
    Guid? analogGroupId, IEnumerable<Guid> tagIds, IEnumerable<TrainingImage> images,
    IEnumerable<TrainingElement> elements, IEnumerable<TrainingDescription> descriptions)
{
    public Guid Id { get; } = id;
    public int Revision { get; } = revision;
    public string Status { get; } = status;
    public IReadOnlyDictionary<string, string> Names { get; } = TrainingFrozen.Map(names);
    public Guid? AnalogGroupId { get; } = analogGroupId;
    public IReadOnlyList<Guid> TagIds { get; } = TrainingFrozen.List(tagIds);
    public IReadOnlyList<TrainingImage> Images { get; } = TrainingFrozen.List(images);
    public IReadOnlyList<TrainingElement> Elements { get; } = TrainingFrozen.List(elements);
    public IReadOnlyList<TrainingDescription> Descriptions { get; } = TrainingFrozen.List(descriptions);
}

public sealed record TrainingImage(Guid MaterialImageId, Guid ImageId, string Sha256, bool IsPrimary,
    int SortOrder, int Width, int Height, bool IsUsable, string? Issue = null, string? ImagePath = null);
public sealed record TrainingRegion(Guid Id, Guid MaterialImageId, RegionGeometry Geometry);

public sealed class TrainingAnalogGroup(Guid id, IReadOnlyDictionary<string, string> names)
{
    public Guid Id { get; } = id;
    public IReadOnlyDictionary<string, string> Names { get; } = TrainingFrozen.Map(names);
}

public sealed class TrainingElement(Guid id, IReadOnlyDictionary<string, string> names, int sortOrder,
    IEnumerable<TrainingRegion> regions, IEnumerable<TrainingDescription> descriptions)
{
    public Guid Id { get; } = id;
    public IReadOnlyDictionary<string, string> Names { get; } = TrainingFrozen.Map(names);
    public int SortOrder { get; } = sortOrder;
    public IReadOnlyList<TrainingRegion> Regions { get; } = TrainingFrozen.List(regions);
    public IReadOnlyList<TrainingDescription> Descriptions { get; } = TrainingFrozen.List(descriptions);
}

public sealed class TrainingDescription(Guid id, IReadOnlyDictionary<string, string> texts, bool isEnabled, int sortOrder)
{
    public Guid Id { get; } = id;
    public IReadOnlyDictionary<string, string> Texts { get; } = TrainingFrozen.Map(texts);
    public bool IsEnabled { get; } = isEnabled;
    public int SortOrder { get; } = sortOrder;
}

public sealed class TrainingConfiguration(IEnumerable<Guid> materialIds, IEnumerable<TrainingMethod> methods,
    string locale, TrainingOrder order = TrainingOrder.Random, int seed = 0)
{
    public IReadOnlyList<Guid> MaterialIds { get; } = TrainingFrozen.List(materialIds.Distinct());
    public IReadOnlyList<TrainingMethod> Methods { get; } = TrainingFrozen.List(methods.Distinct());
    public string Locale { get; } = locale;
    public TrainingOrder Order { get; } = order;
    public int Seed { get; } = seed;
}

public sealed record TrainingIssue(TrainingIssueCode Code, string Detail = "", Guid? ElementId = null,
    Guid? MaterialImageId = null, Guid? DescriptionId = null);
public sealed class TrainingEligibility(Guid materialId, TrainingMethod method, int questionCount, IEnumerable<TrainingIssue> issues)
{
    public Guid MaterialId { get; } = materialId;
    public TrainingMethod Method { get; } = method;
    public int QuestionCount { get; } = questionCount;
    public bool IsEligible => QuestionCount > 0;
    public IReadOnlyList<TrainingIssue> Issues { get; } = TrainingFrozen.List(issues);
}

/// <summary>A searchable named answer. Analog cards share one answer class; element labels share one normalized class.</summary>
public sealed record TrainingAnswer(string Key, string Name);
public sealed record TrainingImageChoice(Guid MaterialId, Guid MaterialImageId, Guid ImageId,
    string Sha256, TrainingAnswer Answer);

/// <summary>Prepared data only: no execution state, submitted answers, grades or stored attempts.</summary>
public sealed class TrainingQuestion(string key, TrainingMethod method, Guid materialId, TrainingAnswer answer,
    Guid? elementId = null, Guid? materialImageId = null, Guid? descriptionId = null, string? description = null,
    IEnumerable<TrainingRegion>? regions = null, IEnumerable<TrainingImageChoice>? imageChoices = null)
{
    public string Key { get; } = key;
    public TrainingMethod Method { get; } = method;
    public Guid MaterialId { get; } = materialId;
    public Guid? ElementId { get; } = elementId;
    public Guid? MaterialImageId { get; } = materialImageId;
    public Guid? DescriptionId { get; } = descriptionId;
    public string? Description { get; } = description;
    public TrainingAnswer Answer { get; } = answer;
    public IReadOnlyList<TrainingRegion> Regions { get; } = TrainingFrozen.List(regions ?? []);
    public IReadOnlyList<TrainingImageChoice> ImageChoices { get; } = TrainingFrozen.List(imageChoices ?? []);
}

public sealed class TrainingPlan(TrainingCatalog catalog, TrainingConfiguration configuration,
    IEnumerable<TrainingQuestion> questions, IEnumerable<TrainingEligibility> eligibility,
    IEnumerable<TrainingAnswer> objectAnswers, IEnumerable<TrainingAnswer> elementAnswers)
{
    public Guid CatalogId { get; } = catalog.CatalogId;
    public long CatalogRevision { get; } = catalog.CatalogRevision;
    public string SourceFingerprint { get; } = catalog.SourceFingerprint;
    public TrainingConfiguration Configuration { get; } = configuration;
    public IReadOnlyList<TrainingQuestion> Questions { get; } = TrainingFrozen.List(questions);
    public IReadOnlyList<TrainingEligibility> Eligibility { get; } = TrainingFrozen.List(eligibility);
    public IReadOnlyList<TrainingAnswer> ObjectAnswers { get; } = TrainingFrozen.List(objectAnswers);
    public IReadOnlyList<TrainingAnswer> ElementAnswers { get; } = TrainingFrozen.List(elementAnswers);
    public int QuestionCount => Questions.Count;
}

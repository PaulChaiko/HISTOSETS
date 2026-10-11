using HistOSets.Core;

namespace HistOSets.Storage;

/// <summary>An isolated editor working copy. Creating or changing it never writes catalog files.</summary>
public sealed class MaterialDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Revision { get; set; }
    public string Kind { get; set; } = "image";
    public string Status { get; set; } = "draft";
    public string AuthorCredit { get; set; } = "";
    public string RightsCredit { get; set; } = "";
    public Guid? AnalogGroupId { get; set; }
    public Dictionary<string, MaterialTextDraft> Texts { get; set; } = new() { ["ru"] = new(), ["en"] = new() };
    public List<MaterialImageDraft> Images { get; set; } = [];
    public List<MaterialElementDraft> Elements { get; set; } = [];
    public HashSet<Guid> TagIds { get; set; } = [];
    public MaterialDraft Clone() => new()
    {
        Id = Id, Revision = Revision, Kind = Kind, Status = Status, AuthorCredit = AuthorCredit, RightsCredit = RightsCredit,
        AnalogGroupId = AnalogGroupId, Texts = Texts.ToDictionary(p => p.Key, p => p.Value.Clone()),
        Images = Images.Select(i => i.Clone()).ToList(), Elements = Elements.Select(e => e.Clone()).ToList(), TagIds = [.. TagIds]
    };
}

public sealed class MaterialTextDraft
{
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public MaterialTextDraft Clone() => new() { Name = Name, Summary = Summary, Description = Description };
}

public sealed class MaterialImageDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ImageId { get; set; }
    public string? SourcePath { get; set; }
    public string? SourceSha256 { get; set; }
    public string? ManagedPath { get; set; }
    public ImageMetadata? Metadata { get; set; }
    public bool IsPrimary { get; set; }
    public MaterialImageDraft Clone() => new() { Id = Id, ImageId = ImageId, SourcePath = SourcePath, SourceSha256 = SourceSha256, ManagedPath = ManagedPath, Metadata = Metadata, IsPrimary = IsPrimary };
}

public sealed class MaterialElementDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Dictionary<string, MaterialTextDraft> Texts { get; set; } = new() { ["ru"] = new(), ["en"] = new() };
    public List<MaterialRegionDraft> Regions { get; set; } = [];
    public MaterialElementDraft Clone() => new() { Id = Id, Texts = Texts.ToDictionary(p => p.Key, p => p.Value.Clone()), Regions = Regions.Select(r => r.Clone()).ToList() };
}

public sealed class MaterialRegionDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MaterialImageId { get; set; }
    public RegionGeometry Geometry { get; set; } = RegionGeometry.Rectangle(0, 0, 1, 1);
    public MaterialRegionDraft Clone() => new() { Id = Id, MaterialImageId = MaterialImageId, Geometry = Geometry };
}

public sealed record MaterialIndexEntry(Guid Id, int Revision, string Status, string Name, string Summary,
    string? ImagePath, Guid? AnalogGroupId, IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string> AnalogGroupNames, IReadOnlySet<Guid> TagIds);

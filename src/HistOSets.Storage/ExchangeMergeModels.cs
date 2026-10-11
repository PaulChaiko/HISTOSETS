namespace HistOSets.Storage;

public enum ExchangeEntityKind { Material, TagGroup, Tag, AnalogGroup }
public enum ExchangeChangeKind { Add, Unchanged, Update, LocalChanged, Conflict, Deletion }
public enum ExchangeResolution { Unresolved, KeepLocal, UseIncoming, Skip }

/// <summary>A whole material or one shared dictionary entry. Names never determine identity.</summary>
public sealed record ExchangePreviewItem(string Key, ExchangeEntityKind Kind, Guid Id, string Name,
    ExchangeChangeKind Status, string LocalSummary, string IncomingSummary, string Warning,
    ExchangeResolution SuggestedResolution, int AffectedMaterialCount)
{
    public bool RequiresDecision => SuggestedResolution == ExchangeResolution.Unresolved;
}

/// <summary>Read-only plan tied to the exact package, catalog identity and state inspected.</summary>
public sealed class ExchangePreview
{
    public Guid TargetCatalogId { get; }
    public long TargetContentRevision { get; }
    public Guid SourceCatalogId => Package.SourceCatalogId;
    public string SourceCatalogName => Package.SourceCatalogName;
    public IReadOnlyList<ExchangePreviewItem> Items { get; }
    public IReadOnlyList<string> Warnings { get; }
    internal ExchangePackage Package { get; }
    internal string StateFingerprint { get; }
    internal string TargetPath { get; }
    /// <summary>Choices can survive a language-only refresh, but never a change on either side.</summary>
    public bool MatchesTargetState(ExchangePreview other) => other is not null &&
        TargetPath==other.TargetPath && TargetCatalogId==other.TargetCatalogId &&
        TargetContentRevision==other.TargetContentRevision && StateFingerprint==other.StateFingerprint &&
        Package.PackageId==other.Package.PackageId && Package.SourceCatalogId==other.Package.SourceCatalogId &&
        Package.ContentSha256==other.Package.ContentSha256;
    internal ExchangePreview(ExchangePackage package, Guid targetId, long revision, string fingerprint,
        string targetPath, IReadOnlyList<ExchangePreviewItem> items, IReadOnlyList<string> warnings)
    {
        Package = package; TargetCatalogId = targetId; TargetContentRevision = revision;
        StateFingerprint = fingerprint; TargetPath = targetPath; Items = items; Warnings = warnings;
    }
}

public sealed record ExchangeImportResult(int Added, int Updated, int Deleted, int Kept, int Skipped,
    int DemotedMaterials, string? BackupPath, bool Changed);

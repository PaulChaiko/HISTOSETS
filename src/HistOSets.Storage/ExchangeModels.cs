using System.Collections.ObjectModel;
using System.Globalization;

namespace HistOSets.Storage;

/// <summary>A selectable exchange root, including an explicitly requested deletion marker.</summary>
public sealed record ExchangeMaterialInfo(Guid Id, int Revision, string Name, string Status, bool IsDeleted,
    IReadOnlyDictionary<string, string> Names);

/// <summary>Only immutable SQLite scalar values cross the package boundary.</summary>
internal sealed class ExchangeRow
{
    internal IReadOnlyDictionary<string, object?> Values { get; }
    internal string Id => String("id")!;
    internal ExchangeRow(IDictionary<string, object?> values) =>
        Values = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(values, StringComparer.Ordinal));
    internal string? String(string column) => Values[column] as string;
    internal long Integer(string column) => Convert.ToInt64(Values[column], CultureInfo.InvariantCulture);
}

internal static class ExchangeSchema
{
    // Dependency order. Identifiers used in SQL come exclusively from this application-owned list.
    internal static readonly IReadOnlyList<string> LegacyTableNames = Array.AsReadOnly(new[] {
        "tag_groups", "tag_group_translations", "tags", "tag_translations", "analog_groups", "analog_group_translations",
        "images", "materials", "material_translations", "material_images", "elements", "element_translations",
        "regions", "material_tags", "image_calibrations"
    });
    internal static readonly IReadOnlyList<string> TableNames = Array.AsReadOnly(LegacyTableNames.Concat(new[] {
        "training_prompts", "training_prompt_translations"
    }).ToArray());
    internal static bool IsTrainingTable(string table) => table is "training_prompts" or "training_prompt_translations";

    internal static IReadOnlyList<ExchangeMaterialInfo> MaterialInfo(
        IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> tables, string locale)
    {
        var deletedRoots = tables["materials"].Where(r => r.String("deleted_utc") is not null).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var translations = tables["material_translations"].Where(r => r.String("deleted_utc") is null || deletedRoots.Contains(r.String("material_id")!))
            .GroupBy(r => r.String("material_id")!).ToDictionary(g => g.Key,
                g => (IReadOnlyDictionary<string, string>)new ReadOnlyDictionary<string, string>(
                    g.ToDictionary(r => r.String("locale")!, r => r.String("name")!, StringComparer.Ordinal)), StringComparer.Ordinal);
        return tables["materials"].Select(r => {
            var names = translations.GetValueOrDefault(r.Id) ?? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
            var name = HistOSets.Core.CatalogBrowser.Label(names, locale);
            if (string.IsNullOrWhiteSpace(name)) name = locale == "en" ? "Untitled object" : "Объект без названия";
            return new ExchangeMaterialInfo(Guid.Parse(r.Id), checked((int)r.Integer("revision")), name,
                r.String("status")!, r.String("deleted_utc") is not null, names);
        }).ToArray();
    }
}

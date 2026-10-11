using System.Globalization;
using System.Text;

namespace HistOSets.Core;

public sealed record CatalogTagGroup(Guid Id, IReadOnlyDictionary<string, string> Names)
{
    public bool IsSystem { get; init; }
    public string Code { get; init; } = "";
}
public sealed record CatalogTag(Guid Id, Guid? GroupId, IReadOnlyDictionary<string, string> Names);
public sealed record CatalogTaxonomy(IReadOnlyList<CatalogTagGroup> Groups, IReadOnlyList<CatalogTag> Tags,
    IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> MaterialTags)
{
    public static CatalogTaxonomy Empty { get; } = new([], [], new Dictionary<Guid, IReadOnlySet<Guid>>());
}

public static class CatalogBrowser
{
    // SQLite's built-in NOCASE covers ASCII only. Normalize names in .NET, including Cyrillic.
    public static string Normalize(string value) => value.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Replace('Ё', 'Е');

    public static string Label(IReadOnlyDictionary<string, string> names, string locale)
        => names.GetValueOrDefault(locale) ?? names.GetValueOrDefault("ru") ?? names.GetValueOrDefault("en")
            ?? names.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).FirstOrDefault() ?? (locale == "en" ? "Untitled" : "Без названия");

    public static string LanguageName(string locale, string displayLocale = "ru") => locale switch
    {
        "ru" => displayLocale == "en" ? "Russian" : "Русский",
        "en" => displayLocale == "en" ? "English" : "Английский",
        "und" => displayLocale == "en" ? "Language unspecified" : "Язык не указан", _ => locale
    };

    public static IReadOnlyList<AtlasSpecimen> Filter(AtlasCatalog catalog, string locale, string query,
        IReadOnlyCollection<Guid> selectedTags, CatalogTaxonomy taxonomy, bool alphabetic = false)
    {
        var tokens = Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var tags = taxonomy.Tags.Where(t => selectedTags.Contains(t.Id)).ToArray();
        if (tags.Length != selectedTags.Distinct().Count()) return [];
        var tagSets = tags.GroupBy(t => t.GroupId).Select(g => g.Select(t => t.Id).ToArray()).ToArray();
        var results = new List<AtlasSpecimen>();
        // Legacy records without UUID must never be merged merely because their names coincide.
        foreach (var group in catalog.Specimens.Select((s, i) => (s, key: s.MaterialId?.ToString() ?? "legacy:" + i)).GroupBy(x => x.key))
        {
            var views = group.Select(x => x.s).ToArray();
            if (!tokens.All(token => views.Any(s => Normalize(s.Name).Contains(token, StringComparison.Ordinal)
                || s.AnalogGroupId is not null && s.AnalogGroupNames.Values.Any(name => Normalize(name).Contains(token, StringComparison.Ordinal))))) continue;
            var material = views[0].MaterialId;
            var assigned = material is { } id ? taxonomy.MaterialTags.GetValueOrDefault(id) : null;
            if (!tagSets.All(set => assigned is not null && set.Any(assigned.Contains))) continue;
            var translated = ChooseLanguage(views, locale);
            results.Add(translated.FirstOrDefault(s => s.IsPrimaryImage) ?? translated[0]);
        }
        return alphabetic ? results.OrderBy(s => s.DisplayName, StringComparer.Create(CultureInfo.GetCultureInfo(locale == "en" ? "en-US" : "ru-RU"), true)).ToArray() : results;
    }

    public static IReadOnlyList<AtlasSpecimen> ChooseLanguage(IEnumerable<AtlasSpecimen> variants, string locale)
    {
        var all = variants.ToArray();
        var language = new[] { locale, "ru", "en", "und" }.FirstOrDefault(l => all.Any(s => s.Locale == l)) ?? all.FirstOrDefault()?.Locale;
        return all.Where(s => s.Locale == language).Select(s => s with { DisplayLocale = locale }).ToArray();
    }
}

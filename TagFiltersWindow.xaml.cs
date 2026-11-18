using System.Globalization;
using System.Windows;
using HistOSets.Controls;
using HistOSets.Core;
using HistOSets.Services;

namespace HistOSets;

public partial class TagFiltersWindow : WorkAreaWindow
{
    private readonly HashSet<Guid> selected;
    private readonly TagEntry[] entries;
    private bool ready;
    internal IReadOnlySet<Guid> SelectedTags => selected;

    internal TagFiltersWindow(CatalogTaxonomy taxonomy, IEnumerable<Guid> selectedTags, string locale)
    {
        selected = new HashSet<Guid>(selectedTags);
        selected.IntersectWith(taxonomy.Tags.Select(tag => tag.Id));
        var counts = new Dictionary<Guid, int>();
        foreach (var assigned in taxonomy.MaterialTags.Values)
            foreach (var tagId in assigned)
                counts[tagId] = counts.GetValueOrDefault(tagId) + 1;

        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo(locale == "en" ? "en-US" : "ru-RU"), true);
        var groups = taxonomy.Groups.ToDictionary(g => g.Id, g => CatalogBrowser.Label(g.Names, locale));
        var untyped = T("Без типа", "Untyped");
        entries = taxonomy.Tags
            .Select(tag => new TagEntry(tag.Id, tag.GroupId, CatalogBrowser.Label(tag.Names, locale),
                tag.GroupId is { } type ? groups.GetValueOrDefault(type, untyped) : untyped,
                counts.GetValueOrDefault(tag.Id), tag.Names.Values.Select(CatalogBrowser.Normalize).ToArray()))
            .OrderBy(entry => entry.Label, comparer).ThenBy(entry => entry.Type, comparer).ThenBy(entry => entry.Id).ToArray();

        InitializeComponent();
        Title = T("Фильтры по тегам", "Tag filters");
        FilterRule.Text = T("Внутри типа достаточно одного из выбранных тегов. Между типами должны совпасть все выбранные типы.",
            "Within each type, any selected tag can match. All selected types must match.");
        TypeLabel.Text = T("Тип тега", "Tag type");
        TagSearchLabel.Text = T("Поиск тегов на обоих языках", "Search tags in either language");
        NoTags.Text = T("Подходящих тегов нет.", "No matching tags.");
        ClearButton.Content = T("Снять выбор", "Clear selection");
        ApplyButton.Content = T("Применить", "Apply");
        CancelButton.Content = T("Отмена", "Cancel");
        var types = new[] { new TypeChoice(null, T("Все типы", "All types"), All: true) }
            .Concat(groups.OrderBy(group => group.Value, comparer).Select(group => new TypeChoice(group.Key, group.Value)))
            .Concat(entries.Any(entry => entry.TypeId is null) ? [new TypeChoice(null, untyped)] : []).ToArray();
        TypeSelector.ItemsSource = types;
        TypeSelector.SelectedIndex = 0;
        ready = true;
        ShowTags();
    }

    private static string T(string ru, string en) => UiLanguage.T(ru, en);
    private void Filters_Changed(object sender, RoutedEventArgs e)
    {
        if (ready) ShowTags();
    }
    private void ShowTags()
    {
        var type = TypeSelector.SelectedItem as TypeChoice;
        var search = CatalogBrowser.Normalize(TagSearchBox.Text.Trim());
        var rows = entries.Where(entry => (type is null || type.All || entry.TypeId == type.Id)
            && entry.SearchNames.Any(name => name.Contains(search, StringComparison.Ordinal)))
            .Select(entry => new TagChoice(entry.Id, entry.Label, T($"{entry.Type} · объектов: {entry.Count}", $"{entry.Type} · objects: {entry.Count}"),
                selected.Contains(entry.Id), SelectTag)).ToArray();
        TagList.ItemsSource = rows;
        NoTags.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }
    private void SelectTag(Guid id, bool value)
    {
        if (value) selected.Add(id);
        else selected.Remove(id);
        UpdateSelection();
    }
    private void UpdateSelection()
    {
        SelectionSummary.Text = T($"Выбрано тегов: {selected.Count}", $"Selected tags: {selected.Count}");
        ClearButton.IsEnabled = selected.Count != 0;
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        selected.Clear();
        ShowTags();
    }
    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private sealed record TypeChoice(Guid? Id, string Label, bool All = false);
    private sealed record TagEntry(Guid Id, Guid? TypeId, string Label, string Type, int Count, string[] SearchNames);
    public sealed class TagChoice(Guid id, string label, string detail, bool selected, Action<Guid, bool> changed)
    {
        public Guid Id { get; } = id;
        public string Label { get; } = label;
        public string Detail { get; } = detail;
        private bool isSelected = selected;
        public bool Selected
        {
            get => isSelected;
            set
            {
                if (isSelected == value) return;
                isSelected = value;
                changed(Id, value);
            }
        }
    }
}

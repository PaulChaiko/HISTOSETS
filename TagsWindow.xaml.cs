using System.Windows;
using System.Windows.Controls;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;

namespace HistOSets;

public partial class TagsWindow : Window
{
    private readonly CatalogStore store;
    private readonly Guid? material;
    private readonly string locale;
    private CatalogTaxonomy taxonomy = CatalogTaxonomy.Empty;
    private List<TagChoice> choices = [];
    private HashSet<Guid> original = [];
    private bool loaded;
    private sealed record GroupChoice(Guid? Id, bool All, string Name, bool IsSystem = false);
    private static string T(string ru, string en) => UiLanguage.T(ru, en);

    public sealed class TagChoice
    {
        public required Guid Id { get; init; }
        public Guid? GroupId { get; init; }
        public required string Name { get; init; }
        public required string SearchText { get; init; }
        public bool Assigned { get; set; }
        public Visibility AssignmentVisibility { get; init; }
    }

    public TagsWindow(CatalogStore store, Guid? material, string? name, string locale)
    {
        this.store = store;
        this.material = material;
        this.locale = locale;
        InitializeComponent();
        Title = T("Типовые теги — HISTOSETS", "Typed tags — HISTOSETS");
        Heading.Text = material is null ? T("Типовые теги", "Typed tags") : T("Теги объекта: ", "Object tags: ") + name;
        Instructions.Text = material is null
            ? T("Выберите тип и создайте значение тега или измените существующее. Типы, заданные системой, фиксированы. Старые группы сохранены; теги из них можно перенести в нужный тип через «Изменить». Изменения справочника сохраняются сразу.",
                "Choose a type and create or edit a tag value. System types are fixed. Legacy groups are preserved; use Edit to move their tags to a suitable type. Dictionary changes are saved immediately.")
            : T("Выберите тип и отметьте существующие теги либо создайте новые значения. Поиск учитывает оба языка. Назначения сохраняются кнопкой внизу. Изменения справочника сохраняются сразу и действуют для всех объектов.",
                "Choose a type and check existing tags or create new values. Search covers both languages. Save assignments using the button below. Dictionary changes are saved immediately and affect all objects.");
        TypesLabel.Text = T("Типы тегов", "Tag types");
        TagsLabel.Text = T("Значения тегов", "Tag values");
        TagSearch.ToolTip = T("Поиск существующего значения на RU или EN", "Search existing values in RU or EN");
        System.Windows.Automation.AutomationProperties.SetName(TagSearch, T("Поиск тегов", "Search tags"));
        EditGroupButton.Content = T("Изменить группу…", "Edit group…");
        DeleteGroupButton.Content = T("Удалить", "Delete");
        EditGroupButton.ToolTip = DeleteGroupButton.ToolTip = T("Доступно только для старых пользовательских групп", "Available only for legacy custom groups");
        NewTagButton.Content = T("Создать тег…", "Create tag…");
        EditTagButton.Content = T("Изменить…", "Edit…");
        DeleteTagButton.Content = T("Удалить", "Delete");
        SaveAssignments.Content = T("Сохранить теги объекта", "Save object tags");
        CloseButton.Content = T("Закрыть", "Close");
        SaveAssignments.Visibility = material is null ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
        Closing += (_, e) =>
        {
            if (material is null || original.SetEquals(Assigned())) return;
            var answer = MessageBox.Show(this, T("Сохранить изменения тегов объекта? Выберите «Нет», чтобы закрыть без сохранения назначений.",
                "Save changes to object tags? Choose No to close without saving assignments."), "HISTOSETS", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) e.Cancel = true;
            else if (answer == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        };
    }

    private HashSet<Guid> Assigned() => choices.Where(c => c.Assigned).Select(c => c.Id).ToHashSet();

    private void Refresh(Guid? selectGroup = null, Guid? selectTag = null)
    {
        var pending = loaded && material is not null ? Assigned() : null;
        var previous = GroupList.SelectedItem as GroupChoice;
        taxonomy = store.LoadTaxonomy();
        original = material is { } m ? taxonomy.MaterialTags.GetValueOrDefault(m)?.ToHashSet() ?? [] : [];
        choices = taxonomy.Tags.Select(t => new TagChoice
        {
            Id = t.Id, GroupId = t.GroupId, Name = CatalogBrowser.Label(t.Names, locale),
            SearchText = CatalogBrowser.Normalize(string.Join(" ", t.Names.Values)),
            Assigned = (pending ?? original).Contains(t.Id),
            AssignmentVisibility = material is null ? Visibility.Collapsed : Visibility.Visible
        }).ToList();
        var groups = new List<GroupChoice> { new(null, true, T("Все типы", "All types")) };
        groups.AddRange(taxonomy.Groups.OrderByDescending(g => g.IsSystem).Select(g => new GroupChoice(g.Id, false,
            CatalogBrowser.Label(g.Names, locale) + (g.IsSystem ? "" : T(" (старая группа)", " (legacy group)")), g.IsSystem)));
        if (taxonomy.Tags.Any(t => t.GroupId is null)) groups.Add(new(null, false, T("Без типа (старые теги)", "Untyped (legacy tags)")));
        GroupList.ItemsSource = groups;
        GroupList.SelectedItem = selectGroup is not null ? groups.FirstOrDefault(g => g.Id == selectGroup) ?? groups[0]
            : groups.FirstOrDefault(g => previous is not null && g.Id == previous.Id && g.All == previous.All) ?? groups[0];
        loaded = true;
        RefreshTags();
        if (selectTag is not null) TagList.SelectedItem = choices.FirstOrDefault(t => t.Id == selectTag);
        OperationStatus.Text = choices.Count == 0 ? T("Выберите тип и создайте первое значение тега.", "Choose a type and create the first tag value.")
            : T($"Типов и старых групп: {taxonomy.Groups.Count}. Тегов: {taxonomy.Tags.Count}.", $"Types and legacy groups: {taxonomy.Groups.Count}. Tags: {taxonomy.Tags.Count}.");
    }

    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TagList is null) return;
        var editable = GroupList.SelectedItem is GroupChoice { Id: not null, IsSystem: false };
        EditGroupButton.IsEnabled = DeleteGroupButton.IsEnabled = editable;
        RefreshTags();
    }

    private void TagSearch_Changed(object sender, TextChangedEventArgs e) { if (TagList is not null) RefreshTags(); }

    private void RefreshTags()
    {
        var group = GroupList.SelectedItem as GroupChoice;
        var tokens = CatalogBrowser.Normalize(TagSearch.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        TagList.ItemsSource = choices.Where(t => (group is null || group.All || t.GroupId == group.Id)
            && tokens.All(token => t.SearchText.Contains(token, StringComparison.Ordinal)))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private bool Try(Action action)
    {
        try { action(); return true; }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            OperationStatus.Text = T("Не удалось сохранить изменения. ", "Could not save changes. ") + UiLanguage.Error(ex);
            return false;
        }
    }

    private void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupChoice { Id: { } id, IsSystem: false }) return;
        var group = taxonomy.Groups.Single(g => g.Id == id);
        var dialog = new TaxonomyEditWindow(T("Изменить старую группу", "Edit legacy group"), group.Names, null, null,
            (ru, en, _) => store.SaveTagGroup(group.Id, ru, en)) { Owner = this };
        if (dialog.ShowDialog() == true) Try(() => Refresh(id));
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupChoice { Id: { } id, IsSystem: false }) return;
        if (MessageBox.Show(this, T("Удалить старую группу? Сначала перенесите или удалите все её теги.", "Delete the legacy group? First move or delete all its tags."),
            "HISTOSETS", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Try(() => { store.DeleteTagGroup(id); Refresh(); });
    }

    private void NewTag_Click(object sender, RoutedEventArgs e) => EditTag(null);
    private void EditTag_Click(object sender, RoutedEventArgs e)
    {
        if (TagList.SelectedItem is TagChoice choice) EditTag(taxonomy.Tags.Single(t => t.Id == choice.Id));
        else OperationStatus.Text = T("Выберите строку тега для изменения.", "Select a tag to edit.");
    }

    private void EditTag(CatalogTag? tag)
    {
        var groups = taxonomy.Groups.Select(g => ((Guid?)g.Id, CatalogBrowser.Label(g.Names, locale)
            + (g.IsSystem ? "" : T(" (старая группа)", " (legacy group)")))).ToList();
        // Untyped legacy values remain editable, but new values always receive a type.
        if (tag is { GroupId: null }) groups.Insert(0, (null, T("Без типа (старый тег)", "Untyped (legacy tag)")));
        Guid? saved = null;
        Guid? savedGroup = null;
        var dialog = new TaxonomyEditWindow(tag is null ? T("Создать тег", "Create tag") : T("Изменить тег", "Edit tag"), tag?.Names, groups,
            tag is not null ? tag.GroupId : (GroupList.SelectedItem as GroupChoice)?.Id,
            (ru, en, group) => { saved = store.SaveTag(tag?.Id, group, ru, en); savedGroup = group; }) { Owner = this };
        if (dialog.ShowDialog() == true) Try(() =>
        {
            TagSearch.Clear();
            Refresh(savedGroup, saved);
        });
    }

    private void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (TagList.SelectedItem is not TagChoice choice) { OperationStatus.Text = T("Выберите строку тега для удаления.", "Select a tag to delete."); return; }
        var uses = taxonomy.MaterialTags.Count(p => p.Value.Contains(choice.Id));
        if (MessageBox.Show(this, T($"Удалить тег «{choice.Name}»? Он будет снят с объектов: {uses}.", $"Delete tag “{choice.Name}”? It will be removed from {uses} objects."),
            "HISTOSETS", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Try(() => { store.DeleteTag(choice.Id); Refresh(); });
    }

    private bool Save()
    {
        if (material is not { } id) return true;
        return Try(() => { var assigned = Assigned(); store.SetMaterialTags(id, assigned); original = assigned; });
    }

    private void SaveAssignments_Click(object sender, RoutedEventArgs e) { if (Save()) Close(); }
}

internal sealed class TaxonomyEditWindow : Window
{
    private sealed record GroupOption(Guid? Id, string Name);
    private static string T(string ru, string en) => UiLanguage.T(ru, en);

    public TaxonomyEditWindow(string title, IReadOnlyDictionary<string, string>? names,
        IReadOnlyList<(Guid? Id, string Name)>? groups, Guid? selectedGroup, Action<string, string, Guid?> save, bool requireBoth = false)
    {
        Title = title + " — HISTOSETS";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 14;
        Background = (System.Windows.Media.Brush)FindResource("BrandBackgroundBrush");
        var panel = new StackPanel { Margin = new Thickness(22), Background = (System.Windows.Media.Brush)FindResource("BrandSurfaceBrush") };
        Content = panel;
        var ru = new TextBox { Text = names?.GetValueOrDefault("ru") ?? "", MaxLength = 120, Margin = new Thickness(0, 4, 0, 12) };
        var en = new TextBox { Text = names?.GetValueOrDefault("en") ?? "", MaxLength = 120, Margin = new Thickness(0, 4, 0, 12) };
        panel.Children.Add(new TextBlock { Text = T("Название на русском", "Name in Russian") }); panel.Children.Add(ru);
        panel.Children.Add(new TextBlock { Text = T("Название на английском", "Name in English") }); panel.Children.Add(en);
        ComboBox? groupBox = null;
        if (groups is not null)
        {
            var options = groups.Select(g => new GroupOption(g.Id, g.Name)).ToArray();
            panel.Children.Add(new TextBlock { Text = T("Тип тега", "Tag type") });
            groupBox = new ComboBox { ItemsSource = options, DisplayMemberPath = "Name", SelectedItem = options.FirstOrDefault(g => g.Id == selectedGroup) ?? options.FirstOrDefault(),
                Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(6) };
            panel.Children.Add(groupBox);
        }
        var status = new TextBlock { Text = requireBoth
                ? T("Заполните названия на обоих языках.", "Enter names in both languages.")
                : groups is null
                    ? T("Достаточно названия на одном языке. Пустое поле удаляет соответствующий перевод.", "A name in one language is sufficient. An empty field removes that translation.")
                    : T("Достаточно названия на одном языке. Пустое поле удаляет перевод. Если значение уже существует в выбранном типе, используйте его в списке тегов.",
                        "A name in one language is sufficient. An empty field removes that translation. If the value already exists in the selected type, use it from the tag list."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = T("Сохранить", "Save"), IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (requireBoth && (string.IsNullOrWhiteSpace(ru.Text) || string.IsNullOrWhiteSpace(en.Text)))
            {
                status.Text = T("Заполните названия на обоих языках.", "Enter names in both languages.");
                return;
            }
            if (string.IsNullOrWhiteSpace(ru.Text) && string.IsNullOrWhiteSpace(en.Text))
            {
                status.Text = T("Укажите название хотя бы на одном языке.", "Enter a name in at least one language.");
                return;
            }
            if (groups is not null && groupBox?.SelectedItem is null)
            {
                status.Text = T("Выберите тип тега.", "Choose a tag type.");
                return;
            }
            try { save(ru.Text, en.Text, (groupBox?.SelectedItem as GroupOption)?.Id); DialogResult = true; }
            catch (Exception ex) { ErrorLog.Write(ex); status.Text = T("Не удалось сохранить. ", "Could not save. ") + UiLanguage.Error(ex); }
        };
        actions.Children.Add(ok); actions.Children.Add(new Button { Content = T("Отмена", "Cancel"), IsCancel = true }); panel.Children.Add(actions);
        Loaded += (_, _) => ru.Focus();
    }
}

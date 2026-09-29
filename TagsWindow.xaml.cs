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
    private sealed record GroupChoice(Guid? Id, bool All, string Name);
    public sealed class TagChoice
    {
        public required Guid Id { get; init; }
        public Guid? GroupId { get; init; }
        public required string Name { get; init; }
        public bool Assigned { get; set; }
        public Visibility AssignmentVisibility { get; init; }
    }
    public TagsWindow(CatalogStore store, Guid? material, string? name, string locale)
    {
        this.store = store;
        this.material = material;
        this.locale = locale;
        InitializeComponent();
        if (material is not null) Heading.Text = "Теги материала: " + name;
        Instructions.Text = material is null
            ? "Создайте группы (например, «Дисциплина» или «Тема») и теги. Для назначения выберите материал в главном окне и нажмите «Теги…». Изменения справочника сохраняются сразу."
            : "Отметьте теги и нажмите «Сохранить теги материала». Они общие для всех языков и изображений этой карточки. Изменения самого справочника сохраняются сразу.";
        SaveAssignments.Visibility = material is null ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
        Closing += (_, e) =>
        {
            if (material is null || original.SetEquals(Assigned())) return;
            var answer = MessageBox.Show(this, "Сохранить изменения тегов материала?", "HISTOSETS", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) e.Cancel = true;
            else if (answer == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        };
    }
    private HashSet<Guid> Assigned() => choices.Where(c => c.Assigned).Select(c => c.Id).ToHashSet();
    private void Refresh(Guid? selectGroup = null)
    {
        var pending = material is not null && choices.Count > 0 ? Assigned() : null;
        var previous = GroupList.SelectedItem as GroupChoice;
        taxonomy = store.LoadTaxonomy();
        original = material is { } m ? taxonomy.MaterialTags.GetValueOrDefault(m)?.ToHashSet() ?? [] : [];
        choices = taxonomy.Tags.Select(t => new TagChoice
        {
            Id = t.Id, GroupId = t.GroupId, Name = CatalogBrowser.Label(t.Names, locale), Assigned = (pending ?? original).Contains(t.Id),
            AssignmentVisibility = material is null ? Visibility.Collapsed : Visibility.Visible
        }).ToList();
        var groups = new List<GroupChoice> { new(null, true, "Все группы"), new(null, false, "Без группы") };
        groups.AddRange(taxonomy.Groups.Select(g => new GroupChoice(g.Id, false, CatalogBrowser.Label(g.Names, locale))));
        GroupList.ItemsSource = groups;
        GroupList.SelectedItem = selectGroup is not null ? groups.First(g => g.Id == selectGroup) : groups.FirstOrDefault(g => previous is not null && g.Id == previous.Id && g.All == previous.All) ?? groups[0];
        RefreshTags();
        OperationStatus.Text = choices.Count == 0 ? "Тегов пока нет. Начните с создания группы и тега." : $"Групп: {taxonomy.Groups.Count}. Тегов: {taxonomy.Tags.Count}.";
    }
    private void Group_Changed(object sender, SelectionChangedEventArgs e) { if (TagList is not null) RefreshTags(); }
    private void RefreshTags()
    {
        var group = GroupList.SelectedItem as GroupChoice;
        TagList.ItemsSource = choices.Where(t => group is null || group.All || t.GroupId == group.Id).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private bool Try(Action action)
    {
        try { action(); return true; }
        catch (Exception ex) { ErrorLog.Write(ex); OperationStatus.Text = ex.Message; return false; }
    }
    private void NewGroup_Click(object sender, RoutedEventArgs e) => EditGroup(null);
    private void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((GroupList.SelectedItem as GroupChoice)?.Id is { } id) EditGroup(taxonomy.Groups.Single(g => g.Id == id));
        else OperationStatus.Text = "Выберите созданную группу.";
    }
    private void EditGroup(CatalogTagGroup? group)
    {
        Guid? saved = null;
        var dialog = new TaxonomyEditWindow(group is null ? "Создать группу" : "Изменить группу", group?.Names, null, null,
            (ru, en, _) => saved = store.SaveTagGroup(group?.Id, ru, en)) { Owner = this };
        if (dialog.ShowDialog() == true) Refresh(saved);
    }
    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((GroupList.SelectedItem as GroupChoice)?.Id is not { } id) { OperationStatus.Text = "Выберите созданную группу."; return; }
        if (MessageBox.Show(this, "Удалить выбранную группу? Группу с тегами удалить нельзя.", "HISTOSETS", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Try(() => { store.DeleteTagGroup(id); Refresh(); });
    }
    private void NewTag_Click(object sender, RoutedEventArgs e) => EditTag(null);
    private void EditTag_Click(object sender, RoutedEventArgs e)
    {
        if (TagList.SelectedItem is TagChoice choice) EditTag(taxonomy.Tags.Single(t => t.Id == choice.Id));
        else OperationStatus.Text = "Выберите строку тега для изменения.";
    }
    private void EditTag(CatalogTag? tag)
    {
        var groups = new List<(Guid? Id, string Name)> { (null, "Без группы") };
        groups.AddRange(taxonomy.Groups.Select(g => ((Guid?)g.Id, CatalogBrowser.Label(g.Names, locale))));
        var dialog = new TaxonomyEditWindow(tag is null ? "Создать тег" : "Изменить тег", tag?.Names, groups,
            tag is not null ? tag.GroupId : (GroupList.SelectedItem as GroupChoice)?.Id,
            (ru, en, group) => store.SaveTag(tag?.Id, group, ru, en)) { Owner = this };
        if (dialog.ShowDialog() == true) Refresh();
    }
    private void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (TagList.SelectedItem is not TagChoice choice) { OperationStatus.Text = "Выберите строку тега для удаления."; return; }
        var uses = taxonomy.MaterialTags.Count(p => p.Value.Contains(choice.Id));
        if (MessageBox.Show(this, $"Удалить тег «{choice.Name}»? Он будет снят с материалов: {uses}.", "HISTOSETS", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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
    public TaxonomyEditWindow(string title, IReadOnlyDictionary<string, string>? names,
        IReadOnlyList<(Guid? Id, string Name)>? groups, Guid? selectedGroup, Action<string, string, Guid?> save)
    {
        Title = title + " — HISTOSETS";
        Width = 540;
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
        panel.Children.Add(new TextBlock { Text = "Название на русском" }); panel.Children.Add(ru);
        panel.Children.Add(new TextBlock { Text = "Название на английском (необязательно)" }); panel.Children.Add(en);
        ComboBox? groupBox = null;
        if (groups is not null)
        {
            var options = groups.Select(g => new GroupOption(g.Id, g.Name)).ToArray();
            panel.Children.Add(new TextBlock { Text = "Группа" });
            groupBox = new ComboBox { ItemsSource = options, DisplayMemberPath = "Name", SelectedItem = options.FirstOrDefault(g => g.Id == selectedGroup) ?? options[0], Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(6) };
            panel.Children.Add(groupBox);
        }
        var status = new TextBlock { Text = "Достаточно названия на одном языке. Пустое поле удаляет соответствующий перевод.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Сохранить", IsDefault = true };
        ok.Click += (_, _) =>
        {
            try { save(ru.Text, en.Text, (groupBox?.SelectedItem as GroupOption)?.Id); DialogResult = true; }
            catch (Exception ex) { ErrorLog.Write(ex); status.Text = ex.Message; }
        };
        actions.Children.Add(ok); actions.Children.Add(new Button { Content = "Отмена", IsCancel = true }); panel.Children.Add(actions);
        Loaded += (_, _) => ru.Focus();
    }
}

using System.Windows;
using System.Windows.Controls;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;

namespace HistOSets;

public partial class AnalogGroupsWindow : Window
{
    private readonly CatalogStore store;
    private readonly Guid? material;
    private readonly string locale;
    private Guid? original;
    private IReadOnlyList<CatalogAnalogGroup> groups = [];
    private sealed record GroupChoice(Guid? Id, string Name);
    private static string T(string ru, string en) => UiLanguage.T(ru, en);

    public AnalogGroupsWindow(CatalogStore store, Guid? material, string? name, string locale)
    {
        this.store = store;
        this.material = material;
        this.locale = locale;
        InitializeComponent();
        Title = T("Группы аналогов — HISTOSETS", "Analogue groups — HISTOSETS");
        Heading.Text = material is null ? T("Группы аналогов", "Analogue groups") : T("Аналоги объекта: ", "Object analogues: ") + name;
        Instructions.Text = material is null
            ? T("Создавайте группы аналогов с названиями на RU и EN. Для назначения группы выберите объект в главном окне. Изменения справочника сохраняются сразу. Группу с объектами удалить нельзя.",
                "Create analogue groups with names in RU and EN. To assign a group, select an object in the main window. Dictionary changes are saved immediately. A group containing objects cannot be deleted.")
            : T("Выберите группу для этого объекта или «Без группы аналогов» и нажмите «Сохранить назначение». Объект может состоять в одной группе. Названия групп используются как общие ответы в самопроверке. Создание, переименование и удаление групп сохраняются сразу.",
                "Choose a group for this object or “No analogue group”, then save the assignment. An object can belong to one group. Group names are used as shared answers in self-testing. Creating, renaming and deleting groups is saved immediately.");
        NewGroupButton.Content = T("Создать группу…", "Create group…");
        EditGroupButton.Content = T("Изменить…", "Edit…");
        DeleteGroupButton.Content = T("Удалить", "Delete");
        SaveAssignmentButton.Content = T("Сохранить назначение", "Save assignment");
        SaveAssignmentButton.Visibility = material is null ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Content = T("Закрыть", "Close");
        original = material is { } id ? store.Load().Specimens.FirstOrDefault(s => s.MaterialId == id)?.AnalogGroupId : null;
        Refresh(original);
        Closing += (_, e) =>
        {
            if (material is null || SelectedId == original) return;
            var answer = MessageBox.Show(this, T("Сохранить назначение группы аналогов? Выберите «Нет», чтобы закрыть без сохранения назначения.",
                "Save the analogue group assignment? Choose No to close without saving the assignment."), "HISTOSETS", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) e.Cancel = true;
            else if (answer == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        };
    }

    private Guid? SelectedId => (GroupList.SelectedItem as GroupChoice)?.Id;

    private void Refresh(Guid? selection)
    {
        groups = store.LoadAnalogGroups();
        var choices = new List<GroupChoice>();
        if (material is not null) choices.Add(new(null, T("Без группы аналогов", "No analogue group")));
        choices.AddRange(groups.OrderBy(g => g.Label(locale), StringComparer.CurrentCultureIgnoreCase).Select(g => new GroupChoice(g.Id, g.Label(locale))));
        GroupList.ItemsSource = choices;
        GroupList.SelectedItem = choices.FirstOrDefault(g => g.Id == selection) ?? (material is not null ? choices.FirstOrDefault() : null);
        UpdateButtons();
        OperationStatus.Text = groups.Count == 0 ? T("Групп аналогов пока нет. Создайте первую группу.", "There are no analogue groups yet. Create the first group.")
            : T($"Групп аналогов: {groups.Count}.", $"Analogue groups: {groups.Count}.");
    }

    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EditGroupButton is not null) UpdateButtons();
    }

    private void UpdateButtons() => EditGroupButton.IsEnabled = DeleteGroupButton.IsEnabled = SelectedId is not null;

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

    private void NewGroup_Click(object sender, RoutedEventArgs e) => Edit(null);
    private void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId is { } id) Edit(groups.Single(g => g.Id == id));
    }

    private void Edit(CatalogAnalogGroup? group)
    {
        Guid? saved = null;
        var dialog = new TaxonomyEditWindow(group is null ? T("Создать группу аналогов", "Create analogue group") : T("Изменить группу аналогов", "Edit analogue group"),
            group?.Names, null, null, (ru, en, _) => saved = store.SaveAnalogGroup(group?.Id, ru, en), requireBoth: true) { Owner = this };
        if (dialog.ShowDialog() == true) Try(() => Refresh(saved));
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupChoice { Id: { } id } selected) return;
        if (MessageBox.Show(this, T($"Удалить группу «{selected.Name}»? У всех объектов этой группы сначала необходимо сохранить назначение «Без группы аналогов» либо другой группы.",
            $"Delete group “{selected.Name}”? First save “No analogue group” or another group for every object in this group."),
            "HISTOSETS", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Try(() => { store.DeleteAnalogGroup(id); Refresh(null); });
    }

    private bool Save()
    {
        if (material is not { } id) return true;
        return Try(() => { var selected = SelectedId; store.SetMaterialAnalogGroup(id, selected); original = selected; });
    }

    private void SaveAssignment_Click(object sender, RoutedEventArgs e) { if (Save()) Close(); }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;

namespace HistOSets;

/// <summary>Read-only preparation and eligibility review. This window does not execute or save attempts.</summary>
public partial class TrainingSetupWindow : Window
{
    private readonly CatalogStore store;
    private readonly List<MaterialRow> materials = [];
    private readonly List<MethodRow> methods = [];
    private readonly HashSet<Guid> selectedTags = [];
    private CatalogTaxonomy taxonomy = CatalogTaxonomy.Empty;
    private bool busy;
    private bool updating;
    private bool closed;
    private bool initialized;
    private Func<string>? statusFactory;
    private sealed record TagChoice(Guid Id, string Label, string SearchText);
    private sealed record OrderChoice(TrainingOrder Value, string Label);
    private sealed record EligibilityRow(string Material, string Method, int Count, string Reason);
    private sealed record QueueRow(int Number, string Method, string Material, string Answer, TrainingQuestion Question);

    public TrainingCatalog? Catalog { get; private set; }
    public TrainingPlan? Plan { get; private set; }
    public bool IsBusy => busy;

    public TrainingSetupWindow(CatalogStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        InitializeComponent();
        foreach (var method in Enum.GetValues<TrainingMethod>()) methods.Add(new(method, InputsChanged) { IsSelected = true });
        MethodList.ItemsSource = methods;
        TagSelector.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(TagText_Changed));
        initialized = true;
        UiLanguage.Changed += Language_Changed;
        ApplyLanguage();
        Loaded += Window_Loaded;
        Closing += (_, e) => { if (busy) e.Cancel = true; };
        Closed += (_, _) => { closed = true; UiLanguage.Changed -= Language_Changed; };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        if (Catalog is null) await ReloadCatalogAsync();
    }

    private static string T(string ru, string en) => UiLanguage.T(ru, en);
    private void Language_Changed(object? sender, EventArgs e)
    {
        ClearPlan();
        ApplyLanguage();
        ShowStatus(() => T("Язык изменён. Сформируйте план заново: задания требуют названий и учебных описаний на выбранном языке.",
            "Language changed. Build the plan again: questions require names and learning descriptions in the selected language."));
    }

    private void ApplyLanguage()
    {
        updating = true;
        var order = CurrentOrder;
        Title = T("HISTOSETS — подготовка самопроверки", "HISTOSETS — self-test preparation");
        Heading.Text = T("Подготовка самопроверки", "Prepare a self-test");
        StageNotice.Text = T("В update7.1 можно проверить материалы и сформировать план. Выполнение заданий и оценивание появятся на следующих этапах.",
            "In update7.1 you can check materials and prepare a plan. Question execution and scoring will follow in later stages.");
        SetupTab.Header = T("Настройка занятия", "Session setup");
        PlanTab.Header = T("Проверка и план", "Review and plan");
        TargetHeading.Text = T("1. Объекты для заданий", "1. Question targets");
        TargetHint.Text = T("Выберите одну карточку или группу. Черновики тоже доступны: пригодность определяется отдельно для каждого метода. Фильтры меняют список, выбор сохраняется.",
            "Select one card or a group. Drafts are included: eligibility is checked separately for each method. Filters change the list and preserve your selection.");
        SearchLabel.Text = T("Название объекта, группы аналогов или UUID", "Object name, analog group or UUID");
        SearchBox.ToolTip = T("Поиск по всем языкам, без учёта регистра и е/ё", "Search all languages, ignoring case and е/ё");
        TagLabel.Text = T("Добавить тег в фильтр — введите для поиска", "Add a tag filter — type to search");
        SelectVisibleButton.Content = T("Выбрать показанные", "Select visible");
        ClearSelectionButton.Content = T("Снять весь выбор", "Clear all selection");
        ResetFiltersButton.Content = T("Сбросить фильтры", "Reset filters");
        MethodsHeading.Text = T("2. Методы самопроверки", "2. Self-test methods");
        OrderLabel.Text = T("3. Порядок заданий", "3. Question order");
        OrderSelector.ItemsSource = new[] { new OrderChoice(TrainingOrder.Random, T("Случайный", "Random")),
            new OrderChoice(TrainingOrder.ByMethod, T("По методам", "By method")), new OrderChoice(TrainingOrder.ByObject, T("По объектам", "By object")) };
        OrderSelector.SelectedValue = order;
        PoolHint.Text = T("Выбранные карточки определяют темы заданий. Справочники ответов и варианты изображений берутся из всей базы; аналоги учитываются как один правильный ответ. В будущих заданиях названия будут выбираться из списка с поиском.",
            "Selected cards determine question targets. Answer dictionaries and image choices use the whole catalog; analogs count as one correct answer. Future naming questions will use a searchable answer list.");
        EligibilityHeading.Text = T("Пригодность по объектам и методам", "Eligibility by object and method");
        EligibilityMaterialColumn.Header = QueueMaterialColumn.Header = T("Объект", "Object");
        EligibilityMethodColumn.Header = QueueMethodColumn.Header = T("Метод", "Method");
        EligibilityCountColumn.Header = T("Заданий", "Questions");
        EligibilityReasonColumn.Header = T("Причины исключения и замечания", "Exclusion reasons and notes");
        QueueAnswerColumn.Header = T("Эталонный ответ", "Expected answer");
        QueueHeading.Text = T("Очередь заданий — предварительный просмотр", "Question queue — preview");
        RefreshButton.Content = T("Обновить материалы", "Refresh materials");
        BuildPlanButton.Content = T("Сформировать план", "Build plan");
        CloseButton.Content = T("Закрыть", "Close");
        LanguageButton.ToolTip = T("Переключить интерфейс и язык заданий на английский", "Switch interface and question language to Russian");
        foreach (var row in methods) row.RefreshLanguage();
        foreach (var row in materials) row.RefreshLanguage();
        updating = false;
        BuildTagChoices("");
        ApplyFilter();
        RenderPlan();
        if (statusFactory is not null) OperationStatus.Text = statusFactory();
        RefreshCommands();
    }

    public async Task ReloadCatalogAsync()
    {
        if (busy || closed) return;
        SetBusy(true);
        ShowStatus(() => T("Чтение материалов и проверка изображений…", "Reading materials and verifying images…"));
        try
        {
            var result = await Task.Run(() => (Catalog: store.CaptureTrainingCatalog(ImageLoader.ReadMetadata), Taxonomy: store.LoadTaxonomy()));
            var changed = Catalog is not null && Catalog.SourceFingerprint != result.Catalog.SourceFingerprint;
            InstallCatalog(result.Catalog, result.Taxonomy);
            ShowStatus(() => changed
                ? T("Материалы изменились. Предыдущий план сброшен; проверьте выбор и сформируйте новый.", "Materials changed. The previous plan was cleared; review the selection and build a new one.")
                : T("Материалы проверены. Выберите объекты и методы, затем сформируйте план.", "Materials checked. Select objects and methods, then build a plan."));
        }
        catch (Exception ex) { ClearPlan(); ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void InstallCatalog(TrainingCatalog catalog, CatalogTaxonomy nextTaxonomy)
    {
        var selected = materials.Where(row => row.IsSelected).Select(row => row.Material.Id).ToHashSet();
        if (Catalog is not null && Catalog.SourceFingerprint != catalog.SourceFingerprint) ClearPlan();
        Catalog = catalog;
        taxonomy = nextTaxonomy;
        updating = true;
        materials.Clear();
        foreach (var material in catalog.Materials) materials.Add(new(material, InputsChanged) { IsSelected = selected.Contains(material.Id) });
        selectedTags.IntersectWith(taxonomy.Tags.Select(tag => tag.Id));
        updating = false;
        BuildTagChoices("");
        ApplyFilter();
        RenderPlan();
    }

    public void SelectMaterials(IEnumerable<Guid> ids)
    {
        if (busy || closed) return;
        var selected = ids.ToHashSet();
        updating = true;
        foreach (var row in materials) row.IsSelected = selected.Contains(row.Material.Id);
        updating = false;
        InputsChanged();
    }

    public void SelectMethods(IEnumerable<TrainingMethod> values)
    {
        if (busy || closed) return;
        var selected = values.ToHashSet();
        updating = true;
        foreach (var row in methods) row.IsSelected = selected.Contains(row.Method);
        updating = false;
        InputsChanged();
    }

    public void SetOrder(TrainingOrder order)
    {
        if (busy || closed) return;
        if (!Enum.IsDefined(order)) throw new ArgumentOutOfRangeException(nameof(order));
        OrderSelector.SelectedValue = order;
    }

    public void SetTagFilters(IEnumerable<Guid> ids)
    {
        if (busy || closed) return;
        selectedTags.Clear();
        selectedTags.UnionWith(ids.Intersect(taxonomy.Tags.Select(tag => tag.Id)));
        InputsChanged();
        ApplyFilter();
    }

    private TrainingOrder CurrentOrder => OrderSelector?.SelectedValue is TrainingOrder order ? order : TrainingOrder.Random;

    public async Task BuildPlanAsync()
    {
        if (busy || closed || Catalog is null) return;
        var configuration = new TrainingConfiguration(materials.Where(row => row.IsSelected).Select(row => row.Material.Id),
            methods.Where(row => row.IsSelected).Select(row => row.Method), UiLanguage.Locale, CurrentOrder, Random.Shared.Next());
        if (configuration.MaterialIds.Count == 0 || configuration.Methods.Count == 0) return;
        var fingerprint = Catalog.SourceFingerprint;
        SetBusy(true);
        ClearPlan();
        ShowStatus(() => T("Повторная проверка материалов и формирование плана…", "Rechecking materials and building the plan…"));
        try
        {
            var result = await Task.Run(() =>
            {
                var snapshot = store.CaptureTrainingCatalog(ImageLoader.ReadMetadata);
                return (Catalog: snapshot, Taxonomy: store.LoadTaxonomy(), Plan: snapshot.SourceFingerprint == fingerprint ? TrainingPlanner.Build(snapshot, configuration) : null);
            });
            InstallCatalog(result.Catalog, result.Taxonomy);
            if (result.Plan is null)
            {
                TrainingTabs.SelectedItem = SetupTab;
                ShowStatus(() => T("База или файлы изображений изменились. Данные обновлены. Проверьте выбранные материалы и ещё раз нажмите «Сформировать план».",
                    "The database or image files changed. Data has been refreshed. Review your selection and choose Build plan again."));
                return;
            }
            Plan = result.Plan;
            RenderPlan();
            TrainingTabs.SelectedItem = PlanTab;
            ShowStatus(() => T("План сформирован без изменения базы. Это предварительный просмотр; занятие ещё не запускается.",
                "The plan was prepared without changing the database. This is a preview; the session does not start yet."));
        }
        catch (Exception ex) { ClearPlan(); ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void InputsChanged()
    {
        if (!initialized || updating) return;
        if (Plan is not null)
        {
            ClearPlan();
            ShowStatus(() => T("Настройки изменены. Сформируйте план заново.", "Settings changed. Build the plan again."));
        }
        RefreshSelectionSummary();
        RefreshCommands();
    }

    private void ClearPlan()
    {
        Plan = null;
        if (initialized) RenderPlan();
    }

    private void RenderPlan()
    {
        if (!initialized) return;
        if (Plan is null)
        {
            PlanSummary.Text = T("План ещё не сформирован", "No plan prepared yet");
            PlanHint.Text = T("Выберите объекты и методы на вкладке настройки. После изменения настроек план нужно сформировать заново.",
                "Select objects and methods in Session setup. Rebuild the plan after changing settings.");
            EligibilityList.ItemsSource = null;
            QueueList.ItemsSource = null;
            QuestionDetails.Text = T("Здесь будут показаны данные выбранного задания.", "Selected question details will appear here.");
            return;
        }
        var plan = Plan;
        var eligibleMaterials = plan.Questions.Select(question => question.MaterialId).Distinct().Count();
        PlanSummary.Text = T($"Заданий: {plan.QuestionCount} · Объектов с заданиями: {eligibleMaterials} из {plan.Configuration.MaterialIds.Count}",
            $"Questions: {plan.QuestionCount} · Objects with questions: {eligibleMaterials} of {plan.Configuration.MaterialIds.Count}");
        var excluded = plan.Eligibility.Count(item => !item.IsEligible);
        PlanHint.Text = T($"Недоступных сочетаний объект–метод: {excluded}. Справочник ответов: объектов — {plan.ObjectAnswers.Count}, элементов — {plan.ElementAnswers.Count}. Язык: русский.",
            $"Unavailable object/method pairs: {excluded}. Answer dictionary: {plan.ObjectAnswers.Count} objects, {plan.ElementAnswers.Count} elements. Language: English.");
        EligibilityList.ItemsSource = plan.Eligibility.Select(item => new EligibilityRow(MaterialName(item.MaterialId), MethodLabel(item.Method), item.QuestionCount,
            item.Issues.Count == 0 ? T("Материал пригоден", "Material is eligible") : string.Join("\n", item.Issues.Select(issue => IssueLabel(item.MaterialId, issue))))).ToArray();
        QueueList.ItemsSource = plan.Questions.Select((question, index) => new QueueRow(index + 1, MethodLabel(question.Method), MaterialName(question.MaterialId), question.Answer.Name, question)).ToArray();
        if (plan.QuestionCount > 0) QueueList.SelectedIndex = 0;
        else QuestionDetails.Text = T("Для выбранных объектов и методов заданий нет. Причины указаны в таблице пригодности.",
            "No questions are available for the selected objects and methods. See the eligibility table for reasons.");
    }

    private string MaterialName(Guid id) => Catalog?.Materials.FirstOrDefault(material => material.Id == id) is { } material
        ? DisplayName(material.Names) : id.ToString();

    private static string DisplayName(IReadOnlyDictionary<string, string> names)
    {
        var localized = names.TryGetValue(UiLanguage.Locale, out var exact) ? exact
            : names.OrderBy(pair => pair.Key, StringComparer.Ordinal).FirstOrDefault(pair => string.Equals(pair.Key, UiLanguage.Locale, StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrWhiteSpace(localized)) return localized;
        var fallback = names.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return fallback is null ? T("Без названия", "Untitled") : fallback + T(" [нет RU]", " [no EN]");
    }

    private string IssueLabel(Guid materialId, TrainingIssue issue)
    {
        var label = issue.Code switch
        {
            TrainingIssueCode.MissingMaterial => T("Объект отсутствует или удалён.", "The object is missing or deleted."),
            TrainingIssueCode.MissingName => T("Нет названия на выбранном языке.", "No name in the selected language."),
            TrainingIssueCode.MissingAnalogName => T("У группы аналогов нет названия на выбранном языке.", "The analog group has no name in the selected language."),
            TrainingIssueCode.AmbiguousName => T("Название совпадает у разных ответов; его нужно уточнить.", "Different answers share this name; clarify the name."),
            TrainingIssueCode.AmbiguousImage => T("Одно и то же изображение связано с разными правильными названиями.", "The same image is linked to different correct names."),
            TrainingIssueCode.NoUsableImage => T("Нет доступного проверенного изображения.", "No verified usable image is available."),
            TrainingIssueCode.NoElements => T("Нет элементов.", "No elements are available."),
            TrainingIssueCode.NoVisibleRegions => T("Нет видимых областей на пригодном изображении.", "No visible regions on a usable image."),
            TrainingIssueCode.RegionOutsideImage => T("Часть эталонной области выходит за изображение; обведение недоступно.", "Part of the reference region is outside the image; tracing is unavailable."),
            TrainingIssueCode.InsufficientImageChoices => T("Недостаточно четырёх различимых вариантов изображений из разных групп ответов.", "Fewer than four distinct image choices from different answer groups are available."),
            TrainingIssueCode.NoDescription => T("Нет учебного описания.", "No learning description is available."),
            TrainingIssueCode.DisabledDescription => T("Учебное описание отключено.", "The learning description is disabled."),
            TrainingIssueCode.MissingDescriptionTranslation => T("Нет перевода учебного описания на выбранный язык.", "The learning description has no translation in the selected language."),
            TrainingIssueCode.AnswerInDescription => T("Учебное описание содержит название правильного ответа.", "The learning description contains the correct answer name."),
            TrainingIssueCode.AmbiguousDescription => T("Одинаковое учебное описание ведёт к разным ответам.", "The same learning description identifies different answers."),
            TrainingIssueCode.DuplicateDescription => T("Повторяющийся вариант описания исключён.", "A duplicate description variant was excluded."),
            _ => T("Материал требует проверки.", "The material needs review.")
        };
        var material = Catalog?.Materials.FirstOrDefault(item => item.Id == materialId);
        if (issue.Code == TrainingIssueCode.InsufficientImageChoices && int.TryParse(issue.Detail, out var choiceCount))
            label += T($" Доступно: {choiceCount}.", $" Available: {choiceCount}.");
        if (issue.Code == TrainingIssueCode.AmbiguousName && !string.IsNullOrWhiteSpace(issue.Detail)) label += " «" + issue.Detail + "».";
        if (issue.Code == TrainingIssueCode.NoUsableImage && material is not null)
        {
            var failures = material.Images.Where(image => !image.IsUsable && !string.IsNullOrWhiteSpace(image.Issue))
                .Select(image => UiLanguage.Message(image.Issue!)).Distinct().ToArray();
            if (failures.Length > 0) label += "\n" + string.Join("\n", failures);
        }
        if (issue.ElementId is { } elementId && material?.Elements.FirstOrDefault(element => element.Id == elementId) is { } element)
            label = DisplayName(element.Names) + ": " + label;
        if (issue.DescriptionId is { } descriptionId)
        {
            var descriptions = issue.ElementId is { } ownerId ? material?.Elements.FirstOrDefault(element => element.Id == ownerId)?.Descriptions : material?.Descriptions;
            var index = descriptions?.ToList().FindIndex(description => description.Id == descriptionId) ?? -1;
            if (index >= 0) label += T($" Вариант {index + 1}.", $" Variant {index + 1}.");
        }
        return label;
    }

    private void Queue_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (QueueList.SelectedItem is not QueueRow row) return;
        var question = row.Question;
        var lines = new List<string> { T("Объект: ", "Object: ") + row.Material, T("Эталон: ", "Expected answer: ") + question.Answer.Name };
        if (!string.IsNullOrWhiteSpace(question.Description)) lines.Add(T("Учебное описание: ", "Learning description: ") + question.Description);
        if (question.MaterialImageId is { } imageId && Catalog?.Materials.FirstOrDefault(material => material.Id == question.MaterialId) is { } material)
            lines.Add(T("Изображение: ", "Image: ") + (material.Images.ToList().FindIndex(image => image.MaterialImageId == imageId) + 1));
        if (question.Regions.Count > 0) lines.Add(T("Эталонных областей: ", "Reference regions: ") + question.Regions.Count);
        if (question.ImageChoices.Count > 0) lines.Add(T("Варианты изображений: ", "Image choices: ") + string.Join("; ", question.ImageChoices.Select(choice => MaterialName(choice.MaterialId))));
        QuestionDetails.Text = string.Join("\n", lines);
    }

    public static string MethodLabel(TrainingMethod method) => method switch
    {
        TrainingMethod.NameObjectImage => T("Определить объект по изображению", "Identify an object from an image"),
        TrainingMethod.ChooseImage => T("Выбрать изображение по названию", "Choose an image by name"),
        TrainingMethod.NameElementRegion => T("Определить элемент по области", "Identify an element from a region"),
        TrainingMethod.TraceElement => T("Обвести элемент", "Outline an element"),
        TrainingMethod.NameObjectDescription => T("Определить объект по описанию", "Identify an object from a description"),
        TrainingMethod.NameElementDescription => T("Определить элемент по описанию", "Identify an element from a description"),
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    private void BuildTagChoices(string query)
    {
        var normalized = CatalogBrowser.Normalize(query);
        var choices = taxonomy.Tags.Select(tag =>
        {
            var group = taxonomy.Groups.FirstOrDefault(group => group.Id == tag.GroupId);
            return new TagChoice(tag.Id, (group is null ? T("Без типа", "Untyped") : CatalogBrowser.Label(group.Names, UiLanguage.Locale)) + ": "
                + CatalogBrowser.Label(tag.Names, UiLanguage.Locale), CatalogBrowser.Normalize(string.Join(" ", tag.Names.Values.Concat(group?.Names.Values ?? []))));
        }).Where(tag => !selectedTags.Contains(tag.Id) && tag.SearchText.Contains(normalized, StringComparison.Ordinal)).OrderBy(tag => tag.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
        updating = true;
        TagSelector.ItemsSource = choices;
        TagSelector.SelectedItem = null;
        TagSelector.Text = query;
        updating = false;
    }

    private void ApplyFilter()
    {
        if (!initialized || updating) return;
        var tokens = CatalogBrowser.Normalize(SearchBox.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var tagSets = taxonomy.Tags.Where(tag => selectedTags.Contains(tag.Id)).GroupBy(tag => tag.GroupId).Select(group => group.Select(tag => tag.Id).ToArray()).ToArray();
        var filtered = materials.Where(row =>
        {
            var analog = Catalog?.AnalogGroups.FirstOrDefault(group => group.Id == row.Material.AnalogGroupId);
            var searchText = CatalogBrowser.Normalize(row.Material.Id + " " + string.Join(" ", row.Material.Names.Values.Concat(analog?.Names.Values ?? [])));
            return tokens.All(token => searchText.Contains(token, StringComparison.Ordinal)) && tagSets.All(set => set.Any(row.Material.TagIds.Contains));
        }).OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        MaterialList.ItemsSource = filtered;
        EmptyMaterials.Text = materials.Count == 0 ? T("В базе пока нет объектов.", "There are no objects in this catalog yet.")
            : T("Нет объектов, соответствующих фильтрам.", "No objects match the filters.");
        EmptyMaterials.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedTags.Children.Clear();
        foreach (var tag in taxonomy.Tags.Where(tag => selectedTags.Contains(tag.Id)))
        {
            var button = new Button { Content = CatalogBrowser.Label(tag.Names, UiLanguage.Locale) + " ×", Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 5, 5),
                ToolTip = T("Убрать фильтр. Внутри типа подходит любой тег; между типами нужны все.", "Remove filter. Any tag within a type; all selected types must match.") };
            button.Click += (_, _) => { selectedTags.Remove(tag.Id); InputsChanged(); BuildTagChoices(""); ApplyFilter(); };
            SelectedTags.Children.Add(button);
        }
        RefreshSelectionSummary();
    }

    private void RefreshSelectionSummary()
    {
        if (!initialized) return;
        var visible = MaterialList.Items.Cast<MaterialRow>().ToArray();
        var count = materials.Count(row => row.IsSelected);
        var hidden = count - visible.Count(row => row.IsSelected);
        SelectionSummary.Text = T($"Показано: {visible.Length} из {materials.Count} · Выбрано: {count} · Скрыто фильтрами из выбранного: {hidden}",
            $"Visible: {visible.Length} of {materials.Count} · Selected: {count} · Selected but hidden by filters: {hidden}");
    }

    private void SetBusy(bool value)
    {
        busy = value;
        SetupPanel.IsEnabled = LanguageButton.IsEnabled = RefreshButton.IsEnabled = CloseButton.IsEnabled = !value;
        RefreshCommands();
    }
    private void RefreshCommands()
    {
        if (!initialized) return;
        BuildPlanButton.IsEnabled = !busy && Catalog is not null && materials.Any(row => row.IsSelected) && methods.Any(row => row.IsSelected);
    }
    private void ShowStatus(Func<string> value) { statusFactory = value; OperationStatus.Text = value(); }
    private void ShowError(Exception ex)
    {
        ErrorLog.Write(ex);
        ShowStatus(() => T("Подготовку не удалось завершить. ", "Preparation could not be completed. ") + UiLanguage.Error(ex));
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (initialized && !updating) { InputsChanged(); ApplyFilter(); } }
    private void Order_Changed(object sender, SelectionChangedEventArgs e) => InputsChanged();
    private void Tag_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updating || TagSelector.SelectedItem is not TagChoice tag) return;
        selectedTags.Add(tag.Id); InputsChanged(); BuildTagChoices(""); ApplyFilter();
    }
    private void TagText_Changed(object sender, TextChangedEventArgs e)
    {
        if (updating || !TagSelector.IsKeyboardFocusWithin) return;
        BuildTagChoices(TagSelector.Text); TagSelector.IsDropDownOpen = true;
    }
    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        updating = true;
        foreach (var row in MaterialList.Items.Cast<MaterialRow>()) row.IsSelected = true;
        updating = false;
        InputsChanged();
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => SelectMaterials([]);
    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        selectedTags.Clear(); SearchBox.Text = ""; InputsChanged(); BuildTagChoices(""); ApplyFilter();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadCatalogAsync();
    private async void BuildPlan_Click(object sender, RoutedEventArgs e) => await BuildPlanAsync();
    private void Language_Click(object sender, RoutedEventArgs e)
    {
        try { UiLanguage.Set(UiLanguage.Locale == "ru" ? "en" : "ru"); }
        catch (Exception ex) { ShowError(ex); }
    }

    private abstract class SelectableRow(Action changed) : INotifyPropertyChanged
    {
        private bool selected;
        public bool IsSelected { get => selected; set { if (selected == value) return; selected = value; OnChanged(); changed(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }
    private sealed class MethodRow(TrainingMethod method, Action changed) : SelectableRow(changed)
    {
        public TrainingMethod Method { get; } = method;
        public string Label => MethodLabel(Method);
        public void RefreshLanguage() => OnChanged(nameof(Label));
    }
    private sealed class MaterialRow(TrainingMaterial material, Action changed) : SelectableRow(changed)
    {
        public TrainingMaterial Material { get; } = material;
        public string Name => DisplayName(Material.Names);
        public string Detail => (Material.Status == "ready" ? T("Готов", "Ready") : T("Черновик", "Draft"))
            + T($" · Изображений пригодно: {Material.Images.Count(image => image.IsUsable)} из {Material.Images.Count} · Элементов: {Material.Elements.Count}",
                $" · Usable images: {Material.Images.Count(image => image.IsUsable)} of {Material.Images.Count} · Elements: {Material.Elements.Count}");
        public void RefreshLanguage() { OnChanged(nameof(Name)); OnChanged(nameof(Detail)); }
    }
}

using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HistOSets.Services;
using HistOSets.Storage;

namespace HistOSets;

/// <summary>Edits an isolated list. Only Apply returns descriptions to the parent working copy.</summary>
public partial class TrainingPromptWindow : Window
{
    private List<TrainingPromptDraft> prompts;
    private readonly string initialFingerprint;
    private readonly string ownerLabel;
    private readonly bool isElement;
    private readonly List<List<TrainingPromptDraft>> undo = [];
    private readonly List<List<TrainingPromptDraft>> redo = [];
    private Guid? selectedId;
    private bool initialized;
    private bool rendering;
    private bool accepted;
    private const int HistoryLimit = 80;
    private sealed record PromptChoice(Guid Id, string Label);
    private static string T(string ru, string en) => UiLanguage.T(ru, en);

    public IReadOnlyList<TrainingPromptDraft> Prompts { get; private set; } = [];

    public TrainingPromptWindow(IEnumerable<TrainingPromptDraft> source, string ownerLabel, bool isElement = false)
    {
        this.ownerLabel = ownerLabel;
        this.isElement = isElement;
        prompts = Clone(source);
        initialFingerprint = Fingerprint(prompts);
        selectedId = prompts.FirstOrDefault()?.Id;
        InitializeComponent();
        initialized = true;
        UiLanguage.Changed += Language_Changed;
        Closed += (_, _) => UiLanguage.Changed -= Language_Changed;
        Localize();
        Render();
    }

    private static List<TrainingPromptDraft> Clone(IEnumerable<TrainingPromptDraft> values) => values.Select(p => p.Clone()).ToList();
    private static string Fingerprint(IEnumerable<TrainingPromptDraft> values) => JsonSerializer.Serialize(values);
    private TrainingPromptDraft? Selected => prompts.FirstOrDefault(p => p.Id == selectedId);

    private void ReadFields(List<TrainingPromptDraft> target)
    {
        if (target.FirstOrDefault(p => p.Id == selectedId) is not { } prompt) return;
        WriteLocale(prompt, "ru", PromptRuText.Text);
        WriteLocale(prompt, "en", PromptEnText.Text);
        prompt.IsEnabled = PromptEnabled.IsChecked == true;
    }

    private static void WriteLocale(TrainingPromptDraft prompt, string locale, string value)
    {
        // Preserve all unseen locales and do not invent absent empty translations.
        if (value.Length > 0 || prompt.Texts.ContainsKey(locale)) prompt.Texts[locale] = value;
    }

    private void FlushFields()
    {
        if (!initialized || rendering) return;
        var before = Clone(prompts);
        ReadFields(prompts);
        Record(before);
    }

    private void Record(List<TrainingPromptDraft> before)
    {
        if (Fingerprint(before) == Fingerprint(prompts)) return;
        undo.Add(before);
        if (undo.Count > HistoryLimit) undo.RemoveAt(0);
        redo.Clear();
        UpdateState();
    }

    private void Change(Action action)
    {
        FlushFields();
        var before = Clone(prompts);
        action();
        Record(before);
        Render();
    }

    private void Render()
    {
        rendering = true;
        try
        {
            if (!prompts.Any(p => p.Id == selectedId)) selectedId = prompts.FirstOrDefault()?.Id;
            TrainingPromptList.ItemsSource = prompts.Select((p, i) => new PromptChoice(p.Id, VariantLabel(p, i))).ToArray();
            TrainingPromptList.SelectedItem = TrainingPromptList.Items.OfType<PromptChoice>().FirstOrDefault(p => p.Id == selectedId);
            var prompt = Selected;
            PromptRuText.Text = prompt?.Texts.GetValueOrDefault("ru") ?? "";
            PromptEnText.Text = prompt?.Texts.GetValueOrDefault("en") ?? "";
            PromptEnabled.IsChecked = prompt?.IsEnabled == true;
            PromptFields.IsEnabled = prompt is not null;
            EmptyPromptHint.Visibility = prompt is null ? Visibility.Visible : Visibility.Collapsed;
            var extra = prompt?.Texts.Keys.Where(k => k is not ("ru" or "en")).Order().ToArray() ?? [];
            OtherLocalesNotice.Text = extra.Length == 0 ? "" : T("Другие языковые варианты сохраняются без изменений: ", "Other language variants are preserved unchanged: ") + string.Join(", ", extra);
        }
        finally { rendering = false; }
        UpdateState();
    }

    private static string VariantLabel(TrainingPromptDraft prompt, int index)
    {
        var text = prompt.Texts.GetValueOrDefault(UiLanguage.Locale) ?? "";
        var preview = string.IsNullOrWhiteSpace(text) ? T("Нет текста на этом языке", "No text in this language")
            : text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (preview.Length > 85) preview = preview[..85] + "…";
        return T($"Вариант {index + 1}", $"Variant {index + 1}")
            + (prompt.IsEnabled ? "" : T(" · выключен", " · disabled")) + "\n" + preview;
    }

    private void UpdateState()
    {
        if (!initialized || rendering) return;
        var pending = Clone(prompts);
        ReadFields(pending);
        var dirty = Fingerprint(pending) != initialFingerprint;
        Title = T("Учебные описания — HISTOSETS", "Training descriptions — HISTOSETS") + (dirty ? " *" : "");
        var index = prompts.FindIndex(p => p.Id == selectedId);
        RemovePromptButton.IsEnabled = index >= 0;
        MovePromptUpButton.IsEnabled = index > 0;
        MovePromptDownButton.IsEnabled = index >= 0 && index + 1 < prompts.Count;
        UndoPromptButton.IsEnabled = undo.Count > 0 || Fingerprint(pending) != Fingerprint(prompts);
        RedoPromptButton.IsEnabled = redo.Count > 0;
        var incomplete = pending.Count(p => p.IsEnabled && (string.IsNullOrWhiteSpace(p.Texts.GetValueOrDefault("ru")) || string.IsNullOrWhiteSpace(p.Texts.GetValueOrDefault("en"))));
        PromptStatus.Text = T("Применение изменяет рабочую копию. Для записи в базу сохраните карточку объекта.",
            "Apply updates the working copy. Save the object card to store descriptions in the catalog.")
            + (incomplete == 0 ? "" : T($" Вариантов без текста RU или EN: {incomplete}. На языке без текста такой вариант недоступен.",
                $" Variants missing RU or EN text: {incomplete}. A variant is unavailable in a language without text."));
    }

    private void Prompt_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || rendering) return;
        var next = (TrainingPromptList.SelectedItem as PromptChoice)?.Id;
        FlushFields();
        selectedId = next;
        Render();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Change(() => { var prompt = new TrainingPromptDraft(); prompts.Add(prompt); selectedId = prompt.Id; });
        PromptRuText.Focus();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } prompt) return;
        Change(() =>
        {
            var index = prompts.IndexOf(prompt);
            prompts.Remove(prompt);
            selectedId = prompts.Count == 0 ? null : prompts[Math.Min(index, prompts.Count - 1)].Id;
        });
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int offset)
    {
        var index = prompts.FindIndex(p => p.Id == selectedId);
        if (index < 0 || index + offset < 0 || index + offset >= prompts.Count) return;
        Change(() => (prompts[index], prompts[index + offset]) = (prompts[index + offset], prompts[index]));
    }

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || rendering) return;
        FlushFields();
        Render();
    }

    private void Prompt_TextChanged(object sender, TextChangedEventArgs e) { if (initialized && !rendering) UpdateState(); }
    private void Prompt_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => FlushFields();

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        FlushFields();
        if (undo.Count == 0) return;
        redo.Add(Clone(prompts));
        prompts = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        Render();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        FlushFields();
        if (redo.Count == 0) return;
        undo.Add(Clone(prompts));
        prompts = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        Render();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        FlushFields();
        Prompts = Clone(prompts);
        accepted = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!initialized || accepted) return;
        FlushFields();
        if (Fingerprint(prompts) == initialFingerprint) return;
        var prompt = new TrainingPromptDiscardPrompt { Owner = this };
        prompt.ShowDialog();
        e.Cancel = !prompt.Discard;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.Z) { e.Handled = true; Undo_Click(sender, e); }
        else if (e.Key == Key.Y) { e.Handled = true; Redo_Click(sender, e); }
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        try { UiLanguage.Set(UiLanguage.Locale == "ru" ? "en" : "ru"); }
        catch (Exception ex) { ErrorLog.Write(ex); PromptStatus.Text = UiLanguage.Error(ex); }
    }

    private void Language_Changed(object? sender, EventArgs e) { FlushFields(); Localize(); Render(); }

    private void Localize()
    {
        var label = string.IsNullOrWhiteSpace(ownerLabel) ? T("без названия", "untitled") : ownerLabel;
        OwnerHeading.Text = isElement ? T($"Учебные описания элемента: {label}", $"Element training descriptions: {label}")
            : T($"Учебные описания объекта: {label}", $"Object training descriptions: {label}");
        TrainingHelp.Text = T("Создайте один или несколько вариантов задания «Узнать по описанию». Каждый вариант — отдельное краткое описание без названия и готового ответа. Обычные описания карточки сюда не копируются. Эти поля необязательны для готовности карточки.",
            "Create one or more variants for “Identify from description”. Each variant is a separate short description without the name or the answer. Ordinary card descriptions are not copied here. These fields are optional for card readiness.");
        VariantsHeading.Text = T("Варианты задания", "Prompt variants");
        EmptyPromptHint.Text = T("Добавьте вариант, чтобы ввести учебное описание на RU и EN.", "Add a variant to enter a training description in RU and EN.");
        EnabledLabel.Text = T("Использовать этот вариант в самопроверке", "Use this variant in self-check");
        PromptRuLabel.Text = T("Учебное описание на русском", "Training description in Russian");
        PromptEnLabel.Text = T("Учебное описание на английском", "Training description in English");
        System.Windows.Automation.AutomationProperties.SetName(PromptRuText, PromptRuLabel.Text);
        System.Windows.Automation.AutomationProperties.SetName(PromptEnText, PromptEnLabel.Text);
        System.Windows.Automation.AutomationProperties.SetName(TrainingPromptList, VariantsHeading.Text);
        AddPromptButton.Content = T("Добавить", "Add"); RemovePromptButton.Content = T("Удалить", "Remove");
        MovePromptUpButton.Content = T("↑ Выше", "↑ Move up"); MovePromptDownButton.Content = T("↓ Ниже", "↓ Move down");
        UndoPromptButton.Content = T("Отменить", "Undo"); RedoPromptButton.Content = T("Повторить", "Redo");
        UndoPromptButton.ToolTip = "Ctrl+Z"; RedoPromptButton.ToolTip = "Ctrl+Y";
        ApplyPromptsButton.Content = T("Применить", "Apply"); CancelPromptsButton.Content = T("Отмена", "Cancel");
    }
}

internal sealed class TrainingPromptDiscardPrompt : Window
{
    public bool Discard { get; private set; }
    public TrainingPromptDiscardPrompt()
    {
        NameScope.SetNameScope(this, new NameScope());
        Title = UiLanguage.T("Не применённые учебные описания", "Unapplied training descriptions");
        Width = 550; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Закрыть окно и отменить изменения учебных описаний? Чтобы сохранить их в рабочей копии, вернитесь к редактированию и нажмите «Применить».",
                "Close this window and discard training-description edits? To keep them in the working copy, continue editing and choose Apply."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18)
        });
        var actions = new WrapPanel();
        var discard = new Button { Name = "DiscardPromptEditsButton", Content = UiLanguage.T("Не применять", "Discard edits") };
        var keepEditing = new Button { Name = "ContinuePromptEditingButton", Content = UiLanguage.T("Продолжить редактирование", "Continue editing"), IsCancel = true, IsDefault = true };
        discard.Click += (_, _) => { Discard = true; DialogResult = true; };
        keepEditing.Click += (_, _) => DialogResult = false;
        RegisterName(discard.Name, discard); RegisterName(keepEditing.Name, keepEditing);
        actions.Children.Add(discard); actions.Children.Add(keepEditing);
        panel.Children.Add(actions); Content = panel;
    }
}

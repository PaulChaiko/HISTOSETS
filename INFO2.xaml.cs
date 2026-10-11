using System.Windows;
using HistOSets.Services;

namespace HistOSets;

public partial class INFO2 : Window
{
    private readonly Func<(string Title, string Description)>? contentResolver;

    public INFO2(string title, string description, Func<(string Title, string Description)>? contentResolver = null)
    {
        this.contentResolver = contentResolver;
        InitializeComponent();
        Title = title;
        Page.Text = description;
        ApplyLanguage();
        RefreshFontControls();
        UiLanguage.Changed += LanguageChanged;
    }

    private void LanguageChanged(object? sender, EventArgs e) => ApplyLanguage();

    private void ApplyLanguage()
    {
        FontSizeLabel.Text = UiLanguage.T("Размер текста", "Text size");
        SmallerTextButton.ToolTip = UiLanguage.T("Уменьшить текст", "Decrease text size");
        LargerTextButton.ToolTip = UiLanguage.T("Увеличить текст", "Increase text size");
        if (contentResolver is not null)
        {
            var content = contentResolver();
            Title = content.Title;
            Page.Text = content.Description;
        }
    }

    private void SmallerText_Click(object sender, RoutedEventArgs e) => ChangeFontSize(-2);
    private void LargerText_Click(object sender, RoutedEventArgs e) => ChangeFontSize(2);

    private void ChangeFontSize(double change)
    {
        Page.FontSize = Math.Clamp(Page.FontSize + change, 12, 32);
        RefreshFontControls();
    }

    private void RefreshFontControls()
    {
        FontSizeValue.Text = Page.FontSize.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        SmallerTextButton.IsEnabled = Page.FontSize > 12;
        LargerTextButton.IsEnabled = Page.FontSize < 32;
    }

    protected override void OnClosed(EventArgs e)
    {
        UiLanguage.Changed -= LanguageChanged;
        base.OnClosed(e);
    }
}

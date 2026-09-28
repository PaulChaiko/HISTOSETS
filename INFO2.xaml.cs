using System.Windows;

namespace HistOSets;

public partial class INFO2 : Window
{
    public INFO2(string title, string description)
    {
        InitializeComponent();
        Title = title;
        Page.Text = description;
    }
}

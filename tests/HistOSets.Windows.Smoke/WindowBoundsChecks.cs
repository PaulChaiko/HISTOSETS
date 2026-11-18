using System.Windows;
using System.Windows.Controls;
using HistOSets.Controls;

internal static partial class Program
{
    private static void RunWindowBoundsChecks()
    {
        // Anchor the fixture on the primary monitor, whose work area WPF exposes.
        // Subwindows exercise the same owner monitor policy as normal dialogs.
        var area = SystemParameters.WorkArea;
        var window = new WorkAreaWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = area.Left,
            Top = area.Top,
            Width = area.Width + 160,
            MinWidth = area.Width + 100,
            Height = area.Height + 160,
            MinHeight = area.Height + 100,
            ShowInTaskbar = false,
            Content = new Grid()
        };
        try
        {
            window.Show();
            Pump();
            Check(InsideWorkArea(window), "An oversized application window fits the usable monitor area even if its preferred minimum is larger");
            var form = new StackPanel();
            form.Children.Add(new Border { Height = area.Height + 200 });
            form.Children.Add(new Button { Content = "OK" });
            var dialog = new WorkAreaWindow
            {
                Owner = window,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.Height,
                Width = 500,
                ShowInTaskbar = false,
                Content = form
            };
            try
            {
                dialog.Show();
                Pump();
                Check(InsideWorkArea(dialog) && dialog.Content is ScrollViewer { ScrollableHeight: > 0 },
                    "An auto-sized form on the owner's monitor fits and can scroll to the final controls");
            }
            finally { dialog.Close(); }
        }
        finally { window.Close(); }

        bool InsideWorkArea(Window candidate) => candidate.ActualWidth > 0 && candidate.ActualHeight > 0
            && candidate.ActualWidth < area.Width && candidate.ActualHeight < area.Height
            && candidate.Left >= area.Left - 1 && candidate.Top >= area.Top - 1
            && candidate.Left + candidate.ActualWidth <= area.Right + 1
            && candidate.Top + candidate.ActualHeight <= area.Bottom + 1;
    }
}

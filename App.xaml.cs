using System.Windows;
using System.Windows.Threading;
using HistOSets.Services;

namespace HistOSets;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        try
        {
            MainWindow ??= new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            ReportFatalError(ex);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportFatalError(e.Exception);
    }

    private void ReportFatalError(Exception exception)
    {
        var log = ErrorLog.Write(exception);
        MessageBox.Show("Не удалось продолжить работу HISTOSETS.\n\n" + exception.Message
            + (log is null ? "" : "\n\nПодробности: " + log), "HISTOSETS", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }
}

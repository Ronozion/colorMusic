using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TrafficLight;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception);
        e.Handled = true; // не кладём процесс, показываем ошибку
        Shutdown();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Report(e.ExceptionObject as Exception);
    }

    private static void Report(Exception? ex)
    {
        var text = ex?.ToString() ?? "Unknown error";
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch { /* лог не записался — не критично */ }

        MessageBox.Show(
            $"В приложении произошла ошибка:\n\n{ex?.Message}\n\nПодробности: error.log рядом с exe",
            "Частотный светофор",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TrafficLight;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Язык интерфейса: RCU системы → «ru» берёт Lang.ru.xaml (смонтирован в App.xaml),
        // любой другой язык — Lang.en.xaml. Ручной override: TL_LANG=ru|en.
        string? lang = Environment.GetEnvironmentVariable("TL_LANG");
        if (string.IsNullOrWhiteSpace(lang))
            lang = string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ru",
                                 StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
        if (!lang.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
            Resources.MergedDictionaries[0] = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Lang.en.xaml", UriKind.Absolute)
            };

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    /// <summary>Локализованная строка из словаря языка (или сам ключ, если строки нет).</summary>
    public static string T(string key) =>
        Current?.TryFindResource(key) as string ?? key;

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
            string.Format(T("L.ErrTextFmt"), ex?.Message),
            T("L.ErrTitle"),
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.Threading.Tasks;
using ReactiveUI;

namespace MailVault.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 1. AppDomain Unhandled Exception Handler
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            HandleGlobalException(e.ExceptionObject as Exception);
        };

        // 2. TaskScheduler Unobserved Task Exception Handler
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            HandleGlobalException(e.Exception);
            e.SetObserved();
        };

        // 3. ReactiveUI Global Exception Handler
        RxApp.DefaultExceptionHandler = System.Reactive.Observer.Create<Exception>(ex =>
        {
            HandleGlobalException(ex);
        });

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void HandleGlobalException(Exception? ex)
    {
        if (ex == null) return;
        System.Diagnostics.Debug.WriteLine($"[GLOBAL TECHNICAL ERROR] {ex}");

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var mainVm = App.MainViewModel;
                if (mainVm != null)
                {
                    mainVm.ShowGlobalError(ex);
                }
            }
            catch
            {
                // Safety net to prevent crash loops
            }
        });
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}

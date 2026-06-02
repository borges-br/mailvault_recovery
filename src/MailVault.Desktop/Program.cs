using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.IO;
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

        // 4. Startup guard — como o app é WinExe (sem console), uma exceção lançada
        //    durante a inicialização da Avalonia não tem para onde ser exibida e o
        //    processo simplesmente morre. Persistimos o erro em crash.log para que
        //    falhas no executável publicado sejam diagnosticáveis.
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex, fatal: true);
            throw;
        }
    }

    private static void HandleGlobalException(Exception? ex)
    {
        if (ex == null) return;
        System.Diagnostics.Debug.WriteLine($"[GLOBAL TECHNICAL ERROR] {ex}");
        WriteCrashLog(ex, fatal: false);

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

    /// <summary>
    /// Grava o erro em %APPDATA%\MailVault\crash.log de forma resiliente.
    /// Nunca lança — é a última linha de defesa de diagnóstico.
    /// </summary>
    private static void WriteCrashLog(Exception? ex, bool fatal)
    {
        if (ex == null) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MailVault");
            Directory.CreateDirectory(dir);
            var logPath = Path.Combine(dir, "crash.log");

            var entry =
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz} | {(fatal ? "FATAL (startup)" : "UNHANDLED")} ===={Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{Environment.NewLine}";

            File.AppendAllText(logPath, entry);
        }
        catch
        {
            // Se nem o log em disco funcionar, não há mais o que fazer.
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}

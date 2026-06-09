using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MailVault.Desktop.Services;
using MailVault.Desktop.ViewModels;
using MailVault.Desktop.Views;

namespace MailVault.Desktop;

public partial class App : Application
{
    public static MainWindowViewModel? MainViewModel { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Aplica o tema salvo (Dark/Light) antes de montar a janela.
        try { ThemeService.Apply(new LocalSettingsService().Load().DarkTheme); }
        catch { /* mantém o default do App.axaml se as settings falharem */ }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            var mainViewModel = new MainWindowViewModel();
            MainViewModel = mainViewModel;
            mainWindow.DataContext = mainViewModel;
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}

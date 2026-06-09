using Avalonia;
using Avalonia.Styling;

namespace MailVault.Desktop.Services;

/// <summary>
/// Aplica o tema (Dark/Light) na Application. Como os tokens vivem em
/// ThemeDictionaries e as Views consomem por DynamicResource, trocar o
/// RequestedThemeVariant re-tematiza tudo ao vivo, sem reiniciar.
/// </summary>
public static class ThemeService
{
    public static void Apply(bool dark)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }
}

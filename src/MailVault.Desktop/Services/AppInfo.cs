using System.Reflection;

namespace MailVault.Desktop.Services;

/// <summary>
/// Fonte única da versão do app em runtime. Lê do assembly (que herda a versão de
/// Directory.Build.props na raiz). Para trocar a versão exibida, basta bumpar lá —
/// nunca hardcode em XAML/VM.
/// </summary>
public static class AppInfo
{
    /// <summary>Versão semântica limpa, ex.: "1.1.0".</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>Versão com prefixo para exibição, ex.: "v1.1.0".</summary>
    public static string DisplayVersion => $"v{Version}";

    private static string ResolveVersion()
    {
        var asm = typeof(AppInfo).Assembly;

        // InformationalVersion preserva o semver definido em Directory.Build.props.
        // Pode vir com sufixo "+<git-hash>" (SourceLink) — removido aqui.
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        return asm.GetName().Version?.ToString(3) ?? "1.1.0";
    }
}

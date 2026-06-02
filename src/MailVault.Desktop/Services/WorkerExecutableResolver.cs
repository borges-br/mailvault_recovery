using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MailVault.Desktop.Services;

public enum WorkerLaunchMode
{
    Exe,
    DotnetDll,
    DotnetRunProject
}

public sealed record WorkerLaunchInfo(
    string FileName,
    string? ArgumentsPrefix,
    string WorkingDirectory,
    WorkerLaunchMode LaunchMode,
    string DiagnosticDescription,
    IReadOnlyList<string> ProbedPaths,
    bool IsPublishedLayout,
    bool IsDevelopmentLayout
);

public sealed class WorkerLaunchResolutionException : Exception
{
    public IReadOnlyList<string> ProbedPaths { get; }
    public string AppBaseDirectory { get; }
    public string CurrentDirectory { get; }
    public string RemediationDetails { get; }

    public WorkerLaunchResolutionException(
        string message,
        IReadOnlyList<string> probedPaths,
        string appBaseDirectory,
        string currentDirectory,
        string remediationDetails) 
        : base(message)
    {
        ProbedPaths = probedPaths;
        AppBaseDirectory = appBaseDirectory;
        CurrentDirectory = currentDirectory;
        RemediationDetails = remediationDetails;
    }
}

public interface IWorkerExecutableResolver
{
    WorkerLaunchInfo Resolve();
}

public sealed class WorkerExecutableResolver : IWorkerExecutableResolver
{
    private static readonly string RemediationMessage =
        "Remediação Sugerida:\n" +
        "1. Certifique-se de compilar o CLI executando: dotnet build src/MailVault.Cli/MailVault.Cli.csproj\n" +
        "2. Se estiver publicando a aplicação, execute o script de empacotamento: .\\scripts\\publish-windows.ps1\n" +
        "3. Como alternativa de desenvolvimento, defina a variável de ambiente: MAILVAULT_CLI_PATH com o caminho completo do executável/dll.";

    // O CLI é publicado/compilado como "mailvault.exe"/"mailvault.dll" (AssemblyName=mailvault
    // em MailVault.Cli.csproj). "MailVault.Cli.*" é mantido como fallback de compatibilidade.
    private static readonly string[] CliExeNames = { "mailvault.exe", "MailVault.Cli.exe" };
    private static readonly string[] CliDllNames = { "mailvault.dll", "MailVault.Cli.dll" };

    public bool DisableDevelopmentFallback { get; set; }

    public WorkerLaunchInfo Resolve()
    {
        var probedPaths = new List<string>();
        var diagnosticLogs = new List<string>();

        // A. Environment Variable Override
        string? envPath = Environment.GetEnvironmentVariable("MAILVAULT_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            string fullEnvPath = Path.GetFullPath(envPath);
            probedPaths.Add($"[Environment Variable] {fullEnvPath}");
            if (File.Exists(fullEnvPath) && IsPathAllowed(fullEnvPath))
            {
                string ext = Path.GetExtension(fullEnvPath).ToLowerInvariant();
                string workDir = Path.GetDirectoryName(fullEnvPath) ?? AppContext.BaseDirectory;
                if (ext == ".exe")
                {
                    return new WorkerLaunchInfo(
                        FileName: fullEnvPath,
                        ArgumentsPrefix: null,
                        WorkingDirectory: workDir,
                        LaunchMode: WorkerLaunchMode.Exe,
                        DiagnosticDescription: $"Resolvido via variável de ambiente MAILVAULT_CLI_PATH (EXE): '{fullEnvPath}'",
                        ProbedPaths: probedPaths,
                        IsPublishedLayout: false,
                        IsDevelopmentLayout: false
                    );
                }
                else if (ext == ".dll")
                {
                    return new WorkerLaunchInfo(
                        FileName: "dotnet",
                        ArgumentsPrefix: $"exec \"{fullEnvPath}\"",
                        WorkingDirectory: workDir,
                        LaunchMode: WorkerLaunchMode.DotnetDll,
                        DiagnosticDescription: $"Resolvido via variável de ambiente MAILVAULT_CLI_PATH (DLL): '{fullEnvPath}'",
                        ProbedPaths: probedPaths,
                        IsPublishedLayout: false,
                        IsDevelopmentLayout: false
                    );
                }
                else
                {
                    diagnosticLogs.Add($"MAILVAULT_CLI_PATH aponta para arquivo com extensão desconhecida: '{fullEnvPath}'.");
                }
            }
            else
            {
                diagnosticLogs.Add($"MAILVAULT_CLI_PATH está definida mas o arquivo não existe ou é inválido: '{fullEnvPath}'.");
            }
        }

        // B. Published/End-User Layout
        string baseDir = AppContext.BaseDirectory;

        foreach (var exeName in CliExeNames)
        {
            string pubExe = Path.GetFullPath(Path.Combine(baseDir, exeName));
            probedPaths.Add($"[Published Layout EXE] {pubExe}");
            if (File.Exists(pubExe) && IsPathAllowed(pubExe))
            {
                return new WorkerLaunchInfo(
                    FileName: pubExe,
                    ArgumentsPrefix: null,
                    WorkingDirectory: baseDir,
                    LaunchMode: WorkerLaunchMode.Exe,
                    DiagnosticDescription: $"Layout de usuário publicado (EXE) detectado em: '{pubExe}'",
                    ProbedPaths: probedPaths,
                    IsPublishedLayout: true,
                    IsDevelopmentLayout: false
                );
            }
        }

        foreach (var dllName in CliDllNames)
        {
            string pubDll = Path.GetFullPath(Path.Combine(baseDir, dllName));
            probedPaths.Add($"[Published Layout DLL] {pubDll}");
            if (File.Exists(pubDll) && IsPathAllowed(pubDll))
            {
                return new WorkerLaunchInfo(
                    FileName: "dotnet",
                    ArgumentsPrefix: $"exec \"{pubDll}\"",
                    WorkingDirectory: baseDir,
                    LaunchMode: WorkerLaunchMode.DotnetDll,
                    DiagnosticDescription: $"Layout de usuário publicado (DLL) detectado em: '{pubDll}'",
                    ProbedPaths: probedPaths,
                    IsPublishedLayout: true,
                    IsDevelopmentLayout: false
                );
            }
        }

        // C. Development Repository Layout
        string? repoRoot = DisableDevelopmentFallback ? null : (FindRepositoryRoot(AppContext.BaseDirectory) ?? FindRepositoryRoot(Environment.CurrentDirectory));
        if (repoRoot != null)
        {
            diagnosticLogs.Add($"Raiz do repositório identificada em: '{repoRoot}'.");

            string[] configs = { "Debug", "Release" };
            foreach (var config in configs)
            {
                string binDir = Path.Combine(repoRoot, "src", "MailVault.Cli", "bin", config, "net10.0");

                foreach (var exeName in CliExeNames)
                {
                    string devExe = Path.GetFullPath(Path.Combine(binDir, exeName));
                    probedPaths.Add($"[Dev Layout EXE ({config})] {devExe}");
                    if (File.Exists(devExe) && IsPathAllowed(devExe))
                    {
                        return new WorkerLaunchInfo(
                            FileName: devExe,
                            ArgumentsPrefix: null,
                            WorkingDirectory: Path.GetDirectoryName(devExe)!,
                            LaunchMode: WorkerLaunchMode.Exe,
                            DiagnosticDescription: $"Modo de desenvolvimento estruturado (EXE - {config}) detectado em: '{devExe}'",
                            ProbedPaths: probedPaths,
                            IsPublishedLayout: false,
                            IsDevelopmentLayout: true
                        );
                    }
                }

                foreach (var dllName in CliDllNames)
                {
                    string devDll = Path.GetFullPath(Path.Combine(binDir, dllName));
                    probedPaths.Add($"[Dev Layout DLL ({config})] {devDll}");
                    if (File.Exists(devDll) && IsPathAllowed(devDll))
                    {
                        return new WorkerLaunchInfo(
                            FileName: "dotnet",
                            ArgumentsPrefix: $"exec \"{devDll}\"",
                            WorkingDirectory: Path.GetDirectoryName(devDll)!,
                            LaunchMode: WorkerLaunchMode.DotnetDll,
                            DiagnosticDescription: $"Modo de desenvolvimento estruturado (DLL - {config}) detectado em: '{devDll}'",
                            ProbedPaths: probedPaths,
                            IsPublishedLayout: false,
                            IsDevelopmentLayout: true
                        );
                    }
                }
            }

            // D. Dotnet Run Project Fallback (strictly development only, verified by repository presence)
            string csprojPath = Path.GetFullPath(Path.Combine(repoRoot, "src", "MailVault.Cli", "MailVault.Cli.csproj"));
            probedPaths.Add($"[Dev Dotnet Run fallback] {csprojPath}");
            if (File.Exists(csprojPath))
            {
                return new WorkerLaunchInfo(
                    FileName: "dotnet",
                    ArgumentsPrefix: $"run --project \"{csprojPath}\" --",
                    WorkingDirectory: repoRoot,
                    LaunchMode: WorkerLaunchMode.DotnetRunProject,
                    DiagnosticDescription: $"Fallback de desenvolvimento (dotnet run) utilizando: '{csprojPath}'",
                    ProbedPaths: probedPaths,
                    IsPublishedLayout: false,
                    IsDevelopmentLayout: true
                );
            }
        }
        else
        {
            diagnosticLogs.Add("A raiz do repositório não pôde ser identificada pesquisando recursivamente a partir dos caminhos locais.");
        }

        // E. Throw helpful resolution error
        string message = "Não foi possível localizar o executável do worker CLI (mailvault.exe ou mailvault.dll) em nenhum dos layouts suportados.\n" +
                         string.Join("\n", diagnosticLogs);
        
        throw new WorkerLaunchResolutionException(
            message,
            probedPaths,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            RemediationMessage
        );
    }

    private static string? FindRepositoryRoot(string startingDirectory)
    {
        if (string.IsNullOrWhiteSpace(startingDirectory) || !Directory.Exists(startingDirectory))
        {
            return null;
        }

        string? current = Path.GetFullPath(startingDirectory);
        while (current != null)
        {
            bool slnExists = File.Exists(Path.Combine(current, "MailVault.sln")) || 
                             File.Exists(Path.Combine(current, "mailvault_recovery.sln"));

            bool csprojExists = File.Exists(Path.Combine(current, "src", "MailVault.Cli", "MailVault.Cli.csproj"));

            if (slnExists || csprojExists)
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }

    private static bool IsPathAllowed(string path)
    {
        if (Environment.GetEnvironmentVariable("MAILVAULT_ALLOW_TESTS_PATH") == "true")
        {
            return true;
        }
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        return !normalized.Contains("/tests/") && 
               !normalized.Contains("/testresults/") && 
               !normalized.Contains("/scratch/") && 
               !normalized.Contains("/.local-corpus/");
    }
}

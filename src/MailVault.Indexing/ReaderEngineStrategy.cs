using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;

namespace MailVault.Indexing;

public sealed record ReaderEngineDescriptor(string Name, string Version, string Description);

public sealed record ReaderEngineCapability(bool IsAvailable, string ExecutablePath, string? Version, string? LicenseWarning = null);

public sealed record ReaderEngineResult(
    string Status, 
    string? ErrorMessage, 
    int FoldersIndexed, 
    int MessagesIndexed, 
    int AttachmentsIndexed, 
    int IssuesDetected
);

public interface IReaderEngine
{
    ReaderEngineDescriptor Descriptor { get; }
    string PrecalculatedSha256 { get; set; }
    Task<ReaderEngineCapability> CheckCapabilityAsync(CancellationToken ct);
    Task<ReaderEngineResult> IndexAsync(
        string filePath,
        ICaseIndexStore store,
        string caseId,
        string operatorName,
        bool cachePreview,
        int? limit,
        IProgress<IndexingProgress>? progress,
        CancellationToken ct);
}

public static class ExternalToolDetector
{
    private static readonly string[] SearchDirectories = GetSearchDirectories();

    private static string[] GetSearchDirectories()
    {
        return new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "libpff"),
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            @"C:\Program Files\libpff",
            @"C:\Program Files (x86)\libpff",
            @"C:\libpff"
        };
    }

    public static async Task<ReaderEngineCapability> DetectToolAsync(string toolName, CancellationToken ct)
    {
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{toolName}.exe" : toolName;

        // 1. Search in configured environment PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            var separators = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            var paths = pathEnv.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in paths)
            {
                try
                {
                    string fullPath = Path.Combine(path, exeName);
                    if (File.Exists(fullPath))
                    {
                        var cap = await QueryToolVersionAsync(fullPath, toolName, ct);
                        if (cap.IsAvailable) return cap;
                    }
                }
                catch
                {
                    // Safe search bypass
                }
            }
        }

        // 2. Search in standard search directories
        foreach (var dir in SearchDirectories)
        {
            if (Directory.Exists(dir))
            {
                try
                {
                    string fullPath = Path.Combine(dir, exeName);
                    if (File.Exists(fullPath))
                    {
                        var cap = await QueryToolVersionAsync(fullPath, toolName, ct);
                        if (cap.IsAvailable) return cap;
                    }
                }
                catch
                {
                    // Safe search bypass
                }
            }
        }

        return new ReaderEngineCapability(false, exeName, null, "Ferramenta não localizada no PATH do sistema ou diretórios padrão.");
    }

    private static async Task<ReaderEngineCapability> QueryToolVersionAsync(string path, string toolName, CancellationToken ct)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-V", // Libpff usually supports -V or -h for version details
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            proc.Start();

            var readOutputTask = proc.StandardOutput.ReadToEndAsync(ct);
            var readErrorTask = proc.StandardError.ReadToEndAsync(ct);

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(2000, delayCts.Token);

            var finishedTask = await Task.WhenAny(Task.WhenAll(readOutputTask, readErrorTask), delayTask);
            if (finishedTask == delayTask)
            {
                try { proc.Kill(true); } catch { }
                return new ReaderEngineCapability(true, path, "Desconhecido (Timeout)", "Falha ao interrogar versão: timeout.");
            }

            delayCts.Cancel();

            string output = await readOutputTask;
            string error = await readErrorTask;

            string combined = (output + "\n" + error).Trim();
            string? version = null;

            // Simple parsing to detect version numbers
            var match = System.Text.RegularExpressions.Regex.Match(combined, @"\d{8}");
            if (match.Success)
            {
                version = match.Value;
            }
            else
            {
                var matchAlt = System.Text.RegularExpressions.Regex.Match(combined, @"\d+\.\d+\.\d+");
                if (matchAlt.Success) version = matchAlt.Value;
            }

            version ??= "Instalado";

            string? licenseWarning = null;
            if (toolName.Equals("readpst", StringComparison.OrdinalIgnoreCase))
            {
                licenseWarning = "Atenção: O readpst é distribuído sob a licença GPL-family. O uso é local e isolado.";
            }
            else if (toolName.Equals("pffexport", StringComparison.OrdinalIgnoreCase))
            {
                licenseWarning = "Atenção: O pffexport (libpff) é tipicamente sob a licença LGPL-family.";
            }

            return new ReaderEngineCapability(true, path, version, licenseWarning);
        }
        catch (Exception ex)
        {
            return new ReaderEngineCapability(true, path, "Instalado", $"Erro ao interrogar versão detalhada: {ex.Message}");
        }
    }

    public static async Task<(string stdout, string stderr, int exitCode)> ExecuteToolWithTimeoutAsync(
        string path, string arguments, int timeoutMs, CancellationToken ct)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = path,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        proc.Start();

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        using var stdoutSemaphore = new SemaphoreSlim(1, 1);
        using var stderrSemaphore = new SemaphoreSlim(1, 1);

        proc.OutputDataReceived += async (s, e) =>
        {
            if (e.Data != null)
            {
                await stdoutSemaphore.WaitAsync(ct);
                try { stdoutBuilder.AppendLine(e.Data); } finally { stdoutSemaphore.Release(); }
            }
        };

        proc.ErrorDataReceived += async (s, e) =>
        {
            if (e.Data != null)
            {
                await stderrSemaphore.WaitAsync(ct);
                try { stderrBuilder.AppendLine(e.Data); } finally { stderrSemaphore.Release(); }
            }
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(timeoutMs, cts.Token);

        var processWaitTask = proc.WaitForExitAsync(ct);
        var finishedTask = await Task.WhenAny(processWaitTask, delayTask);

        if (finishedTask == delayTask)
        {
            try
            {
                proc.Kill(true);
            }
            catch
            {
                // Safe process kill
            }

            await stdoutSemaphore.WaitAsync(ct);
            try { stdoutBuilder.AppendLine("\n[KILL] Processo finalizado por exceder o tempo limite do Watchdog."); } finally { stdoutSemaphore.Release(); }

            return (stdoutBuilder.ToString(), stderrBuilder.ToString(), -99);
        }

        cts.Cancel();

        // Ensure buffers are flushed
        await processWaitTask;

        await stdoutSemaphore.WaitAsync(ct);
        string finalStdout = stdoutBuilder.ToString();
        stdoutSemaphore.Release();

        await stderrSemaphore.WaitAsync(ct);
        string finalStderr = stderrBuilder.ToString();
        stderrSemaphore.Release();

        return (finalStdout, finalStderr, proc.ExitCode);
    }
}

public interface IMetadataOnlyEngine
{
    bool MetadataOnly { get; set; }
}

public sealed class XstReaderEngine : IReaderEngine, IMetadataOnlyEngine
{
    public bool MetadataOnly { get; set; } = true;

    public ReaderEngineDescriptor Descriptor => new ReaderEngineDescriptor(
        "XstReader",
        "1.0.6",
        "Motor lógico nativo .NET baseado em reflexão da biblioteca XstReader."
    );

    public string PrecalculatedSha256 { get; set; } = string.Empty;

    public async Task<ReaderEngineCapability> CheckCapabilityAsync(CancellationToken ct)
    {
        try
        {
            var resolver = new ReflectionAdapterResolver();
            var res = resolver.ResolveAdapter(".ost");
            if (res.Success && res.Reader != null)
            {
                return new ReaderEngineCapability(true, "Embedded Assembly", "1.0.6");
            }
            return new ReaderEngineCapability(false, "Missing", null, res.ErrorMessage);
        }
        catch (Exception ex)
        {
            return new ReaderEngineCapability(false, "Error", null, ex.Message);
        }
    }

    public async Task<ReaderEngineResult> IndexAsync(
        string filePath,
        ICaseIndexStore store,
        string caseId,
        string operatorName,
        bool cachePreview,
        int? limit,
        IProgress<IndexingProgress>? progress,
        CancellationToken ct)
    {
        string extension = Path.GetExtension(filePath);
        var resolver = new ReflectionAdapterResolver();
        var res = resolver.ResolveAdapter(extension);
        if (!res.Success || res.Reader == null)
        {
            throw new InvalidOperationException(res.ErrorMessage ?? $"Não foi possível inicializar o motor XstReader para a extensão '{extension}'.");
        }

        if (res.Reader is IMetadataOnlyAware metadataAware)
        {
            metadataAware.MetadataOnly = MetadataOnly;
        }

        var service = new IndexingService
        {
            PrecalculatedSha256 = PrecalculatedSha256
        };

        var result = await service.RunIndexAsync(
            filePath,
            store,
            res.Reader,
            caseId,
            operatorName,
            cachePreview,
            limit,
            progress,
            ct
        );

        return new ReaderEngineResult(
            result.Status,
            result.ErrorMessage,
            result.FoldersIndexed,
            result.MessagesIndexed,
            result.AttachmentsIndexed,
            result.IssuesDetected
        );
    }
}

public sealed class LibpffExternalEngine : IReaderEngine
{
    public ReaderEngineDescriptor Descriptor => new ReaderEngineDescriptor(
        "LibpffExternal",
        "Experimental",
        "Motor externo experimental de recuperação forense baseado em libpff (pffexport)."
    );

    public string PrecalculatedSha256 { get; set; } = string.Empty;
    public string DeepRecoveryMode { get; set; } = "items";

    public async Task<ReaderEngineCapability> CheckCapabilityAsync(CancellationToken ct)
    {
        return await ExternalToolDetector.DetectToolAsync("pffexport", ct);
    }

    public async Task<ReaderEngineResult> IndexAsync(
        string filePath,
        ICaseIndexStore store,
        string caseId,
        string operatorName,
        bool cachePreview,
        int? limit,
        IProgress<IndexingProgress>? progress,
        CancellationToken ct)
    {
        var capability = await CheckCapabilityAsync(ct);
        if (!capability.IsAvailable)
        {
            throw new InvalidOperationException("O utilitário externo 'pffexport' não está disponível ou instalado no sistema.");
        }

        var pffInfoCap = await ExternalToolDetector.DetectToolAsync("pffinfo", ct);
        if (!pffInfoCap.IsAvailable)
        {
            throw new InvalidOperationException("O utilitário externo 'pffinfo' não está disponível ou instalado no sistema.");
        }

        string caseFolderPath = Path.GetDirectoryName(store.DatabasePath) ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(caseFolderPath))
        {
            Directory.CreateDirectory(caseFolderPath);
        }

        // 1. Dynamic Mode Verification (Probe)
        progress?.Report(new IndexingProgress("OpeningEvidence", "", 0, 0, 0, 0, 5.0, TimeSpan.Zero, "Validando suporte a parâmetros da ferramenta nativa...", true));
        var (helpStdout, helpStderr, helpExit) = await ExternalToolDetector.ExecuteToolWithTimeoutAsync(capability.ExecutablePath, "", 5000, ct);
        string combinedHelp = helpStdout + "\n" + helpStderr;

        string modeFlag = DeepRecoveryMode switch
        {
            "recovered" => "recovered",
            "all" => "all",
            _ => "items"
        };
        string formatFlag = DeepRecoveryMode == "all" ? "all" : "text";

        bool supportsModeArg = combinedHelp.Contains("-m") || combinedHelp.Contains("mode");
        if (!supportsModeArg || !combinedHelp.Contains(modeFlag))
        {
            return new ReaderEngineResult(
                "NativeToolFailed",
                $"O utilitário nativo pffexport não possui suporte ou não reconhece o modo de extração solicitado: '{modeFlag}'.",
                0, 0, 0, 1
            );
        }

        bool supportsFormatArg = combinedHelp.Contains("-f") || combinedHelp.Contains("format");
        if (!supportsFormatArg || !combinedHelp.Contains(formatFlag))
        {
            return new ReaderEngineResult(
                "NativeToolFailed",
                $"O utilitário nativo pffexport não possui suporte ou não reconhece o formato de extração solicitado: '{formatFlag}'.",
                0, 0, 0, 1
            );
        }

        // 2. Evidence SHA-256 Calculation
        string sha256 = PrecalculatedSha256;
        if (string.IsNullOrWhiteSpace(sha256))
        {
            progress?.Report(new IndexingProgress("OpeningEvidence", "", 0, 0, 0, 0, 10.0, TimeSpan.Zero, "Calculando hash SHA-256 da evidência original...", true));
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hashBytes = await sha.ComputeHashAsync(stream, ct);
            sha256 = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        // 3. Execute pffinfo
        progress?.Report(new IndexingProgress("OpeningEvidence", "", 0, 0, 0, 0, 20.0, TimeSpan.Zero, "Executando pffinfo para diagnóstico de cabeçalho...", true));
        var pffinfoStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (infoStdout, infoStderr, infoExitCode) = await ExternalToolDetector.ExecuteToolWithTimeoutAsync(
            pffInfoCap.ExecutablePath,
            $"\"{filePath}\"",
            30000,
            ct
        );
        pffinfoStopwatch.Stop();

        // 4. Build recovery directory and options

        string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        string recoveryDir = Path.Combine(caseFolderPath, "external-recovery", "pffexport", runId);
        if (!Directory.Exists(recoveryDir))
        {
            Directory.CreateDirectory(recoveryDir);
        }

        string exportArgs = $"-m {modeFlag} -f {formatFlag} -q -o \"{recoveryDir}\" \"{filePath}\"";

        // 5. Execute pffexport
        progress?.Report(new IndexingProgress("OpeningEvidence", "", 0, 0, 0, 0, 40.0, TimeSpan.Zero, $"Executando pffexport em modo {modeFlag} (recuperação profunda)...", true));
        var pffexportStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (stdout, stderr, exitCode) = await ExternalToolDetector.ExecuteToolWithTimeoutAsync(
            capability.ExecutablePath,
            exportArgs,
            180000, // Capped Timeout
            ct
        );
        pffexportStopwatch.Stop();

        // 6. Output statistics scanning
        int outputFileCount = 0;
        long outputTotalBytes = 0;
        var firstOutputFiles = new List<string>();
        if (Directory.Exists(recoveryDir))
        {
            var files = Directory.GetFiles(recoveryDir, "*", SearchOption.AllDirectories);
            outputFileCount = files.Length;
            foreach (var f in files)
            {
                outputTotalBytes += new FileInfo(f).Length;
                if (firstOutputFiles.Count < 10)
                {
                    firstOutputFiles.Add(Path.GetRelativePath(recoveryDir, f));
                }
            }
        }

        // 7. Summarize and Sanitize Stderr
        string errorCategory;
        var deduplicatedErrors = NativeToolErrorSummarizer.Summarize(stderr, out errorCategory);

        string sanitizedRawStderr = NativeToolErrorSummarizer.SanitizeAndLimitStderr(stderr);
        string rawStderrPath = Path.Combine(caseFolderPath, "native-fallback-stderr.log");
        await File.WriteAllTextAsync(rawStderrPath, sanitizedRawStderr, ct);

        // 8. Classify Final Status
        string finalStatus;
        if (infoExitCode != 0)
        {
            finalStatus = "NativeDiagnosticFailed";
        }
        else if (exitCode == -99)
        {
            finalStatus = "NativeToolTimeout";
        }
        else if (outputFileCount > 0)
        {
            if (errorCategory.Contains("LocalDescriptor") || errorCategory.Contains("Attachment"))
            {
                // Even if exitCode == 0, if we have local descriptor/attachment structure corruptions, it is a partial recovery!
                finalStatus = "PartialNativeRecovery";
            }
            else if (exitCode != 0)
            {
                finalStatus = "PartialNativeRecovery";
            }
            else
            {
                finalStatus = string.IsNullOrWhiteSpace(stderr) ? "NativeExportSuccess" : "NativeExportSuccessWithWarnings";
            }
        }
        else // outputFileCount == 0
        {
            finalStatus = (errorCategory.Contains("LocalDescriptor") || errorCategory.Contains("Attachment"))
                ? "NativeToolUnsupportedStructure"
                : "NativeToolFailed";
        }

        string recommendation = finalStatus switch
        {
            "NativeToolFailed" or "NativeToolUnsupportedStructure" => "Use XstReader MetadataOnly para indexação. Use recuperação profunda apenas como tentativa experimental.",
            _ => "Extração nativa concluída ou parcial. Revise os arquivos extraídos no diretório do caso."
        };

        // 9. Structured reports saving (Historical and Latest copy)
        var report = new
        {
            caseId = caseId,
            evidencePathSanitized = Path.GetFileName(filePath),
            evidenceSha256 = sha256,
            toolVersions = new
            {
                pffinfo = pffInfoCap.Version ?? "Unknown",
                pffexport = capability.Version ?? "Unknown"
            },
            pffinfo = new
            {
                command = $"{pffInfoCap.ExecutablePath} \"{filePath}\"",
                exitCode = infoExitCode,
                stdoutSummary = infoStdout.Length > 1000 ? infoStdout.Substring(0, 1000) + "..." : infoStdout,
                stderrSummary = infoStderr.Length > 1000 ? infoStderr.Substring(0, 1000) + "..." : infoStderr,
                elapsedMs = pffinfoStopwatch.ElapsedMilliseconds
            },
            pffexport = new
            {
                command = $"{capability.ExecutablePath} {exportArgs}",
                mode = modeFlag,
                format = formatFlag,
                exitCode = exitCode,
                elapsedMs = pffexportStopwatch.ElapsedMilliseconds,
                outputPath = recoveryDir,
                outputFolderExists = Directory.Exists(recoveryDir),
                outputFileCount = outputFileCount,
                outputTotalBytes = outputTotalBytes,
                firstOutputFiles = firstOutputFiles,
                stderrSummaryDeduplicated = deduplicatedErrors,
                rawStderrPath = "native-fallback-stderr.log"
            },
            finalStatus = finalStatus,
            recommendation = recommendation
        };

        string reportJson = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        
        string historicalReportPath = Path.Combine(caseFolderPath, $"native-fallback-report-{runId}.json");
        await File.WriteAllTextAsync(historicalReportPath, reportJson, ct);

        string latestReportPath = Path.Combine(caseFolderPath, "native-fallback-report.json");
        await File.WriteAllTextAsync(latestReportPath, reportJson, ct);

        // 10. Persist issue inside case.db
        using var writer = store.CreateWriter();
        var extractionIssue = new ExtractionIssue(
            Code: finalStatus == "NativeExportSuccess" ? "MV-INFO-LIBPFF" : "MV-ERR-LIBPFF",
            Severity: finalStatus == "NativeExportSuccess" ? "Info" : "Warning",
            Message: $"Execução experimental de recuperação nativa finalizada com status: {finalStatus}.",
            ObjectId: "pffexport",
            TechnicalDetails: $"RecoveryDir: {recoveryDir}. OutputFiles: {outputFileCount}. Categoria de Erros: {errorCategory}."
        );

        await writer.BeginTransactionAsync(ct);
        await writer.SaveIssueAsync(extractionIssue, ct);
        await writer.CommitTransactionAsync(CancellationToken.None);

        // Relational database population is completely bypassed because folders/messages are never saved!
        // We only return the result
        return new ReaderEngineResult(
            finalStatus,
            finalStatus == "NativeExportSuccess" || finalStatus == "NativeExportSuccessWithWarnings" ? null : $"A ferramenta nativa retornou status: {finalStatus}.",
            0,
            0,
            0,
            finalStatus == "NativeExportSuccess" ? 0 : 1
        );
    }
}

public interface IReaderEngineFactory
{
    IEnumerable<ReaderEngineDescriptor> GetAvailableEngines();
    IReaderEngine CreateEngine(string engineName);
}

public sealed class ReaderEngineFactory : IReaderEngineFactory
{
    private readonly Dictionary<string, IReaderEngine> _engines = new(StringComparer.OrdinalIgnoreCase)
    {
        { "XstReader", new XstReaderEngine() },
        { "LibpffExternal", new LibpffExternalEngine() }
    };

    public IEnumerable<ReaderEngineDescriptor> GetAvailableEngines()
    {
        return _engines.Values.Select(e => e.Descriptor);
    }

    public IReaderEngine CreateEngine(string engineName)
    {
        if (_engines.TryGetValue(engineName, out var engine))
        {
            return engine;
        }
        throw new NotSupportedException($"O motor de leitura '{engineName}' não é suportado.");
    }
}

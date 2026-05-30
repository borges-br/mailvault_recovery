using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Indexing;

/// <summary>
/// Resultado de uma passagem de Deep Scan via libpff/pffexport (carving de itens
/// apagados/órfãos). Honesto: distingue "não abriu" de "extraiu".
/// </summary>
public sealed record DeepScanResult(
    bool ToolAvailable,
    string? ToolPath,
    string? ToolVersion,
    bool Opened,
    int ExtractedFiles,
    long ExtractedBytes,
    string OutputDir,
    int ExitCode,
    string Status,
    string? ErrorSummary,
    long ElapsedMs);

/// <summary>
/// Deep Scan opt-in/fallback baseado no pffexport vendorizado (libpff, LGPL). Roda como
/// PROCESSO SEPARADO — zero impacto no caminho rápido estrutural (XstReader). Só deve ser
/// invocado explicitamente (--deep-scan) ou como fallback quando o estrutural falha/0.
/// NÃO contorna cabeçalho destruído nem truncamento severo (limite do libpff, medido no
/// probe do Milestone 3); para esses casos reporta OpenFailed honestamente.
/// </summary>
public static class PffDeepScanRunner
{
    public static async Task<DeepScanResult> RunAsync(
        string sourcePath, string outputDir, string mode, int timeoutMs, CancellationToken ct)
    {
        var cap = await ExternalToolDetector.DetectToolAsync("pffexport", ct);
        // Caminhos ABSOLUTOS: pffexport (nativo Windows) falha ao criar o export path se receber
        // caminho relativo / com barras '/'. Normaliza source e target.
        string exportRoot = Path.GetFullPath(Path.Combine(outputDir, "_deepscan"));
        if (!cap.IsAvailable)
        {
            return new DeepScanResult(false, null, null, false, 0, 0, exportRoot, -1, "ToolNotAvailable",
                "pffexport (libpff) não localizado no PATH nem em tools/libpff. Empacote-o via publish para habilitar Deep Scan.", 0);
        }

        try { if (Directory.Exists(exportRoot)) Directory.Delete(exportRoot, true); } catch { /* best-effort */ }
        Directory.CreateDirectory(exportRoot);
        string target = Path.Combine(exportRoot, "export");
        string absSource = Path.GetFullPath(sourcePath);
        string args = $"-m {mode} -q -t \"{target}\" \"{absSource}\"";

        var sw = Stopwatch.StartNew();
        var (stdout, stderr, exit) = await ExternalToolDetector.ExecuteToolWithTimeoutAsync(
            cap.ExecutablePath, args, timeoutMs, ct);
        sw.Stop();

        int files = 0; long bytes = 0;
        if (Directory.Exists(exportRoot))
        {
            foreach (var f in Directory.GetFiles(exportRoot, "*", SearchOption.AllDirectories))
            {
                files++;
                try { bytes += new FileInfo(f).Length; } catch { /* best-effort */ }
            }
        }

        bool openFailed =
            stderr.Contains("invalid file signature", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("unable to read index node", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Error opening file", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("unable to open", StringComparison.OrdinalIgnoreCase);

        string status =
            exit == -99 ? "Timeout"
            : openFailed && files == 0 ? "OpenFailed"
            : files > 0 ? (string.IsNullOrWhiteSpace(stderr) ? "Extracted" : "PartialExtracted")
            : "NoOutput";

        string? firstErr = stderr
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("pffexport", StringComparison.OrdinalIgnoreCase));

        return new DeepScanResult(
            ToolAvailable: true,
            ToolPath: cap.ExecutablePath,
            ToolVersion: cap.Version,
            Opened: !openFailed,
            ExtractedFiles: files,
            ExtractedBytes: bytes,
            OutputDir: exportRoot,
            ExitCode: exit,
            Status: status,
            ErrorSummary: firstErr,
            ElapsedMs: sw.ElapsedMilliseconds);
    }
}

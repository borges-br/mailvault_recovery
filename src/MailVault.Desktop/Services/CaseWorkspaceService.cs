using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Indexing;

namespace MailVault.Desktop.Services;

/// <summary>
/// Describes how a case folder was opened.
/// </summary>
public enum CaseOpenMode
{
    /// <summary>Full case.db + manifest found — full mode.</summary>
    Full,
    /// <summary>case.db found but manifest.json absent — limited mode with a warning banner.</summary>
    LimitedNoManifest,
    /// <summary>Journal file detected — opened with journal-recovery warning.</summary>
    LimitedJournal,
}

/// <summary>
/// Result returned when a case is successfully opened.
/// </summary>
public sealed record CaseOpenResult(
    string CaseFolderPath,
    SqliteCaseIndexStore Store,
    CaseOpenMode OpenMode,
    string? WarningMessage);

/// <summary>
/// Opens, creates and routes MailVault cases.
/// Responsibility: lifecycle management of SqliteCaseIndexStore.
/// Does NOT own diagnostic logic — delegates to CaseWorkspaceDiagnosticService.
/// </summary>
public sealed class CaseWorkspaceService : IDisposable
{
    private readonly CaseWorkspaceDiagnosticService _diagnostics;
    private SqliteCaseIndexStore? _activeStore;

    public CaseWorkspaceService(CaseWorkspaceDiagnosticService diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Opens an existing case folder. Validates with CaseWorkspaceDiagnosticService first.
    /// Journal is a warning, not a fatal error. Absent manifest is a warning, not a fatal error.
    /// Returns null if the folder cannot be opened at all.
    /// </summary>
    public async Task<CaseOpenResult?> OpenExistingCaseAsync(string caseFolderPath, CancellationToken ct = default)
    {
        var diagnosis = await _diagnostics.DiagnoseAsync(caseFolderPath, ct);

        // Hard fail: directory or DB missing
        if (!diagnosis.DirectoryExists)
            throw new InvalidOperationException($"Diretório não encontrado: {caseFolderPath}");

        if (!diagnosis.CaseDbExists)
            throw new InvalidOperationException($"case.db não encontrado em: {caseFolderPath}");

        if (!diagnosis.CaseDbReadable)
            throw new InvalidOperationException(diagnosis.ErrorMessage ?? "Não foi possível abrir case.db.");

        // Dispose any previous store
        DisposeActiveStore();

        var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseFolderPath, ct);
        _activeStore = store;

        // Determine open mode and warning
        CaseOpenMode mode;
        string? warning = null;

        if (diagnosis.JournalFileExists && !diagnosis.ManifestExists)
        {
            mode = CaseOpenMode.LimitedJournal;
            warning = "⚠ Journal SQLite detectado e manifest.json ausente. Caso aberto em modo limitado. Recomenda-se reindexar.";
        }
        else if (diagnosis.JournalFileExists)
        {
            mode = CaseOpenMode.LimitedJournal;
            warning = "⚠ Arquivo de journal SQLite detectado. O SQLite recuperará automaticamente. Recomenda-se reindexar.";
        }
        else if (!diagnosis.ManifestExists)
        {
            mode = CaseOpenMode.LimitedNoManifest;
            warning = "⚠ manifest.json ausente. Caso aberto em modo limitado. Metadados de auditoria podem estar incompletos.";
        }
        else
        {
            mode = CaseOpenMode.Full;
        }

        return new CaseOpenResult(caseFolderPath, store, mode, warning);
    }

    /// <summary>
    /// Resolves what type of input is provided (case folder, .ost, .pst) and routes accordingly.
    /// </summary>
    public async Task<CaseOpenResult?> OpenInputAsync(string inputPath, CancellationToken ct = default)
    {
        if (Directory.Exists(inputPath))
        {
            return await OpenExistingCaseAsync(inputPath, ct);
        }

        if (File.Exists(inputPath))
        {
            string ext = Path.GetExtension(inputPath).ToLowerInvariant();
            if (ext is ".ost" or ".pst")
            {
                throw new NotSupportedException(
                    "Criação de caso a partir de .ost/.pst pela UI está em desenvolvimento. " +
                    "Use 'mailvault index <arquivo>' no terminal para criar o índice.");
            }

            throw new NotSupportedException($"Tipo de arquivo não suportado: {ext}");
        }

        throw new FileNotFoundException($"Caminho não encontrado: {inputPath}");
    }

    public void CloseActiveCase()
    {
        DisposeActiveStore();
    }

    private void DisposeActiveStore()
    {
        _activeStore?.Dispose();
        _activeStore = null;
    }

    public void Dispose() => DisposeActiveStore();
}

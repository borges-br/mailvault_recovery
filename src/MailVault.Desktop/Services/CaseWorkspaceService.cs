using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Domain;
using MailVault.Indexing;

namespace MailVault.Desktop.Services;

/// <summary>
/// Describes how a case folder was opened.
/// </summary>
public enum CaseOpenMode
{
    Full,
    LimitedNoManifest,
    LimitedJournal,
    LimitedNoManifestAndJournal
}

public enum CaseWorkspaceStatus
{
    Intact,
    Limited,
    Warning,
    Empty,
    Error
}

public sealed record CaseWorkspaceStats(
    int FolderCount,
    int MessageCount,
    int AttachmentCount,
    int IssueCount,
    long TotalAttachmentSizeBytes);

/// <summary>
/// Result returned when a case is successfully opened and read.
/// </summary>
public sealed record CaseOpenResult(
    string CaseFolderPath,
    string CaseDbPath,
    SqliteCaseIndexStore Store,
    CaseOpenMode OpenMode,
    CaseWorkspaceStatus Status,
    CaseValidationResult Diagnostic,
    CaseInfoRef? CaseInfo,
    CaseWorkspaceStats Stats,
    IReadOnlyList<string> Warnings,
    string? ErrorMessage,
    string? SuggestedAction)
{
    public string? WarningMessage => Warnings.Count == 0 ? null : string.Join(Environment.NewLine, Warnings);
}

/// <summary>
/// Opens, creates and routes MailVault cases.
/// Responsibility: lifecycle management of SqliteCaseIndexStore.
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
    /// Opens an existing case folder and loads case_info, stats and warnings.
    /// Missing manifest and SQLite journal are warnings; invalid schema and unreadable DB are errors.
    /// </summary>
    public async Task<CaseOpenResult?> OpenExistingCaseAsync(string caseFolderPath, CancellationToken ct = default)
    {
        var diagnosis = await _diagnostics.DiagnoseAsync(caseFolderPath, ct);

        if (!diagnosis.DirectoryExists)
        {
            throw new InvalidOperationException(diagnosis.ErrorMessage ?? $"Diretório não encontrado: {caseFolderPath}");
        }

        if (!diagnosis.CaseDbExists)
        {
            throw new InvalidOperationException(diagnosis.ErrorMessage ?? $"case.db não encontrado em: {caseFolderPath}");
        }

        if (!diagnosis.CaseDbReadable)
        {
            throw new InvalidOperationException(diagnosis.ErrorMessage ?? "Não foi possível abrir case.db.");
        }

        if (!diagnosis.SchemaValid)
        {
            string missing = diagnosis.MissingSchemaObjects.Count == 0
                ? "sem detalhes"
                : string.Join(", ", diagnosis.MissingSchemaObjects);
            throw new InvalidOperationException($"{diagnosis.ErrorMessage ?? "Schema do case.db incompatível."} Itens ausentes: {missing}");
        }

        DisposeActiveStore();

        var store = new SqliteCaseIndexStore();
        try
        {
            await store.InitializeAsync(caseFolderPath, ct);
            _activeStore = store;

            using var reader = store.CreateReader();
            CaseInfoRef? caseInfo = await reader.GetCaseInfoAsync(ct);
            var stats = new CaseWorkspaceStats(
                FolderCount: await reader.GetFolderCountAsync(ct),
                MessageCount: await reader.GetMessageCountAsync(ct),
                AttachmentCount: await reader.GetAttachmentCountAsync(ct),
                IssueCount: await reader.GetIssueCountAsync(ct),
                TotalAttachmentSizeBytes: await reader.GetTotalAttachmentSizeAsync(ct));

            var warnings = new List<string>(diagnosis.Warnings);
            if (caseInfo is null)
            {
                warnings.Add("case_info não contém metadados do caso.");
            }

            if (stats.MessageCount == 0)
            {
                warnings.Add("case.db foi aberto, mas não há mensagens indexadas.");
            }

            CaseOpenMode mode = DetermineOpenMode(diagnosis);
            CaseWorkspaceStatus status = DetermineStatus(diagnosis, stats, warnings);
            string? suggestedAction = status == CaseWorkspaceStatus.Empty
                ? "Reindexe a mídia de origem ou confira o audit.log para entender por que nenhuma mensagem foi gravada."
                : diagnosis.SuggestedAction;

            return new CaseOpenResult(
                CaseFolderPath: caseFolderPath,
                CaseDbPath: diagnosis.CaseDbPath,
                Store: store,
                OpenMode: mode,
                Status: status,
                Diagnostic: diagnosis,
                CaseInfo: caseInfo,
                Stats: stats,
                Warnings: warnings,
                ErrorMessage: null,
                SuggestedAction: suggestedAction);
        }
        catch
        {
            if (!ReferenceEquals(_activeStore, store))
            {
                store.Dispose();
            }
            else
            {
                DisposeActiveStore();
            }

            throw;
        }
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

    private static CaseOpenMode DetermineOpenMode(CaseValidationResult diagnosis)
    {
        if (!diagnosis.ManifestExists && diagnosis.JournalFileExists)
        {
            return CaseOpenMode.LimitedNoManifestAndJournal;
        }

        if (!diagnosis.ManifestExists)
        {
            return CaseOpenMode.LimitedNoManifest;
        }

        if (diagnosis.JournalFileExists)
        {
            return CaseOpenMode.LimitedJournal;
        }

        return CaseOpenMode.Full;
    }

    private static CaseWorkspaceStatus DetermineStatus(
        CaseValidationResult diagnosis,
        CaseWorkspaceStats stats,
        IReadOnlyCollection<string> warnings)
    {
        if (stats.MessageCount == 0)
        {
            return CaseWorkspaceStatus.Empty;
        }

        if (!diagnosis.ManifestExists)
        {
            return CaseWorkspaceStatus.Limited;
        }

        if (warnings.Count > 0)
        {
            return CaseWorkspaceStatus.Warning;
        }

        return CaseWorkspaceStatus.Intact;
    }

    private void DisposeActiveStore()
    {
        _activeStore?.Dispose();
        _activeStore = null;
    }

    public void Dispose() => DisposeActiveStore();
}

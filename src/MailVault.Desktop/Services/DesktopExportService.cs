using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Audit;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Indexing;

namespace MailVault.Desktop.Services;

public class DesktopExportService
{
    public virtual async Task<ExportJobResult> RunExportAsync(
        string caseFolderPath,
        string format,
        string? outDir,
        string? folder,
        int? limit,
        int? offset,
        bool includeAttachments,
        bool extractAttachments,
        bool overwrite,
        bool dryRun,
        IProgressReporter progressReporter,
        CancellationToken ct)
    {
        string dbPath = Path.Combine(caseFolderPath, "case.db");
        if (!File.Exists(dbPath))
        {
            throw new FileNotFoundException($"Banco case.db não localizado em '{caseFolderPath}'");
        }

        string auditLogFilePath = Path.Combine(caseFolderPath, "audit.log");
        var auditWriter = new FileAuditTrailWriter(auditLogFilePath);

        await auditWriter.WriteEventAsync(new AuditEvent(
            EventId: Guid.NewGuid().ToString(),
            Timestamp: DateTimeOffset.Now,
            Action: "ExportStartedByUI",
            OperatorName: Environment.UserName,
            Details: $"Exportação via UI iniciada para formato {format.ToUpperInvariant()}."
        ), ct);

        try
        {
            string finalOutputDir = outDir ?? Path.Combine(caseFolderPath, "exports");

            var orchestrator = new WorkerProcessOrchestrator();
            var jobConfig = new WorkerJobConfig(
                EvidencePath: "", 
                CasePath: caseFolderPath,
                CaseId: "",
                OperatorId: Environment.UserName,
                EvidenceSha256: "",
                EvidenceSize: 0,
                SelectedReaderEngine: ""
            )
            {
                JobKind = "Export",
                ExportFormat = format,
                OutputPath = finalOutputDir,
                IncludeAttachments = includeAttachments,
                ExtractAttachments = extractAttachments
            };

            var result = await orchestrator.RunJobAsync(
                jobConfig,
                p => {
                    progressReporter.ReportProgress(p.FoldersProcessed, p.Message);
                },
                ct
            );

            if (result.Status == "Failed")
            {
                throw new InvalidOperationException(result.ErrorMessage ?? "A exportação out-of-process falhou.");
            }

            await auditWriter.WriteEventAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString(),
                Timestamp: DateTimeOffset.Now,
                Action: "ExportCompletedByUI",
                OperatorName: Environment.UserName,
                Details: $"Exportação finalizada. DryRun={dryRun}. Total exportado: {result.MessagesIndexed}."
            ), ct);

            return new ExportJobResult(
                ExportId: Guid.NewGuid().ToString("N"),
                Format: format,
                FoldersSelected: 0,
                MessagesSelected: result.MessagesIndexed,
                MessagesExported: result.MessagesIndexed,
                MessagesFailed: 0,
                AttachmentsExported: 0,
                AttachmentsFailed: 0,
                Issues: Array.Empty<ExtractionIssue>(),
                ExportedFiles: Array.Empty<string>(),
                DurationMs: 0
            );
        }
        catch (Exception ex)
        {
            await auditWriter.WriteEventAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString(),
                Timestamp: DateTimeOffset.Now,
                Action: "ExportFailedByUI",
                OperatorName: Environment.UserName,
                Details: $"Falha técnica durante a exportação via UI: {ex.Message}"
            ), CancellationToken.None);
            throw;
        }
    }
}

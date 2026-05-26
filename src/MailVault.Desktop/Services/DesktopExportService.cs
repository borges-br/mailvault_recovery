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

            using var store = new SqliteCaseIndexStore();
            await store.InitializeAsync(caseFolderPath, ct);

            using var caseReader = store.CreateReader();
            var caseInfo = await caseReader.GetCaseInfoAsync(ct);
            if (caseInfo == null)
            {
                throw new InvalidOperationException("Metadados do caso (case_info) estão ausentes no case.db.");
            }

            var adapterResolver = new ReflectionAdapterResolver();

            IMessageExporter innerExporter;
            if (format.Equals("eml", StringComparison.OrdinalIgnoreCase))
            {
                innerExporter = new MailVault.Exporters.Eml.EmlExporter();
            }
            else
            {
                var emlExporter = new MailVault.Exporters.Eml.EmlExporter();
                innerExporter = new MailVault.Exporters.Mbox.MboxExporter(emlExporter);
            }

            var exportRunner = new ExportJobRunner();
            var options = new ExportJobOptions(
                CaseFolder: caseFolderPath,
                Format: format,
                OutputDir: finalOutputDir,
                FolderIdOrPath: folder,
                Limit: limit,
                Offset: offset,
                IncludeAttachments: includeAttachments,
                ExtractAttachments: extractAttachments,
                Overwrite: overwrite,
                DryRun: dryRun
            );

            var result = await exportRunner.RunExportJobAsync(options, caseReader, adapterResolver, innerExporter, progressReporter, ct);

            await auditWriter.WriteEventAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString(),
                Timestamp: DateTimeOffset.Now,
                Action: "ExportCompletedByUI",
                OperatorName: Environment.UserName,
                Details: $"Exportação finalizada. DryRun={dryRun}. Total selecionado: {result.MessagesSelected}, exportado: {result.MessagesExported}."
            ), ct);

            return result;
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

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Audit;
using MailVault.Domain;
using MailVault.Validation;

namespace MailVault.Desktop.Services;

public class DesktopValidationService
{
    public virtual async Task<ValidationReport> ValidateExportAsync(
        string caseFolderPath,
        string? exportFolderPath,
        string format,
        bool strict,
        bool checkEml,
        bool checkMbox,
        bool checkAtt,
        int? sampleSize,
        string? outDir,
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
            Action: "ValidationStartedByUI",
            OperatorName: Environment.UserName,
            Details: $"Validação técnica iniciada via UI. Format={format}, Strict={strict}."
        ), ct);

        try
        {
            var engine = new ValidationEngine();
            string finalOutDir = outDir ?? caseFolderPath;

            var report = await engine.ValidateAsync(
                caseFolderPath,
                exportFolderPath,
                format,
                strict,
                checkEml,
                checkMbox,
                checkAtt,
                sampleSize,
                finalOutDir,
                ct
            );

            await auditWriter.WriteEventAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString(),
                Timestamp: DateTimeOffset.Now,
                Action: "ValidationCompletedByUI",
                OperatorName: Environment.UserName,
                Details: $"Validação técnica concluída. Status do relatório: {report.Status}. Mensagens indexadas: {report.IndexedMessages}, exportadas: {report.ExportedMessages}."
            ), ct);

            return report;
        }
        catch (Exception ex)
        {
            await auditWriter.WriteEventAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString(),
                Timestamp: DateTimeOffset.Now,
                Action: "ValidationFailedByUI",
                OperatorName: Environment.UserName,
                Details: $"Falha técnica durante validação de conformidade via UI: {ex.Message}"
            ), CancellationToken.None);
            throw;
        }
    }
}

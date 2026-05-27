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
                JobKind = "ValidateExport",
                ExportFormat = format
            };

            var result = await orchestrator.RunJobAsync(
                jobConfig,
                p => { },
                ct
            );

            if (result.Status == "Failed")
            {
                throw new InvalidOperationException(result.ErrorMessage ?? "A validação out-of-process falhou.");
            }

            // Read the validation-report.json that was saved in the caseFolderPath
            string reportFile = Path.Combine(caseFolderPath, "validation-report.json");
            ValidationReport report;
            if (File.Exists(reportFile))
            {
                string json = await File.ReadAllTextAsync(reportFile, ct);
                report = System.Text.Json.JsonSerializer.Deserialize<ValidationReport>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                         ?? CreateFailedReport("Falha ao deserializar o relatório de validação.");
            }
            else
            {
                report = CreateFailedReport("Relatório de validação não encontrado no disco após execução.");
            }

            await auditWriter.WriteEventAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString(),
                Timestamp: DateTimeOffset.Now,
                Action: "ValidationCompletedByUI",
                OperatorName: Environment.UserName,
                Details: $"Validação técnica concluída. Status do relatório: {report.Status}."
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

    private static ValidationReport CreateFailedReport(string message)
    {
        return new ValidationReport(
            ValidationId: Guid.NewGuid().ToString("N"),
            CaseId: "",
            SourceFileMasked: "",
            SourceSha256: "",
            AdapterName: "",
            AdapterVersion: "",
            ExportId: "",
            ExportFormat: "",
            StartedAt: DateTimeOffset.Now,
            CompletedAt: DateTimeOffset.Now,
            DurationMs: 0,
            IndexedMessages: 0,
            SelectedMessages: 0,
            ExportedMessages: 0,
            FailedMessages: 0,
            IndexedAttachments: 0,
            ExportedAttachments: 0,
            FailedAttachments: 0,
            EmptyExportedFiles: 0,
            DuplicateOutputNames: 0,
            MissingExpectedFiles: 0,
            PathSafetyIssues: 0,
            FoldersChecked: Array.Empty<string>(),
            FolderResults: Array.Empty<FolderValidationResult>(),
            WarningCount: 0,
            ErrorCount: 1,
            Status: "Failed",
            Issues: new[] {
                new ValidationIssue("MV-ERR-VALIDATION-MISSING", "Error", message, "ValidationEngine")
            }
        );
    }
}

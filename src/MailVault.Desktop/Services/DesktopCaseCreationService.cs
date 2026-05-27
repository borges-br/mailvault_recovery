using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Audit;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Indexing;

namespace MailVault.Desktop.Services;

public sealed record DesktopIndexingProgress(
    string CurrentStep,
    double Percentage,
    int FoldersIndexed,
    int MessagesIndexed,
    int AttachmentsIndexed,
    int IssuesDetected);

public class DesktopCaseCreationService
{
    public interface IIndexingProgressReporter
    {
        void Report(DesktopIndexingProgress progress);
    }

    public virtual async Task<IndexResult> CreateAndIndexCaseAsync(
        string sourceFilePath,
        string outputDir,
        string caseId,
        bool cachePreview,
        int? limit,
        IIndexingProgressReporter progressReporter,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.Now;
        string caseFolderPath = Path.Combine(outputDir, caseId);
        Directory.CreateDirectory(caseFolderPath);

        string auditLogFilePath = Path.Combine(caseFolderPath, "audit.log");
        var auditWriter = new FileAuditTrailWriter(auditLogFilePath);

        progressReporter.Report(new DesktopIndexingProgress("Calculando assinatura hash de integridade (SHA-256)...", 5, 0, 0, 0, 0));

        await auditWriter.WriteEventAsync(new AuditEvent(
            EventId: Guid.NewGuid().ToString(),
            Timestamp: DateTimeOffset.Now,
            Action: "IndexStartedByUI",
            OperatorName: Environment.UserName,
            Details: $"Indexação via UI iniciada para a evidência original: {sourceFilePath}."
        ), ct);

        // Step 1: Calculate SHA-256
        var hashService = new HashService();
        var progressReporterWrapper = new ProgressWrapper(progressReporter, "Calculando hash (SHA-256)");
        string sha256 = await hashService.CalculateSha256Async(sourceFilePath, progressReporterWrapper, ct);

        await auditWriter.WriteEventAsync(new AuditEvent(
            EventId: Guid.NewGuid().ToString(),
            Timestamp: DateTimeOffset.Now,
            Action: "HashCalculated",
            OperatorName: Environment.UserName,
            Details: $"Hash da evidência original calculado: {sha256}."
        ), ct);

        // Step 2: Resolve Adapter
        progressReporter.Report(new DesktopIndexingProgress("Resolvendo adaptador de e-mail...", 25, 0, 0, 0, 0));
        string extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        var resolver = new ReflectionAdapterResolver();
        var loadResult = resolver.ResolveAdapter(extension);

        if (!loadResult.Success || loadResult.Reader == null)
        {
            string err = loadResult.ErrorMessage ?? "Erro desconhecido ao carregar o adaptador.";
            await auditWriter.WriteEventAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString(),
                Timestamp: DateTimeOffset.Now,
                Action: "AdapterResolutionFailed",
                OperatorName: Environment.UserName,
                Details: $"Falha ao resolver adaptador para {extension}. Erro: {err}"
            ), CancellationToken.None);

            throw new InvalidOperationException(err);
        }

        await auditWriter.WriteEventAsync(new AuditEvent(
            EventId: Guid.NewGuid().ToString(),
            Timestamp: DateTimeOffset.Now,
            Action: "AdapterResolved",
            OperatorName: Environment.UserName,
            Details: $"Adaptador '{loadResult.Reader.ReaderName}' carregado com sucesso."
        ), ct);

        // Step 3: Indexing Execution
        progressReporter.Report(new DesktopIndexingProgress("Abrindo banco de dados case.db...", 35, 0, 0, 0, 0));
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseFolderPath, ct);

        progressReporter.Report(new DesktopIndexingProgress("Executando pipeline de indexação...", 45, 0, 0, 0, 0));
        var indexingService = new IndexingService
        {
            PrecalculatedSha256 = sha256
        };

        var progressAdapter = new Progress<IndexingProgress>(p =>
        {
            double overallPercent = 35;
            if (p.Phase == "Completed")
            {
                overallPercent = 95;
            }
            else
            {
                double estPct = Math.Min(90.0, (p.MessagesProcessed / 500.0) * 5.0);
                overallPercent = 35 + estPct * 0.60;
            }

            progressReporter.Report(new DesktopIndexingProgress(
                CurrentStep: p.Message,
                Percentage: overallPercent,
                FoldersIndexed: p.FoldersProcessed,
                MessagesIndexed: p.MessagesProcessed,
                AttachmentsIndexed: p.AttachmentsProcessed,
                IssuesDetected: p.IssuesCount
            ));
        });

        var indexResult = await indexingService.RunIndexAsync(
            sourceFilePath,
            store,
            loadResult.Reader,
            caseId,
            Environment.UserName,
            cachePreview,
            limit,
            progressAdapter,
            ct
        );

        // Step 4: Write Manifest & Logs
        progressReporter.Report(new DesktopIndexingProgress("Finalizando caso e salvando manifesto...", 95, indexResult.FoldersIndexed, indexResult.MessagesIndexed, indexResult.AttachmentsIndexed, indexResult.IssuesDetected));

        var warnings = new List<ExtractionIssue>();
        if (!indexResult.Status.Equals("Success", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new ExtractionIssue(
                Code: "MV-WARN-UI-PARTIAL",
                Severity: "Warning",
                Message: $"Indexação concluída com status parcial ou falha controlada: {indexResult.Status}. Detalhes: {indexResult.ErrorMessage}",
                ObjectId: caseId,
                TechnicalDetails: indexResult.ErrorMessage
            ));
        }

        var manifest = new RecoveryManifest(
            CaseId: caseId,
            SourceFile: sourceFilePath,
            SourceSizeBytes: new FileInfo(sourceFilePath).Length,
            SourceSha256: sha256,
            OperatorName: Environment.UserName,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.Now,
            ToolVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0",
            Actions: new List<string> { "Created case from UI", $"Executed indexation pipeline ({indexResult.Status})" },
            Warnings: warnings
        );

        await ManifestService.SaveManifestAsync(outputDir, manifest, ct);

        await auditWriter.WriteEventAsync(new AuditEvent(
            EventId: Guid.NewGuid().ToString(),
            Timestamp: DateTimeOffset.Now,
            Action: "CaseClosedByUI",
            OperatorName: Environment.UserName,
            Details: $"Indexação do caso finalizada. Status: {indexResult.Status}. Pastas: {indexResult.FoldersIndexed}, E-mails: {indexResult.MessagesIndexed}."
        ), ct);

        progressReporter.Report(new DesktopIndexingProgress("Indexação concluída!", 100, indexResult.FoldersIndexed, indexResult.MessagesIndexed, indexResult.AttachmentsIndexed, indexResult.IssuesDetected));

        return indexResult;
    }

    private sealed class ProgressWrapper : IProgressReporter
    {
        private readonly IIndexingProgressReporter _reporter;
        private readonly string _step;

        public ProgressWrapper(IIndexingProgressReporter reporter, string step)
        {
            _reporter = reporter;
            _step = step;
        }

        public void ReportProgress(double percentage, string status)
        {
            // Map 0-100 of hash calculation to 5-25% of overall process
            double overallPercent = 5 + (percentage * 0.20);
            _reporter.Report(new DesktopIndexingProgress($"{_step}: {status}", overallPercent, 0, 0, 0, 0));
        }
    }
}

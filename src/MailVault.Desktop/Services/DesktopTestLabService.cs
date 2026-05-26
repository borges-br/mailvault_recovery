using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Indexing;

namespace MailVault.Desktop.Services;

public sealed record CorpusFileRecord(
    string FileName,
    string RelativePath,
    string MaskedPath,
    string Extension,
    long SizeBytes,
    string Sha256,
    string Category);

public sealed record PipelineStepResult(
    string StepName,
    bool Success,
    string Details,
    long DurationMs);

public sealed record PipelineSummary(
    string CaseId,
    string SourceFile,
    string Sha256,
    long SourceSizeBytes,
    string Status,
    List<PipelineStepResult> Steps,
    int FoldersIndexed,
    int MessagesIndexed,
    int AttachmentsIndexed,
    int IssuesDetected,
    int MessagesExportedEml,
    int MessagesExportedMbox,
    string ValidationStatus,
    string ErrorsJsonPath,
    long TotalDurationMs);

public sealed class DesktopTestLabService
{
    private readonly DesktopCaseCreationService _creationService;
    private readonly DesktopExportService _exportService;
    private readonly DesktopValidationService _validationService;

    public DesktopTestLabService(
        DesktopCaseCreationService creationService,
        DesktopExportService exportService,
        DesktopValidationService validationService)
    {
        _creationService = creationService;
        _exportService = exportService;
        _validationService = validationService;
    }

    public bool VerifyCorpusStructure(string corpusPath)
    {
        if (!Directory.Exists(corpusPath))
            return false;

        string[] required = { "evidences", "exports", "cases", "results" };
        return required.All(dir => Directory.Exists(Path.Combine(corpusPath, dir)));
    }

    public void CreateDefaultStructure(string corpusPath)
    {
        Directory.CreateDirectory(corpusPath);
        string[] folders = { "evidences", "exports", "cases", "results" };
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(corpusPath, folder));
        }

        // Add a friendly README in evidence folder
        string readmePath = Path.Combine(corpusPath, "evidences", "README.md");
        if (!File.Exists(readmePath))
        {
            File.WriteAllText(readmePath, "# Evidências do Corpus Local\nColoque seus arquivos .ost/.pst/.mbox/.eml locais e não-confidenciais nesta pasta para fins de teste no Test Lab.");
        }
    }

    public async Task<List<CorpusFileRecord>> ScanCorpusAsync(string corpusPath, CancellationToken ct)
    {
        var list = new List<CorpusFileRecord>();
        if (!Directory.Exists(corpusPath))
            return list;

        var files = Directory.GetFiles(corpusPath, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase) && 
                        !Path.GetFileName(f).Equals(".gitkeep", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var hashService = new HashService();
        var nullProgress = new NullProgress();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(file);
            string ext = fileInfo.Extension.ToLowerInvariant();
            string relativePath = Path.GetRelativePath(corpusPath, file);
            string maskedPath = MaskPath(relativePath);

            string category = "Outros / Desconhecido";
            if (ext == ".ost") category = "OST Microsoft Outlook";
            else if (ext == ".pst") category = "PST Microsoft Outlook";
            else if (ext == ".eml") category = "EML RFC 822";
            else if (ext == ".mbox" || Path.GetFileName(file).Equals("mbox", StringComparison.OrdinalIgnoreCase)) category = "MBOX Unix Mailbox";

            // Calculate hash securely
            string sha256 = await hashService.CalculateSha256Async(file, nullProgress, ct);

            list.Add(new CorpusFileRecord(
                FileName: Path.GetFileName(file),
                RelativePath: relativePath,
                MaskedPath: maskedPath,
                Extension: ext,
                SizeBytes: fileInfo.Length,
                Sha256: sha256,
                Category: category
            ));
        }

        return list;
    }

    public async Task<PipelineSummary> RunPipelineAsync(
        string corpusPath,
        CorpusFileRecord record,
        Action<string, double> progressReporter,
        CancellationToken ct)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var steps = new List<PipelineStepResult>();

        string absoluteSourceFile = Path.Combine(corpusPath, record.RelativePath);
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string testCaseId = $"TEST-{record.Extension.TrimStart('.').ToUpperInvariant()}-{timestamp}";
        
        string testCasesBaseDir = Path.Combine(corpusPath, "cases");
        string testExportsBaseDir = Path.Combine(corpusPath, "exports", testCaseId);
        string testCaseFolder = Path.Combine(testCasesBaseDir, testCaseId);

        progressReporter("Verificando suporte de extensão...", 5);
        if (record.Extension != ".ost" && record.Extension != ".pst")
        {
            throw new NotSupportedException(
                $"A extensão '{record.Extension}' não é suportada para indexação nesta versão. " +
                "MBOX e EML possuem suporte exclusivo para validação estrutural no Test Lab.");
        }

        // STEP 1: Inspect & Index
        progressReporter("Iniciando indexação do arquivo...", 10);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int foldersIndexed = 0, messagesIndexed = 0, attachmentsIndexed = 0, issuesDetected = 0;
        bool step1Success = false;
        string step1Details = "";

        try
        {
            var creationProgress = new ProgressWrapper(progressReporter, 10, 50);
            var indexResult = await _creationService.CreateAndIndexCaseAsync(
                absoluteSourceFile,
                testCasesBaseDir,
                testCaseId,
                cachePreview: true,
                limit: null,
                creationProgress,
                ct
            );

            foldersIndexed = indexResult.FoldersIndexed;
            messagesIndexed = indexResult.MessagesIndexed;
            attachmentsIndexed = indexResult.AttachmentsIndexed;
            issuesDetected = indexResult.IssuesDetected;
            step1Success = indexResult.Status == "Success" || indexResult.Status == "Partial";
            step1Details = $"Sucesso. Status: {indexResult.Status}. Pastas: {foldersIndexed}, Mensagens: {messagesIndexed}.";
        }
        catch (Exception ex)
        {
            step1Details = $"Erro: {ex.Message}";
        }
        sw.Stop();
        steps.Add(new PipelineStepResult("Inspect & Index", step1Success, step1Details, sw.ElapsedMilliseconds));

        if (!step1Success)
        {
            return CreateFailedSummary(testCaseId, absoluteSourceFile, record.Sha256, record.SizeBytes, steps, totalSw.ElapsedMilliseconds);
        }

        // STEP 2: Stats Query
        progressReporter("Computando estatísticas do SQLite...", 55);
        sw = System.Diagnostics.Stopwatch.StartNew();
        bool step2Success = false;
        string step2Details = "";
        try
        {
            using var store = new SqliteCaseIndexStore();
            await store.InitializeAsync(testCaseFolder, ct);
            using var reader = store.CreateReader();
            int fc = await reader.GetFolderCountAsync(ct);
            int mc = await reader.GetMessageCountAsync(ct);
            step2Success = true;
            step2Details = $"Sucesso. Total pastas indexadas: {fc}, mensagens: {mc}.";
        }
        catch (Exception ex)
        {
            step2Details = $"Erro: {ex.Message}";
        }
        sw.Stop();
        steps.Add(new PipelineStepResult("Stats Query", step2Success, step2Details, sw.ElapsedMilliseconds));

        // STEP 3: Export EML
        progressReporter("Executando exportação para EML...", 60);
        sw = System.Diagnostics.Stopwatch.StartNew();
        bool step3Success = false;
        string step3Details = "";
        int emlExported = 0;
        try
        {
            string emlOut = Path.Combine(testExportsBaseDir, "eml");
            var expResult = await _exportService.RunExportAsync(
                testCaseFolder,
                "eml",
                emlOut,
                folder: null,
                limit: null,
                offset: null,
                includeAttachments: true,
                extractAttachments: true,
                overwrite: true,
                dryRun: false,
                new ConsoleReporter(),
                ct
            );
            emlExported = expResult.MessagesExported;
            step3Success = true;
            step3Details = $"Sucesso. Exportados {emlExported} e-mails e {expResult.AttachmentsExported} anexos.";
        }
        catch (Exception ex)
        {
            step3Details = $"Erro: {ex.Message}";
        }
        sw.Stop();
        steps.Add(new PipelineStepResult("Export EML", step3Success, step3Details, sw.ElapsedMilliseconds));

        // STEP 4: Export MBOX
        progressReporter("Executando exportação para MBOX...", 75);
        sw = System.Diagnostics.Stopwatch.StartNew();
        bool step4Success = false;
        string step4Details = "";
        int mboxExported = 0;
        try
        {
            string mboxOut = Path.Combine(testExportsBaseDir, "mbox");
            var expResult = await _exportService.RunExportAsync(
                testCaseFolder,
                "mbox",
                mboxOut,
                folder: null,
                limit: null,
                offset: null,
                includeAttachments: true,
                extractAttachments: false,
                overwrite: true,
                dryRun: false,
                new ConsoleReporter(),
                ct
            );
            mboxExported = expResult.MessagesExported;
            step4Success = true;
            step4Details = $"Sucesso. Exportadas {mboxExported} mensagens.";
        }
        catch (Exception ex)
        {
            step4Details = $"Erro: {ex.Message}";
        }
        sw.Stop();
        steps.Add(new PipelineStepResult("Export MBOX", step4Success, step4Details, sw.ElapsedMilliseconds));

        // STEP 5: Validate
        progressReporter("Rodando laboratório de validação de qualidade...", 85);
        sw = System.Diagnostics.Stopwatch.StartNew();
        bool step5Success = false;
        string step5Details = "";
        string validationStatus = "Unknown";
        try
        {
            string emlFolder = Path.Combine(testExportsBaseDir, "eml");
            var report = await _validationService.ValidateExportAsync(
                testCaseFolder,
                emlFolder,
                format: "eml",
                strict: false,
                checkEml: true,
                checkMbox: false,
                checkAtt: true,
                sampleSize: null,
                outDir: testCaseFolder,
                ct
            );
            validationStatus = report.Status;
            step5Success = report.Status != "Failed";
            step5Details = $"Sucesso. Status final de qualidade: {validationStatus}.";
        }
        catch (Exception ex)
        {
            step5Details = $"Erro: {ex.Message}";
        }
        sw.Stop();
        steps.Add(new PipelineStepResult("Validation Lab", step5Success, step5Details, sw.ElapsedMilliseconds));

        totalSw.Stop();
        progressReporter("Arquivando resultados da execução...", 95);

        var summary = new PipelineSummary(
            CaseId: testCaseId,
            SourceFile: record.MaskedPath,
            Sha256: record.Sha256,
            SourceSizeBytes: record.SizeBytes,
            Status: steps.All(s => s.Success) ? "Success" : "Partial",
            Steps: steps,
            FoldersIndexed: foldersIndexed,
            MessagesIndexed: messagesIndexed,
            AttachmentsIndexed: attachmentsIndexed,
            IssuesDetected: issuesDetected,
            MessagesExportedEml: emlExported,
            MessagesExportedMbox: mboxExported,
            ValidationStatus: validationStatus,
            ErrorsJsonPath: Path.Combine(testCaseFolder, "validation-report.json"),
            TotalDurationMs: totalSw.ElapsedMilliseconds
        );

        // Save summary to results
        string resultsDir = Path.Combine(corpusPath, "results", "runs");
        Directory.CreateDirectory(resultsDir);
        string summaryJsonFile = Path.Combine(resultsDir, $"{testCaseId}-summary.json");
        string summaryMdFile = Path.Combine(resultsDir, $"{testCaseId}-summary.md");

        string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(summaryJsonFile, json);

        string md = GenerateMarkdownSummary(summary);
        File.WriteAllText(summaryMdFile, md);

        progressReporter("Pipeline executado com sucesso!", 100);
        return summary;
    }

    private PipelineSummary CreateFailedSummary(
        string caseId,
        string sourceFile,
        string sha256,
        long size,
        List<PipelineStepResult> steps,
        long totalDuration)
    {
        return new PipelineSummary(
            CaseId: caseId,
            SourceFile: MaskPath(Path.GetFileName(sourceFile)),
            Sha256: sha256,
            SourceSizeBytes: size,
            Status: "Failed",
            Steps: steps,
            FoldersIndexed: 0,
            MessagesIndexed: 0,
            AttachmentsIndexed: 0,
            IssuesDetected: 0,
            MessagesExportedEml: 0,
            MessagesExportedMbox: 0,
            ValidationStatus: "Failed",
            ErrorsJsonPath: "",
            TotalDurationMs: totalDuration
        );
    }

    private static string GenerateMarkdownSummary(PipelineSummary summary)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Relatório de Execução Pipeline — {summary.CaseId}");
        sb.AppendLine();
        sb.AppendLine($"**Origem da Evidência:** `{summary.SourceFile}`");
        sb.AppendLine($"**SHA-256 Original:** `{summary.Sha256}`");
        sb.AppendLine($"**Tamanho Origem:** {summary.SourceSizeBytes:N0} bytes");
        sb.AppendLine($"**Status Final Pipeline:** **{summary.Status}**");
        sb.AppendLine($"**Conformidade Validação:** **{summary.ValidationStatus}**");
        sb.AppendLine($"**Tempo de Execução:** {summary.TotalDurationMs} ms");
        sb.AppendLine();
        sb.AppendLine("## Detalhes das Etapas Executadas");
        sb.AppendLine();
        sb.AppendLine("| Etapa | Sucesso | Detalhes | Tempo (ms) |");
        sb.AppendLine("| :--- | :--- | :--- | :--- |");
        foreach (var s in summary.Steps)
        {
            string statusIcon = s.Success ? "✅ Sim" : "❌ Não";
            sb.AppendLine($"| {s.StepName} | {statusIcon} | {s.Details} | {s.DurationMs} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Métricas da Rodada");
        sb.AppendLine();
        sb.AppendLine($"- **Pastas Indexadas:** {summary.FoldersIndexed}");
        sb.AppendLine($"- **Mensagens Indexadas:** {summary.MessagesIndexed}");
        sb.AppendLine($"- **Anexos Indexados:** {summary.AttachmentsIndexed}");
        sb.AppendLine($"- **Divergências SQLite:** {summary.IssuesDetected}");
        sb.AppendLine($"- **Exportados para EML:** {summary.MessagesExportedEml}");
        sb.AppendLine($"- **Exportados para MBOX:** {summary.MessagesExportedMbox}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"Gerado automaticamente pelo MailVault Recovery Visual Test Lab em {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        return sb.ToString();
    }

    private static string MaskPath(string path)
    {
        return Regex.Replace(
            path,
            @"(?i)([a-z]:\\users\\)[^\\]+",
            "$1<USER>");
    }

    private sealed class NullProgress : IProgressReporter
    {
        public void ReportProgress(double percentage, string status) { }
    }

    private sealed class ConsoleReporter : IProgressReporter
    {
        public void ReportProgress(double percentage, string status) { }
    }

    private sealed class ProgressWrapper : DesktopCaseCreationService.IIndexingProgressReporter
    {
        private readonly Action<string, double> _reporter;
        private readonly double _startPercent;
        private readonly double _endPercent;

        public ProgressWrapper(Action<string, double> reporter, double startPercent, double endPercent)
        {
            _reporter = reporter;
            _startPercent = startPercent;
            _endPercent = endPercent;
        }

        public void Report(DesktopIndexingProgress progress)
        {
            double scale = (_endPercent - _startPercent) / 100.0;
            double actualPercent = _startPercent + (progress.Percentage * scale);
            string msg = $"{progress.CurrentStep} — Mapeado: {progress.FoldersIndexed} pastas, {progress.MessagesIndexed} emails.";
            _reporter(msg, actualPercent);
        }
    }
}

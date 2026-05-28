using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Audit;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Indexing;
using MailVault.Validation;
using MimeKit;
using Microsoft.Data.Sqlite;

namespace MailVault.Cli;

public static class Program
{
    private static IMailStoreReader? _injectedReader;

    public static int ExitCode { get; private set; } = 0;

    public static void InjectReader(IMailStoreReader reader)
    {
        _injectedReader = reader;
    }

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        ExitCode = 0; // Reset for testing runner environments

        var rootCommand = new RootCommand("MailVault Recovery CLI — Ferramenta local e offline de recuperação forense.");

        // Command: inspect
        var inspectCommand = new Command("inspect", "Inspeciona um arquivo .ost/.pst de origem, calcula seu hash SHA-256 e gera o manifesto.");
        var fileArgInspect = new Argument<FileInfo>("file", "O caminho do arquivo .ost/.pst a ser inspecionado.") { Arity = ArgumentArity.ExactlyOne };
        var outOptInspect = new Option<DirectoryInfo>("--out", "O diretório de saída base para salvar a pasta do caso.") { IsRequired = false };
        outOptInspect.SetDefaultValue(new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "mailvault-cases")));
        inspectCommand.AddArgument(fileArgInspect);
        inspectCommand.AddOption(outOptInspect);
        inspectCommand.SetHandler(async (FileInfo file, DirectoryInfo outDir) =>
        {
            await HandleInspectAsync(file, outDir);
        }, fileArgInspect, outOptInspect);

        // Command: tree
        var treeCommand = new Command("tree", "Exibe a árvore hierárquica de pastas de um arquivo .ost/.pst.");
        var fileArgTree = new Argument<FileInfo>("file", "O caminho do arquivo .ost/.pst.") { Arity = ArgumentArity.ExactlyOne };
        var outOptTree = new Option<DirectoryInfo>("--out", "O diretório de saída base para o caso.") { IsRequired = false };
        outOptTree.SetDefaultValue(new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "mailvault-cases")));
        var maxDepthOpt = new Option<int>("--max-depth", () => 99, "Profundidade máxima de exibição de subpastas.");
        treeCommand.AddArgument(fileArgTree);
        treeCommand.AddOption(outOptTree);
        treeCommand.AddOption(maxDepthOpt);
        treeCommand.SetHandler(async (FileInfo file, DirectoryInfo outDir, int maxDepth) =>
        {
            await HandleTreeAsync(file, outDir, maxDepth);
        }, fileArgTree, outOptTree, maxDepthOpt);

        // Command: list
        var listCommand = new Command("list", "Lista e-mails contidos em uma pasta do arquivo .ost/.pst.");
        var fileArgList = new Argument<FileInfo>("file", "O caminho do arquivo .ost/.pst.") { Arity = ArgumentArity.ExactlyOne };
        var folderOpt = new Option<string>("--folder", "ID ou caminho completo da pasta para listagem.") { IsRequired = true };
        var limitOpt = new Option<int>("--limit", () => 50, "Quantidade máxima de e-mails a exibir.");
        var offsetOpt = new Option<int>("--offset", () => 0, "Quantidade de e-mails a ignorar.");
        var outOptList = new Option<DirectoryInfo>("--out", "O diretório de saída base para o caso.") { IsRequired = false };
        outOptList.SetDefaultValue(new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "mailvault-cases")));
        listCommand.AddArgument(fileArgList);
        listCommand.AddOption(folderOpt);
        listCommand.AddOption(limitOpt);
        listCommand.AddOption(offsetOpt);
        listCommand.AddOption(outOptList);
        listCommand.SetHandler(async (FileInfo file, string folder, int limit, int offset, DirectoryInfo outDir) =>
        {
            await HandleListAsync(file, folder, limit, offset, outDir);
        }, fileArgList, folderOpt, limitOpt, offsetOpt, outOptList);

        // Command: preview
        var previewCommand = new Command("preview", "Exibe detalhes seguros de uma mensagem específica.");
        var fileArgPreview = new Argument<FileInfo>("file", "O caminho do arquivo .ost/.pst.");
        var msgIdOpt = new Option<string>("--message-id", "O ID interno da mensagem.") { IsRequired = true };
        var bodyLinesOpt = new Option<int>("--body-lines", () => 30, "Limite de linhas do corpo da mensagem no preview.");
        var outOptPreview = new Option<DirectoryInfo>("--out", "O diretório de saída base para o caso.") { IsRequired = false };
        outOptPreview.SetDefaultValue(new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "mailvault-cases")));
        previewCommand.AddArgument(fileArgPreview);
        previewCommand.AddOption(msgIdOpt);
        previewCommand.AddOption(bodyLinesOpt);
        previewCommand.AddOption(outOptPreview);
        previewCommand.SetHandler(async (FileInfo file, string messageId, int bodyLines, DirectoryInfo outDir) =>
        {
            await HandlePreviewAsync(file, messageId, bodyLines, outDir);
        }, fileArgPreview, msgIdOpt, bodyLinesOpt, outOptPreview);

        // NEW Command: index
        var indexCommand = new Command("index", "Lê e-mails da mídia via adapter e indexa metadados no case.db de forma persistente.");
        var fileArgIndex = new Argument<FileInfo>("file", "O caminho do arquivo .ost/.pst.") { Arity = ArgumentArity.ExactlyOne };
        var outOptIndex = new Option<DirectoryInfo>("--out", "O diretório de saída base para o caso.") { IsRequired = false };
        outOptIndex.SetDefaultValue(new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "mailvault-cases")));
        var caseIdOpt = new Option<string>("--case-id", "Opcional: ID de caso pré-definido.");
        var forceOpt = new Option<bool>("--force", "Força a recriação do índice do caso caso ele já exista.");
        var limitOptIndex = new Option<int?>("--limit", "Opcional: Limita a quantidade de e-mails indexados por pasta.");
        var noPreviewCacheOpt = new Option<bool>("--no-preview-cache", "Desativa o cacheamento e a geração do preview do corpo da mensagem.");
        indexCommand.AddArgument(fileArgIndex);
        indexCommand.AddOption(outOptIndex);
        indexCommand.AddOption(caseIdOpt);
        indexCommand.AddOption(forceOpt);
        indexCommand.AddOption(limitOptIndex);
        indexCommand.AddOption(noPreviewCacheOpt);
        indexCommand.SetHandler(async (FileInfo file, DirectoryInfo outDir, string? caseId, bool force, int? limit, bool noPreviewCache) =>
        {
            await HandleIndexAsync(file, outDir, caseId, force, limit, noPreviewCache);
        }, fileArgIndex, outOptIndex, caseIdOpt, forceOpt, limitOptIndex, noPreviewCacheOpt);

        // NEW Command: stats
        var statsCommand = new Command("stats", "Exibe estatísticas consolidadas e forenses a partir do índice case.db.");
        var caseFolderArgStats = new Argument<DirectoryInfo>("case-folder", "A pasta de caso contendo o arquivo case.db.") { Arity = ArgumentArity.ExactlyOne };
        statsCommand.AddArgument(caseFolderArgStats);
        statsCommand.SetHandler(async (DirectoryInfo caseFolder) =>
        {
            await HandleStatsAsync(caseFolder);
        }, caseFolderArgStats);

        // NEW Command: search
        var searchCommand = new Command("search", "Busca mensagens no índice persistente do caso.");
        var caseFolderArgSearch = new Argument<DirectoryInfo>("case-folder", "A pasta de caso contendo o arquivo case.db.") { Arity = ArgumentArity.ExactlyOne };
        var queryOpt = new Option<string>("--query", "Termo de texto para pesquisar.") { IsRequired = true };
        var folderOptSearch = new Option<string?>("--folder", "Caminho da pasta para limitar a busca.");
        var limitOptSearch = new Option<int>("--limit", () => 50, "Limite de resultados.");
        var offsetOptSearch = new Option<int>("--offset", () => 0, "Ignorar os primeiros N e-mails.");
        var includePreviewOpt = new Option<bool>("--include-preview", "Se marcado, exibe o preview do e-mail.");
        searchCommand.AddArgument(caseFolderArgSearch);
        searchCommand.AddOption(queryOpt);
        searchCommand.AddOption(folderOptSearch);
        searchCommand.AddOption(limitOptSearch);
        searchCommand.AddOption(offsetOptSearch);
        searchCommand.AddOption(includePreviewOpt);
        searchCommand.SetHandler(async (DirectoryInfo caseFolder, string query, string? folder, int limit, int offset, bool includePreview) =>
        {
            await HandleSearchAsync(caseFolder, query, folder, limit, offset, includePreview);
        }, caseFolderArgSearch, queryOpt, folderOptSearch, limitOptSearch, offsetOptSearch, includePreviewOpt);

        // NEW Command: export
        var exportCommand = new Command("export", "Exporta mensagens do caso para os formatos EML ou MBOX com suporte forense.");
        var caseFolderArgExport = new Argument<DirectoryInfo>("case-folder", "A pasta de caso contendo o arquivo case.db.") { Arity = ArgumentArity.ExactlyOne };
        var formatOpt = new Option<string>("--format", "Formato de exportação: eml ou mbox.") { IsRequired = true };
        formatOpt.FromAmong("eml", "mbox");
        var outOptExport = new Option<DirectoryInfo>("--out", "Diretório de saída para salvar os e-mails exportados.");
        var folderOptExport = new Option<string?>("--folder", "Caminho ou ID da pasta interna a ser exportada.");
        var limitOptExport = new Option<int?>("--limit", "Limite máximo de e-mails a exportar por pasta.");
        var offsetOptExport = new Option<int?>("--offset", "Ignorar os primeiros N e-mails na exportação.");
        var includeAttachmentsOpt = new Option<bool>("--include-attachments", () => true, "Se ativado, incorpora anexos na estrutura MIME.");
        var extractAttachmentsOpt = new Option<bool>("--extract-attachments", () => false, "Se ativado, extrai fisicamente os anexos para arquivos avulsos.");
        var overwriteOpt = new Option<bool>("--overwrite", () => false, "Se marcado, sobrescreve exportações anteriores na pasta de saída.");
        var dryRunOpt = new Option<bool>("--dry-run", () => false, "Se marcado, apenas valida a exportação e calcula estimativas sem gravar em disco.");

        exportCommand.AddArgument(caseFolderArgExport);
        exportCommand.AddOption(formatOpt);
        exportCommand.AddOption(outOptExport);
        exportCommand.AddOption(folderOptExport);
        exportCommand.AddOption(limitOptExport);
        exportCommand.AddOption(offsetOptExport);
        exportCommand.AddOption(includeAttachmentsOpt);
        exportCommand.AddOption(extractAttachmentsOpt);
        exportCommand.AddOption(overwriteOpt);
        exportCommand.AddOption(dryRunOpt);

        exportCommand.SetHandler(async (context) =>
        {
            var caseFolder = context.ParseResult.GetValueForArgument(caseFolderArgExport);
            var format = context.ParseResult.GetValueForOption(formatOpt)!;
            var outDir = context.ParseResult.GetValueForOption(outOptExport);
            var folder = context.ParseResult.GetValueForOption(folderOptExport);
            var limit = context.ParseResult.GetValueForOption(limitOptExport);
            var offset = context.ParseResult.GetValueForOption(offsetOptExport);
            var includeAttachments = context.ParseResult.GetValueForOption(includeAttachmentsOpt);
            var extractAttachments = context.ParseResult.GetValueForOption(extractAttachmentsOpt);
            var overwrite = context.ParseResult.GetValueForOption(overwriteOpt);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOpt);

            var exitCode = await HandleExportAsync(caseFolder, format, outDir, folder, limit, offset, includeAttachments, extractAttachments, overwrite, dryRun);
            context.ExitCode = exitCode;
        });

        // NEW Command: validate
        var validateCommand = new Command("validate", "Valida a integridade técnica e conformidade forense de uma exportação.");
        var caseFolderArgValidate = new Argument<DirectoryInfo>("case-folder", "A pasta de caso contendo o case.db.") { Arity = ArgumentArity.ExactlyOne };
        var exportFolderOpt = new Option<DirectoryInfo?>("--export-folder", "Opcional: Diretório contendo os e-mails exportados.");
        var formatOptValidate = new Option<string>("--format", () => "auto", "Formato de exportação: eml, mbox ou auto.");
        formatOptValidate.FromAmong("eml", "mbox", "auto");
        var jsonOpt = new Option<bool>("--json", () => false, "Se marcado, gera a saída técnica em JSON bruto.");
        var strictOpt = new Option<bool>("--strict", () => false, "Se ativado, qualquer warning estrutural gera falha técnica no status final.");
        var checkEmlParseOpt = new Option<bool>("--check-eml-parse", () => true, "Habilita o parseamento estrutural profundo de EMLs.");
        var checkMboxStructureOpt = new Option<bool>("--check-mbox-structure", () => true, "Habilita a auditoria de layout de arquivos MBOX.");
        var checkAttachmentsOpt = new Option<bool>("--check-attachments", () => true, "Habilita a validação cruzada física de anexos.");
        var sampleSizeOpt = new Option<int?>("--sample-size", "Opcional: Limita a amostragem física de arquivos validados.");
        var outOptValidate = new Option<DirectoryInfo?>("--out", "Diretório de saída para salvar o validation-report.json.");

        validateCommand.AddArgument(caseFolderArgValidate);
        validateCommand.AddOption(exportFolderOpt);
        validateCommand.AddOption(formatOptValidate);
        validateCommand.AddOption(jsonOpt);
        validateCommand.AddOption(strictOpt);
        validateCommand.AddOption(checkEmlParseOpt);
        validateCommand.AddOption(checkMboxStructureOpt);
        validateCommand.AddOption(checkAttachmentsOpt);
        validateCommand.AddOption(sampleSizeOpt);
        validateCommand.AddOption(outOptValidate);

        validateCommand.SetHandler(async (context) =>
        {
            var caseFolder = context.ParseResult.GetValueForArgument(caseFolderArgValidate);
            var exportFolder = context.ParseResult.GetValueForOption(exportFolderOpt);
            var format = context.ParseResult.GetValueForOption(formatOptValidate)!;
            var json = context.ParseResult.GetValueForOption(jsonOpt);
            var strict = context.ParseResult.GetValueForOption(strictOpt);
            var checkEml = context.ParseResult.GetValueForOption(checkEmlParseOpt);
            var checkMbox = context.ParseResult.GetValueForOption(checkMboxStructureOpt);
            var checkAtt = context.ParseResult.GetValueForOption(checkAttachmentsOpt);
            var sampleSize = context.ParseResult.GetValueForOption(sampleSizeOpt);
            var outDir = context.ParseResult.GetValueForOption(outOptValidate);

            var exitCode = await HandleValidateAsync(caseFolder, exportFolder, format, json, strict, checkEml, checkMbox, checkAtt, sampleSize, outDir);
            context.ExitCode = exitCode;
        });

        // NEW Command: corpus scan
        var corpusCommand = new Command("corpus", "Comandos auxiliares de gerência do laboratório de validação.");
        var scanCommand = new Command("scan", "Escaneia e indexa metadados agregados do corpus de teste local.");
        var corpusFolderArg = new Argument<DirectoryInfo>("corpus-folder", "O diretório do corpus de teste local.") { Arity = ArgumentArity.ExactlyOne };
        var outOptCorpus = new Option<DirectoryInfo?>("--out", "Diretório de saída para salvar o relatório do corpus.");

        scanCommand.AddArgument(corpusFolderArg);
        scanCommand.AddOption(outOptCorpus);
        scanCommand.SetHandler(async (DirectoryInfo corpusFolder, DirectoryInfo? outDir) =>
        {
            await HandleCorpusScanAsync(corpusFolder, outDir);
        }, corpusFolderArg, outOptCorpus);

        corpusCommand.AddCommand(scanCommand);

        // Command: index-worker
        var indexWorkerCommand = new Command("index-worker", "Comando interno isolado de processamento para indexação forense.");
        var jobOpt = new Option<FileInfo>("--job", "Caminho do arquivo job.json contendo as configurações da indexação.") { IsRequired = true };
        indexWorkerCommand.AddOption(jobOpt);
        indexWorkerCommand.SetHandler(async (FileInfo job) =>
        {
            await HandleIndexWorkerAsync(job);
        }, jobOpt);

        // Command: recover-eml — direct recovery from OST/PST/MBOX to individual .eml files
        var recoverEmlCommand = new Command("recover-eml", "Recupera e-mails diretamente de um OST/PST para arquivos .eml sem indexação prévia.");
        var fileArgRecoverEml = new Argument<FileInfo>("file", "Caminho do arquivo .ost ou .pst a recuperar.") { Arity = ArgumentArity.ExactlyOne };
        var outOptRecoverEml = new Option<string>("--out", "Diretório de saída para os arquivos .eml exportados.") { IsRequired = true };
        var folderOptRecoverEml = new Option<string?>("--folder", "Exportar apenas esta pasta (caminho lógico).");
        recoverEmlCommand.AddArgument(fileArgRecoverEml);
        recoverEmlCommand.AddOption(outOptRecoverEml);
        recoverEmlCommand.AddOption(folderOptRecoverEml);
        recoverEmlCommand.SetHandler(async (context) =>
        {
            var file = context.ParseResult.GetValueForArgument(fileArgRecoverEml);
            var outDir = context.ParseResult.GetValueForOption(outOptRecoverEml)!;
            var folder = context.ParseResult.GetValueForOption(folderOptRecoverEml);
            context.ExitCode = await HandleRecoverEmlAsync(file, outDir, folder);
        });

        // Command: recover-mbox — direct recovery from OST/PST to .mbox files
        var recoverMboxCommand = new Command("recover-mbox", "Recupera e-mails diretamente de um OST/PST para arquivos .mbox sem indexação prévia.");
        var fileArgRecoverMbox = new Argument<FileInfo>("file", "Caminho do arquivo .ost ou .pst a recuperar.") { Arity = ArgumentArity.ExactlyOne };
        var outOptRecoverMbox = new Option<string>("--out", "Diretório de saída para os arquivos .mbox exportados.") { IsRequired = true };
        var folderOptRecoverMbox = new Option<string?>("--folder", "Exportar apenas esta pasta (caminho lógico).");
        recoverMboxCommand.AddArgument(fileArgRecoverMbox);
        recoverMboxCommand.AddOption(outOptRecoverMbox);
        recoverMboxCommand.AddOption(folderOptRecoverMbox);
        recoverMboxCommand.SetHandler(async (context) =>
        {
            var file = context.ParseResult.GetValueForArgument(fileArgRecoverMbox);
            var outDir = context.ParseResult.GetValueForOption(outOptRecoverMbox)!;
            var folder = context.ParseResult.GetValueForOption(folderOptRecoverMbox);
            context.ExitCode = await HandleRecoverMboxAsync(file, outDir, folder);
        });

        rootCommand.AddCommand(inspectCommand);
        rootCommand.AddCommand(treeCommand);
        rootCommand.AddCommand(listCommand);
        rootCommand.AddCommand(previewCommand);
        rootCommand.AddCommand(indexCommand);
        rootCommand.AddCommand(statsCommand);
        rootCommand.AddCommand(searchCommand);
        rootCommand.AddCommand(exportCommand);
        rootCommand.AddCommand(validateCommand);
        rootCommand.AddCommand(corpusCommand);
        rootCommand.AddCommand(indexWorkerCommand);
        rootCommand.AddCommand(recoverEmlCommand);
        rootCommand.AddCommand(recoverMboxCommand);

        // Command: worker
        var workerCommand = new Command("worker", "Comando unificado isolado de processamento para operações forenses.");
        var workerJobOpt = new Option<FileInfo>("--job", "Caminho do arquivo job.json contendo as configurações do job.") { IsRequired = true };
        workerCommand.AddOption(workerJobOpt);
        workerCommand.SetHandler(async (FileInfo job) =>
        {
            await HandleWorkerJobAsync(job);
        }, workerJobOpt);

        rootCommand.AddCommand(workerCommand);

        int result = await rootCommand.InvokeAsync(args);
        return ExitCode != 0 ? ExitCode : result;
    }

    private static IMailStoreReader GetMailStoreReader(string extension)
    {
        if (_injectedReader != null)
        {
            return _injectedReader;
        }

        var resolver = new ReflectionAdapterResolver();
        var result = resolver.ResolveAdapter(extension);
        if (result.Success && result.Reader != null)
        {
            return result.Reader;
        }

        throw new InvalidOperationException(result.ErrorMessage ?? "Erro desconhecido ao carregar dinamicamente o adapter.");
    }

    private static async Task<(string sha256, string caseId, string caseFolderPath, string auditLogFilePath, DateTimeOffset startedAt)> InitializeCaseAsync(FileInfo file, DirectoryInfo outDir, string commandName, string? explicitCaseId = null)
    {
        var startedAt = DateTimeOffset.Now;
        var caseId = explicitCaseId ?? ManifestService.GenerateCaseId(startedAt);
        string caseFolderPath = Path.Combine(outDir.FullName, caseId);
        string auditLogFilePath = Path.Combine(caseFolderPath, "audit.log");

        var hashService = new HashService();
        var progressReporter = new ConsoleProgressReporter();

        Console.WriteLine($"[*] Caso Inicializado: {caseId}");
        Console.WriteLine($"[*] Operador: {Environment.UserName}");
        Console.WriteLine($"[*] Arquivo: {file.FullName}");
        Console.WriteLine($"[*] Comando Executado: {commandName}");
        Console.WriteLine("[*] Calculando hash de integridade (SHA-256 por streaming)...");

        string sha256 = await hashService.CalculateSha256Async(file.FullName, progressReporter, CancellationToken.None);
        Console.WriteLine($"[*] SHA-256: {sha256}");
        Console.WriteLine();

        var auditWriter = new FileAuditTrailWriter(auditLogFilePath);
        await auditWriter.WriteEventAsync(new AuditEvent(
            EventId: Guid.NewGuid().ToString(),
            Timestamp: DateTimeOffset.Now,
            Action: "CommandStarted",
            OperatorName: Environment.UserName,
            Details: $"Comando {commandName} iniciado. Hash calculado: {sha256}."
        ), CancellationToken.None);

        return (sha256, caseId, caseFolderPath, auditLogFilePath, startedAt);
    }

    private static async Task CloseCaseAsync(string auditLogFilePath, string caseId, string sourceFile, long sourceSize, string sha256, DateTimeOffset startedAt, string commandName, List<ExtractionIssue> warnings)
    {
        var auditWriter = new FileAuditTrailWriter(auditLogFilePath);
        await auditWriter.WriteEventAsync(new AuditEvent(
            EventId: Guid.NewGuid().ToString(),
            Timestamp: DateTimeOffset.Now,
            Action: "CommandCompleted",
            OperatorName: Environment.UserName,
            Details: $"Comando {commandName} finalizado com sucesso."
        ), CancellationToken.None);

        var manifest = new RecoveryManifest(
            CaseId: caseId,
            SourceFile: sourceFile,
            SourceSizeBytes: sourceSize,
            SourceSha256: sha256,
            OperatorName: Environment.UserName,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.Now,
            ToolVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0",
            Actions: new List<string> { $"Executed command: {commandName}" },
            Warnings: warnings
        );

        string outDirBase = Path.GetDirectoryName(Path.GetDirectoryName(auditLogFilePath))!;
        await ManifestService.SaveManifestAsync(outDirBase, manifest, CancellationToken.None);
    }

    private static async Task HandleInspectAsync(FileInfo file, DirectoryInfo outDir)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                  MailVault Recovery — Inspeção Técnica de Mídia                ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        if (!file.Exists)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] O arquivo especificado não existe: '{file.FullName}'");
            Console.ResetColor();
            ExitCode = 1;
            return;
        }

        var startedAt = DateTimeOffset.Now;
        var caseId = ManifestService.GenerateCaseId(startedAt);
        string caseFolderPath = Path.Combine(outDir.FullName, caseId);
        string auditLogFilePath = Path.Combine(caseFolderPath, "audit.log");

        Console.WriteLine($"[*] Caso Inicializado: {caseId}");
        Console.WriteLine($"[*] Operador: {Environment.UserName}");
        Console.WriteLine($"[*] Arquivo: {file.FullName}");
        Console.WriteLine($"[*] Tamanho: {file.Length:N0} bytes");
        Console.WriteLine();

        var progressReporter = new ConsoleProgressReporter();
        var hashService = new HashService();
        var auditWriter = new FileAuditTrailWriter(auditLogFilePath);

        Console.WriteLine("[*] Iniciando cálculo de hash de integridade (SHA-256 por streaming)...");
        string sha256 = string.Empty;
        try
        {
            sha256 = await hashService.CalculateSha256Async(file.FullName, progressReporter, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] Falha ao calcular hash SHA-256: {ex.Message}");
            Console.ResetColor();
            ExitCode = 2;
            return;
        }

        Console.WriteLine();

        string extension = file.Extension.ToLowerInvariant();
        string preliminaryStatus = "Aprovado para Processamento";
        var warnings = new List<ExtractionIssue>();

        if (file.Length == 0)
        {
            preliminaryStatus = "Atenção: Arquivo Vazio";
            warnings.Add(new ExtractionIssue(
                Code: "MV-WARN-001",
                Severity: "Warning",
                Message: "O arquivo de origem possui tamanho de zero bytes.",
                ObjectId: file.Name,
                TechnicalDetails: "Tamanho de arquivo é 0."
            ));
        }

        if (extension != ".ost" && extension != ".pst")
        {
            preliminaryStatus = "Atenção: Extensão Não Padrão";
            warnings.Add(new ExtractionIssue(
                Code: "MV-WARN-002",
                Severity: "Warning",
                Message: $"A extensão do arquivo '{extension}' não é a padrão .ost ou .pst.",
                ObjectId: file.Name,
                TechnicalDetails: $"Extensão '{extension}' não reconhecida nativamente."
            ));
        }

        var manifest = new RecoveryManifest(
            CaseId: caseId,
            SourceFile: file.FullName,
            SourceSizeBytes: file.Length,
            SourceSha256: sha256,
            OperatorName: Environment.UserName,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.Now,
            ToolVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0",
            Actions: new List<string> { $"Inspected file: {file.Name}", "Generated integrity SHA-256 hash" },
            Warnings: warnings
        );

        string manifestPath = string.Empty;
        try
        {
            manifestPath = await ManifestService.SaveManifestAsync(outDir.FullName, manifest, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] Falha ao salvar manifest.json: {ex.Message}");
            Console.ResetColor();
            ExitCode = 3;
            return;
        }

        var auditEvent = new AuditEvent(
            EventId: Guid.NewGuid().ToString(),
            Timestamp: DateTimeOffset.Now,
            Action: "FILE_INSPECTED",
            OperatorName: Environment.UserName,
            Details: $"Arquivo inspecionado com sucesso. Hash gerado: {sha256}. Pasta do caso criada.",
            FilePath: file.FullName,
            FileHash: sha256
        );

        try
        {
            await auditWriter.WriteEventAsync(auditEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[AVISO] Falha ao gravar trilha de auditoria: {ex.Message}");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                       RELATÓRIO TÉCNICO DE INSPEÇÃO                            ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();
        Console.WriteLine($"Caminho do arquivo  : {file.FullName}");
        Console.WriteLine($"Nome do arquivo     : {file.Name}");
        Console.WriteLine($"Extensão            : {extension}");
        Console.WriteLine($"Tamanho (bytes)     : {file.Length:N0}");
        Console.WriteLine($"SHA-256 (streaming) : {sha256}");
        Console.WriteLine($"Data/Hora Inspeção  : {startedAt:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine($"Status Preliminar   : {preliminaryStatus}");

        if (warnings.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Avisos:");
            foreach (var warn in warnings)
            {
                Console.WriteLine($"  - [{warn.Code}] [{warn.Severity}] {warn.Message}");
            }
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine("Avisos              : Nenhum aviso ou problema detectado.");
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"[*] Manifesto salvo com sucesso em:");
        Console.WriteLine($"    {manifestPath}");
        Console.WriteLine($"[*] Trilha de auditoria salva em:");
        Console.WriteLine($"    {auditLogFilePath}");
        Console.WriteLine("================================================================================");
        Console.ResetColor();
    }

    private static async Task HandleTreeAsync(FileInfo file, DirectoryInfo outDir, int maxDepth)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                  MailVault Recovery — Árvore de Pastas                         ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        if (!file.Exists)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] O arquivo especificado não existe: '{file.FullName}'");
            Console.ResetColor();
            ExitCode = 1;
            return;
        }

        var caseDetails = await InitializeCaseAsync(file, outDir, "tree");
        var auditWriter = new FileAuditTrailWriter(caseDetails.auditLogFilePath);

        var reader = GetMailStoreReader(file.Extension);
        var warnings = new List<ExtractionIssue>();

        Console.WriteLine("[*] Abrindo arquivo e varrendo hierarquia de pastas...");
        await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "StoreOpened", Environment.UserName, "Arquivo aberto para mapeamento."), CancellationToken.None);

        try
        {
            await reader.InspectAsync(file.FullName, CancellationToken.None);

            var roots = new List<FolderNode>();
            await foreach (var rootFolder in reader.EnumerateFoldersAsync(CancellationToken.None))
            {
                roots.Add(rootFolder);
            }

            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "FoldersEnumerated", Environment.UserName, $"Mapeadas {roots.Count} pastas raiz."), CancellationToken.None);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Estrutura Hierárquica de Pastas:");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ResetColor();

            int totalFolders = 0;
            for (int i = 0; i < roots.Count; i++)
            {
                PrintTreeFolder(roots[i], "", i == roots.Count - 1, 1, maxDepth, ref totalFolders);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ResetColor();
            Console.WriteLine($"[x] Varredura Concluída.");
            Console.WriteLine($"[*] Total de Pastas: {totalFolders}");

            await CloseCaseAsync(caseDetails.auditLogFilePath, caseDetails.caseId, file.FullName, file.Length, caseDetails.sha256, caseDetails.startedAt, "tree", warnings);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[*] Manifesto e trilha gravados em: {caseDetails.caseFolderPath}");
            Console.WriteLine("================================================================================");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[CRITICAL ERRO] Falha ao processar árvore: {ex.Message}");
            Console.ResetColor();
            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "CommandFailed", Environment.UserName, $"FALHA DO COMANDO tree: {ex}"), CancellationToken.None);
            ExitCode = 4;
        }
    }

    private static void PrintTreeFolder(FolderNode folder, string indent, bool isLast, int currentDepth, int maxDepth, ref int totalFolders)
    {
        if (currentDepth > maxDepth) return;

        totalFolders++;
        Console.Write(indent);
        Console.Write(isLast ? "└── " : "├── ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write(folder.DisplayName);
        Console.ResetColor();
        Console.WriteLine($" (Mensagens: {folder.MessageCount ?? 0})");

        string childIndent = indent + (isLast ? "    " : "│   ");
        for (int i = 0; i < folder.Children.Count; i++)
        {
            PrintTreeFolder(folder.Children[i], childIndent, i == folder.Children.Count - 1, currentDepth + 1, maxDepth, ref totalFolders);
        }
    }

    private static async Task HandleListAsync(FileInfo file, string folder, int limit, int offset, DirectoryInfo outDir)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                  MailVault Recovery — Listagem de Mensagens                    ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        if (!file.Exists)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] O arquivo especificado não existe: '{file.FullName}'");
            Console.ResetColor();
            ExitCode = 1;
            return;
        }

        var caseDetails = await InitializeCaseAsync(file, outDir, "list");
        var auditWriter = new FileAuditTrailWriter(caseDetails.auditLogFilePath);

        var reader = GetMailStoreReader(file.Extension);
        var warnings = new List<ExtractionIssue>();

        Console.WriteLine($"[*] Buscando mensagens na pasta: '{folder}'...");
        await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "StoreOpened", Environment.UserName, "Arquivo aberto para listagem de mensagens."), CancellationToken.None);

        try
        {
            await reader.InspectAsync(file.FullName, CancellationToken.None);

            var messages = new List<MailItem>();
            int idx = 0;

            await foreach (var msg in reader.EnumerateMessagesAsync(new FolderId(folder), CancellationToken.None))
            {
                if (idx >= offset && messages.Count < limit)
                {
                    messages.Add(msg);
                }
                idx++;
            }

            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "MessagesEnumerated", Environment.UserName, $"Listadas {messages.Count} mensagens na pasta {folder}."), CancellationToken.None);

            Console.WriteLine($"[*] Encontradas {idx} mensagens no total. Exibindo {messages.Count} itens (limit={limit}, offset={offset}):");
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("   ID INTERNO   |      DATA      |      REMETENTE      | ASSUNTO");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ResetColor();

            foreach (var m in messages)
            {
                string dateStr = m.ReceivedAt?.ToString("yyyy-MM-dd HH:mm") ?? m.SentAt?.ToString("yyyy-MM-dd HH:mm") ?? "N/A";
                string fromStr = m.From?.Name ?? m.From?.Address ?? "Remetente Desconhecido";
                if (fromStr.Length > 25) fromStr = fromStr.Substring(0, 22) + "...";
                string subStr = m.Subject ?? "(Sem Assunto)";
                if (subStr.Length > 30) subStr = subStr.Substring(0, 27) + "...";

                string idStr = m.InternalId;
                if (idStr.Length > 15) idStr = idStr.Substring(idStr.Length - 15);

                Console.WriteLine($"{idStr.PadRight(15)} | {dateStr.PadRight(14)} | {fromStr.PadRight(25)} | {subStr} | anexos: {m.Attachments.Count}");

                if (m.Issues.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    foreach (var issue in m.Issues)
                    {
                        Console.WriteLine($"   --> ALERTA: [{issue.Code}] {issue.Message}");
                    }
                    Console.ResetColor();
                }
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ResetColor();

            await CloseCaseAsync(caseDetails.auditLogFilePath, caseDetails.caseId, file.FullName, file.Length, caseDetails.sha256, caseDetails.startedAt, "list", warnings);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[*] Manifesto e trilha gravados em: {caseDetails.caseFolderPath}");
            Console.WriteLine("================================================================================");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[CRITICAL ERRO] Falha ao listar mensagens: {ex.Message}");
            Console.ResetColor();
            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "CommandFailed", Environment.UserName, $"FALHA DO COMANDO list: {ex}"), CancellationToken.None);
            ExitCode = 5;
        }
    }

    private static async Task HandlePreviewAsync(FileInfo file, string messageId, int bodyLines, DirectoryInfo outDir)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                  MailVault Recovery — Visualização Segura                      ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        if (!file.Exists)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] O arquivo especificado não existe: '{file.FullName}'");
            Console.ResetColor();
            ExitCode = 1;
            return;
        }

        var caseDetails = await InitializeCaseAsync(file, outDir, "preview");
        var auditWriter = new FileAuditTrailWriter(caseDetails.auditLogFilePath);

        var reader = GetMailStoreReader(file.Extension);
        var warnings = new List<ExtractionIssue>();

        Console.WriteLine($"[*] Buscando detalhes da mensagem ID: '{messageId}'...");
        await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "StoreOpened", Environment.UserName, "Arquivo aberto para preview de mensagem."), CancellationToken.None);

        try
        {
            await reader.InspectAsync(file.FullName, CancellationToken.None);
            var result = await reader.GetMessageAsync(new MessageId(messageId), CancellationToken.None);

            if (!result.Success || result.Value == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ERRO] Não foi possível obter a mensagem:");
                foreach (var issue in result.Issues)
                {
                    Console.WriteLine($"  - [{issue.Code}] [{issue.Severity}] {issue.Message}");
                    warnings.Add(issue);
                }
                Console.ResetColor();
                await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "IssuesDetected", Environment.UserName, $"Falha ao abrir mensagem: {result.Issues.FirstOrDefault()?.Message}"), CancellationToken.None);
                await CloseCaseAsync(caseDetails.auditLogFilePath, caseDetails.caseId, file.FullName, file.Length, caseDetails.sha256, caseDetails.startedAt, "preview", warnings);
                ExitCode = 6;
                return;
            }

            var m = result.Value;
            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "MessagePreviewed", Environment.UserName, $"Preview da mensagem {messageId} carregado."), CancellationToken.None);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("CABEÇALHOS E DADOS:");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ResetColor();
            Console.WriteLine($"Internal ID   : {m.InternalId}");
            Console.WriteLine($"Message ID    : {m.InternetMessageId ?? "N/A"}");
            Console.WriteLine($"Assunto       : {m.Subject ?? "(Sem Assunto)"}");
            Console.WriteLine($"Remetente     : {FormatAddress(m.From)}");
            Console.WriteLine($"Para          : {string.Join("; ", m.To.Select(FormatAddress))}");
            Console.WriteLine($"Cc            : {string.Join("; ", m.Cc.Select(FormatAddress))}");
            Console.WriteLine($"Bcc           : {string.Join("; ", m.Bcc.Select(FormatAddress))}");
            Console.WriteLine($"Data Envio    : {m.SentAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "N/A"}");
            Console.WriteLine($"Data Recepção : {m.ReceivedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "N/A"}");
            Console.WriteLine($"Possui HTML   : {(string.IsNullOrEmpty(m.HtmlBody) ? "Não" : "Sim")}");
            Console.WriteLine($"Possui Texto  : {(string.IsNullOrEmpty(m.PlainTextBody) ? "Não" : "Sim")}");
            Console.WriteLine($"MAPI Props    : {m.RawProperties.Count} propriedades indexadas.");

            if (m.Attachments.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nANEXOS:");
                Console.WriteLine("--------------------------------------------------------------------------------");
                Console.ResetColor();
                foreach (var att in m.Attachments)
                {
                    Console.WriteLine($"  - Anexo ID    : {att.InternalId}");
                    Console.WriteLine($"    Nome        : {att.FileName ?? "(Sem Nome)"}");
                    Console.WriteLine($"    Tamanho     : {att.SizeBytes?.ToString("N0") ?? "Desconhecido"} bytes");
                    Console.WriteLine($"    Inline      : {(att.IsInline ? "Sim" : "Não")}");
                }
            }

            if (m.Issues.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\nALERTAS/ISSUES DA MENSAGEM:");
                Console.WriteLine("--------------------------------------------------------------------------------");
                Console.ResetColor();
                foreach (var isue in m.Issues)
                {
                    Console.WriteLine($"  * [{isue.Code}] [{isue.Severity}] {isue.Message}");
                    warnings.Add(isue);
                }
            }

            string? bodyToTruncate = !string.IsNullOrEmpty(m.PlainTextBody) ? m.PlainTextBody : m.HtmlBody;
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PREVIEW DO CORPO DA MENSAGEM (Truncado em no máximo {bodyLines} linhas):");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ResetColor();

            if (string.IsNullOrEmpty(bodyToTruncate))
            {
                Console.WriteLine("(Corpo da mensagem vazio ou não decodificado)");
            }
            else
            {
                var lines = bodyToTruncate.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                int linesToPrint = Math.Min(lines.Length, bodyLines);

                for (int i = 0; i < linesToPrint; i++)
                {
                    Console.WriteLine(lines[i]);
                }

                if (lines.Length > bodyLines)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n[... TEXTO TRUNCADO SEGURAMENTE PARA COMPLIANCE FORENSE - {lines.Length - bodyLines} LINHAS OCULTAS ...]");
                    Console.ResetColor();
                }
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ResetColor();

            await CloseCaseAsync(caseDetails.auditLogFilePath, caseDetails.caseId, file.FullName, file.Length, caseDetails.sha256, caseDetails.startedAt, "preview", warnings);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[*] Manifesto e trilha gravados em: {caseDetails.caseFolderPath}");
            Console.WriteLine("================================================================================");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[CRITICAL ERRO] Falha no preview: {ex.Message}");
            Console.ResetColor();
            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "CommandFailed", Environment.UserName, $"FALHA DO COMANDO preview: {ex}"), CancellationToken.None);
            ExitCode = 7;
        }
    }

    private static async Task<int> HandleIndexAsync(FileInfo file, DirectoryInfo outDir, string? explicitCaseId, bool force, int? limit, bool noPreviewCache)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                  MailVault Recovery — Indexador Persistente                    ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        if (!file.Exists)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] O arquivo especificado não existe: '{file.FullName}'");
            Console.ResetColor();
            ExitCode = 1;
            return 1;
        }

        var caseDetails = await InitializeCaseAsync(file, outDir, "index", explicitCaseId);
        var auditWriter = new FileAuditTrailWriter(caseDetails.auditLogFilePath);

        Console.WriteLine("[*] Criando banco de dados do caso e abrindo canal de persistência...");
        await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "IndexDatabaseCreated", Environment.UserName, "Banco de dados case.db inicializado e verificado."), CancellationToken.None);

        try
        {
            using var store = new SqliteCaseIndexStore();
            string dbPath = Path.Combine(caseDetails.caseFolderPath, "case.db");

            if (File.Exists(dbPath) && force)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[*] Opção --force ativa: removendo índice anterior...");
                Console.ResetColor();
                File.Delete(dbPath);
            }

            await store.InitializeAsync(caseDetails.caseFolderPath, CancellationToken.None);

            var reader = GetMailStoreReader(file.Extension);
            var indexingService = new IndexingService();

            Console.WriteLine("[*] Iniciando indexação recursiva (pipeline de normalização ativo)...");
            var result = await indexingService.RunIndexAsync(
                file.FullName,
                store,
                reader,
                caseDetails.caseId,
                Environment.UserName,
                !noPreviewCache,
                limit,
                CancellationToken.None
            );

            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "FoldersIndexed", Environment.UserName, $"Indexadas {result.FoldersIndexed} pastas."), CancellationToken.None);
            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "MessagesIndexed", Environment.UserName, $"Indexadas {result.MessagesIndexed} mensagens."), CancellationToken.None);

            if (!result.Status.Equals("Success", StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("================================================================================");
                Console.WriteLine("                  INDEXAÇÃO FINALIZADA COM FALHA CONTROLADA                     ");
                Console.WriteLine("================================================================================");
                Console.ResetColor();
                Console.WriteLine($"Arquivo              : {MaskPathForConsole(file.FullName)}");
                Console.WriteLine("Etapa                : indexação recursiva");
                Console.WriteLine($"Status               : {result.Status}");
                Console.WriteLine($"Caminho do Caso      : {caseDetails.caseFolderPath}");
                Console.WriteLine($"Caminho do Banco     : {result.DbPath}");
                Console.WriteLine($"Pastas Persistidas   : {result.FoldersIndexed}");
                Console.WriteLine($"Mensagens Persistidas: {result.MessagesIndexed}");
                Console.WriteLine($"Anexos Persistidos   : {result.AttachmentsIndexed}");
                Console.WriteLine($"Issues Registradas   : {result.IssuesDetected}");
                Console.WriteLine($"Mensagem             : {SanitizeForConsole(result.ErrorMessage ?? "A indexação terminou sem sucesso completo.")}");
                Console.WriteLine("Possível causa       : falha estrutural do arquivo, pasta/mensagem corrompida ou limitação do XstReader.");
                Console.WriteLine($"Ação recomendada     : use mailvault stats \"{caseDetails.caseFolderPath}\" para verificar progresso parcial.");
                Console.WriteLine($"Auditoria            : consulte {caseDetails.auditLogFilePath}");
                Console.WriteLine("================================================================================");

                await auditWriter.WriteEventAsync(new AuditEvent(
                    Guid.NewGuid().ToString(),
                    DateTimeOffset.Now,
                    "IndexFailedControlled",
                    Environment.UserName,
                    $"Indexação finalizada com status {result.Status}. Case={caseDetails.caseId}; Pastas={result.FoldersIndexed}; Mensagens={result.MessagesIndexed}; Issues={result.IssuesDetected}; Motivo={SanitizeForConsole(result.ErrorMessage ?? "sem mensagem adicional")}"),
                    CancellationToken.None);

                ExitCode = 8;
                return 8;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("================================================================================");
            Console.WriteLine("                         RELATÓRIO DE INDEXAÇÃO                                 ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();
            Console.WriteLine($"Caminho do Caso       : {caseDetails.caseFolderPath}");
            Console.WriteLine($"Caminho do Banco (db) : {result.DbPath}");
            Console.WriteLine($"Pastas Indexadas      : {result.FoldersIndexed}");
            Console.WriteLine($"Mensagens Indexadas   : {result.MessagesIndexed}");
            Console.WriteLine($"Anexos Indexados      : {result.AttachmentsIndexed}");
            Console.WriteLine($"Alertas / Issues      : {result.IssuesDetected}");
            Console.WriteLine($"Duração total         : {result.DurationMs} ms");
            Console.WriteLine($"Integridade (SHA-256) : {result.Sha256}");
            Console.WriteLine("================================================================================");

            await CloseCaseAsync(caseDetails.auditLogFilePath, caseDetails.caseId, file.FullName, file.Length, caseDetails.sha256, caseDetails.startedAt, "index", new List<ExtractionIssue>());

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[*] Manifesto e logs de auditoria gravados perfeitamente.");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ERRO CONTROLADO] Falha na indexação técnica.");
            Console.ResetColor();
            Console.WriteLine($"Arquivo              : {MaskPathForConsole(file.FullName)}");
            Console.WriteLine("Etapa                : preparação/indexação");
            Console.WriteLine($"Mensagem             : {SanitizeForConsole(ex.Message)}");
            Console.WriteLine("Possível causa       : falha estrutural do arquivo, adapter indisponível ou erro de persistência.");
            Console.WriteLine($"Caminho do Caso      : {caseDetails.caseFolderPath}");
            Console.WriteLine($"Ação recomendada     : consulte audit.log em {caseDetails.auditLogFilePath}");

            await auditWriter.WriteEventAsync(new AuditEvent(
                Guid.NewGuid().ToString(),
                DateTimeOffset.Now,
                "CommandFailed",
                Environment.UserName,
                $"FALHA CONTROLADA DO COMANDO index: {ex.GetType().Name}: {SanitizeForConsole(ex.Message)}"),
                CancellationToken.None);
            ExitCode = 8;
            return 8;
        }
    }

    private static async Task<int> HandleStatsAsync(DirectoryInfo caseFolder)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                  MailVault Recovery — Estatísticas do Caso                     ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        string dbPath = Path.Combine(caseFolder.FullName, "case.db");
        if (!File.Exists(dbPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] O banco de dados indexado case.db não foi encontrado em: '{caseFolder.FullName}'");
            Console.ResetColor();
            ExitCode = 9;
            return 9;
        }

        try
        {
            using var store = new SqliteCaseIndexStore();
            await store.InitializeAsync(caseFolder.FullName, CancellationToken.None);

            using var reader = store.CreateReader();

            int foldersCount = await reader.GetFolderCountAsync(CancellationToken.None);
            int messagesCount = await reader.GetMessageCountAsync(CancellationToken.None);
            int attachmentsCount = await reader.GetAttachmentCountAsync(CancellationToken.None);
            int issuesCount = await reader.GetIssueCountAsync(CancellationToken.None);
            long totalAttachSize = await reader.GetTotalAttachmentSizeAsync(CancellationToken.None);
            var largestAttach = await reader.GetLargestAttachmentAsync(CancellationToken.None);
            var topFolders = await reader.GetTopFoldersByMessageCountAsync(5, CancellationToken.None);

            Console.WriteLine($"Caminho da Sessão : {caseFolder.FullName}");
            Console.WriteLine($"Arquivo de Banco  : {dbPath}");
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("MÉTRICAS GERAIS:");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ResetColor();
            Console.WriteLine($"Pastas Encontradas    : {foldersCount}");
            Console.WriteLine($"Mensagens Indexadas   : {messagesCount}");
            Console.WriteLine($"Anexos Localizados    : {attachmentsCount}");
            Console.WriteLine($"Tamanho Total Anexos  : {totalAttachSize:N0} bytes ({(double)totalAttachSize / (1024 * 1024):F2} MB)");
            Console.WriteLine($"Maior Anexo Detectado : {largestAttach.fileName} ({(double)largestAttach.sizeBytes / (1024 * 1024):F2} MB)");
            Console.WriteLine($"Alertas / Issues      : {issuesCount}");
            Console.WriteLine();

            if (topFolders.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASTAS COM MAIS MENSAGENS:");
                Console.WriteLine("--------------------------------------------------------------------------------");
                Console.ResetColor();
                int idx = 1;
                foreach (var pair in topFolders)
                {
                    Console.WriteLine($"{idx}. {pair.Key} — {pair.Value} e-mails");
                    idx++;
                }
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[CRITICAL ERRO] Falha ao computar estatísticas: {ex.Message}");
            Console.ResetColor();
            ExitCode = 10;
            return 10;
        }
    }

    private static async Task<int> HandleSearchAsync(DirectoryInfo caseFolder, string queryText, string? folderPath, int limit, int offset, bool includePreview)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                  MailVault Recovery — Pesquisa do Caso                         ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        string dbPath = Path.Combine(caseFolder.FullName, "case.db");
        if (!File.Exists(dbPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] O arquivo case.db não foi encontrado na pasta: '{caseFolder.FullName}'");
            Console.ResetColor();
            ExitCode = 11;
            return 11;
        }

        Console.WriteLine($"[*] Consultando índice pela query: '{queryText}'...");
        if (!string.IsNullOrEmpty(folderPath))
        {
            Console.WriteLine($"[*] Filtro de pasta ativo: '{folderPath}'");
        }
        Console.WriteLine();

        try
        {
            using var store = new SqliteCaseIndexStore();
            await store.InitializeAsync(caseFolder.FullName, CancellationToken.None);

            using var reader = store.CreateReader();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("   ID INTERNO   |      DATA      |      REMETENTE      | ASSUNTO");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ResetColor();

            int matchesCount = 0;
            await foreach (var m in reader.SearchMessagesAsync(queryText, folderPath, limit, offset, CancellationToken.None))
            {
                matchesCount++;
                string dateStr = m.ReceivedAt?.ToString("yyyy-MM-dd HH:mm") ?? m.SentAt?.ToString("yyyy-MM-dd HH:mm") ?? "N/A";
                string fromStr = m.From?.Name ?? m.From?.Address ?? "Remetente Desconhecido";
                if (fromStr.Length > 25) fromStr = fromStr.Substring(0, 22) + "...";
                string subStr = m.Subject ?? "(Sem Assunto)";
                if (subStr.Length > 30) subStr = subStr.Substring(0, 27) + "...";

                string idStr = m.InternalId;
                if (idStr.Length > 15) idStr = idStr.Substring(idStr.Length - 15);

                Console.WriteLine($"{idStr.PadRight(15)} | {dateStr.PadRight(14)} | {fromStr.PadRight(25)} | {subStr} | anexos: {m.Attachments.Count}");

                if (includePreview && !string.IsNullOrEmpty(m.PlainTextBody))
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("   >>> PREVIEW:");
                    var lines = m.PlainTextBody.Split('\n');
                    foreach (var line in lines.Take(5))
                    {
                        Console.WriteLine($"     {line}");
                    }
                    if (lines.Length > 5)
                    {
                        Console.WriteLine("     [... preview truncado para legibilidade ...]");
                    }
                    Console.ResetColor();
                }
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ResetColor();
            Console.WriteLine($"[x] Busca finalizada. Exibidos {matchesCount} e-mails correspondentes.");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[CRITICAL ERRO] Falha ao executar pesquisa: {ex.Message}");
            Console.ResetColor();
            ExitCode = 12;
            return 12;
        }
    }

    private static async Task<int> HandleExportAsync(
        DirectoryInfo caseFolder,
        string format,
        DirectoryInfo? outDir,
        string? folder,
        int? limit,
        int? offset,
        bool includeAttachments,
        bool extractAttachments,
        bool overwrite,
        bool dryRun)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                  MailVault Recovery — Motor de Exportação                      ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        string dbPath = Path.Combine(caseFolder.FullName, "case.db");
        if (!File.Exists(dbPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] O banco de dados do caso 'case.db' não foi encontrado em: '{caseFolder.FullName}'");
            Console.ResetColor();
            ExitCode = 11;
            return 11;
        }

        string auditLogFilePath = Path.Combine(caseFolder.FullName, "audit.log");
        var auditWriter = new FileAuditTrailWriter(auditLogFilePath);

        await auditWriter.WriteEventAsync(new AuditEvent(
            EventId: Guid.NewGuid().ToString(),
            Timestamp: DateTimeOffset.Now,
            Action: "ExportCommandStarted",
            OperatorName: Environment.UserName,
            Details: $"Exportação iniciada para formato {format.ToUpperInvariant()}."
        ), CancellationToken.None);

        try
        {
            string finalOutputDir = outDir?.FullName ?? Path.Combine(caseFolder.FullName, "exports");

            using var store = new SqliteCaseIndexStore();
            await store.InitializeAsync(caseFolder.FullName, CancellationToken.None);
            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "CaseIndexOpened", Environment.UserName, "Conexão aberta com o banco case.db do caso."), CancellationToken.None);

            using var caseReader = store.CreateReader();
            var caseInfo = await caseReader.GetCaseInfoAsync(CancellationToken.None);
            if (caseInfo == null)
            {
                throw new InvalidOperationException("Metadados do caso estão ausentes da persistência.");
            }

            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "SourceFileVerified", Environment.UserName, $"Evidência original verificada em: {caseInfo.SourceFile}"), CancellationToken.None);

            var adapterResolver = new ReflectionAdapterResolver();
            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "AdapterResolved", Environment.UserName, "Resolvedor dinâmico de assemblies de adaptadores inicializado."), CancellationToken.None);

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
                CaseFolder: caseFolder.FullName,
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

            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "StoreOpenedReadOnly", Environment.UserName, "Sessão física da evidência original aberta em modo Read-Only sob custódia forense."), CancellationToken.None);
            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "ExportStarted", Environment.UserName, $"Iniciando execução do motor de exportação ({format.ToUpperInvariant()}). DryRun={dryRun}."), CancellationToken.None);

            Console.WriteLine($"[*] Pasta do Caso     : {caseFolder.FullName}");
            Console.WriteLine($"[*] Formato de Saída  : {format.ToUpperInvariant()}");
            Console.WriteLine($"[*] Diretorio Alvo    : {finalOutputDir}");
            Console.WriteLine($"[*] Dry Run (Simular) : {dryRun}");
            Console.WriteLine();

            var progress = new ConsoleProgressReporter();
            var result = await exportRunner.RunExportJobAsync(options, caseReader, adapterResolver, innerExporter, progress, CancellationToken.None);

            if (dryRun)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("================================================================================");
                Console.WriteLine("                  RELATÓRIO DE SIMULAÇÃO (DRY RUN)                             ");
                Console.WriteLine("================================================================================");
                Console.ResetColor();
                Console.WriteLine($"Pastas Selecionadas   : {result.FoldersSelected}");
                Console.WriteLine($"Mensagens no Escopo   : {result.MessagesSelected}");
                Console.WriteLine($"Nenhum arquivo físico foi gravado na simulação.");
                Console.WriteLine("================================================================================");
                
                await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "ExportCompleted", Environment.UserName, "Simulação de exportação finalizada com sucesso (Dry Run)."), CancellationToken.None);
                return 0;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("================================================================================");
            Console.WriteLine("                         RELATÓRIO DE EXPORTAÇÃO                                ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();
            Console.WriteLine($"Caminho de Saída      : {finalOutputDir}");
            Console.WriteLine($"Pastas Selecionadas   : {result.FoldersSelected}");
            Console.WriteLine($"Mensagens Exportadas  : {result.MessagesExported}");
            Console.WriteLine($"Mensagens com Falha   : {result.MessagesFailed}");
            Console.WriteLine($"Anexos Exportados     : {result.AttachmentsExported}");
            Console.WriteLine($"Anexos com Falha      : {result.AttachmentsFailed}");
            Console.WriteLine($"Duração total         : {result.DurationMs} ms");
            Console.WriteLine($"Manifesto export-manifest.json gerado perfeitamente.");
            Console.WriteLine("================================================================================");

            foreach (var issue in result.Issues)
            {
                string shortMsg = issue.Message.Length > 80 ? issue.Message.Substring(0, 77) + "..." : issue.Message;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[!] ALERTA ({issue.Code}): {shortMsg} (ID: {issue.ObjectId})");
                Console.ResetColor();
            }

            if (result.MessagesFailed > 0)
            {
                await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "MessageExportFailed", Environment.UserName, $"Exportação finalizada com {result.MessagesFailed} falhas de mensagens."), CancellationToken.None);
            }
            else
            {
                await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "ExportCompleted", Environment.UserName, $"Exportação forense finalizada perfeitamente. Exportadas {result.MessagesExported} mensagens."), CancellationToken.None);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[CRITICAL ERRO] Falha técnica no motor de exportação: {ex.Message}");
            Console.ResetColor();
            
            await auditWriter.WriteEventAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString(),
                Timestamp: DateTimeOffset.Now,
                Action: "ExportFailed",
                OperatorName: Environment.UserName,
                Details: $"FALHA DO COMANDO export: {ex.Message}"
            ), CancellationToken.None);

            ExitCode = 13;
            return 13;
        }
    }

    private static string MaskPathForConsole(string path)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.Replace(
                path,
                @"(?i)([a-z]:\\users\\)[^\\]+",
                "$1<USER>");
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }

    private static string SanitizeForConsole(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "sem detalhes";
        }

        string sanitized = message.Replace('\r', ' ').Replace('\n', ' ');
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",
            "<email_masked>");
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"(?i)([a-z]:\\users\\)[^\\]+",
            "$1<USER>");

        return sanitized.Length > 500 ? sanitized[..497] + "..." : sanitized;
    }

    private static string FormatAddress(MailAddressRef? addr)
    {
        if (addr == null) return "N/A";
        if (!string.IsNullOrEmpty(addr.Name) && !string.IsNullOrEmpty(addr.Address))
        {
            return $"{addr.Name} <{addr.Address}>";
        }
        return addr.Name ?? addr.Address ?? "N/A";
    }

    private sealed class ConsoleProgressReporter : IProgressReporter
    {
        private int _lastPercentageInt = -1;

        public void ReportProgress(double percentage, string status)
        {
            int pctInt = (int)Math.Round(percentage);
            if (pctInt != _lastPercentageInt)
            {
                _lastPercentageInt = pctInt;

                int width = 80;
                try
                {
                    width = Console.WindowWidth;
                }
                catch
                {
                }

                string progressStr = $"[*] Progress: {percentage:F1}% - {status}";
                if (progressStr.Length < width - 1)
                {
                    progressStr = progressStr.PadRight(width - 1);
                }
                else
                {
                    progressStr = progressStr.Substring(0, width - 1);
                }

                Console.Write($"\r{progressStr}");
                if (pctInt >= 100)
                {
                    Console.WriteLine();
                }
            }
        }
    }

    private static async Task<int> HandleValidateAsync(
        DirectoryInfo caseFolder,
        DirectoryInfo? exportFolder,
        string format,
        bool json,
        bool strict,
        bool checkEml,
        bool checkMbox,
        bool checkAtt,
        int? sampleSize,
        DirectoryInfo? outDir)
    {
        string dbPath = Path.Combine(caseFolder.FullName, "case.db");
        if (!File.Exists(dbPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] O banco de dados do caso 'case.db' não foi encontrado na pasta: '{caseFolder.FullName}'");
            Console.ResetColor();
            ExitCode = 11;
            return 11;
        }

        string auditLogFilePath = Path.Combine(caseFolder.FullName, "audit.log");
        var auditWriter = new FileAuditTrailWriter(auditLogFilePath);

        await auditWriter.WriteEventAsync(new AuditEvent(
            EventId: Guid.NewGuid().ToString(),
            Timestamp: DateTimeOffset.Now,
            Action: "ValidationCommandStarted",
            OperatorName: Environment.UserName,
            Details: $"Validação técnica de qualidade iniciada. Strict={strict}."
        ), CancellationToken.None);

        try
        {
            var engine = new MailVault.Validation.ValidationEngine();
            string? outPath = outDir?.FullName ?? caseFolder.FullName;

            var report = await engine.ValidateAsync(
                caseFolder.FullName,
                exportFolder?.FullName,
                format,
                strict,
                checkEml,
                checkMbox,
                checkAtt,
                sampleSize,
                outPath,
                CancellationToken.None
            );

            if (json)
            {
                string reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(reportJson);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("================================================================================");
                Console.WriteLine("                  MailVault Recovery — Relatório de Validação                   ");
                Console.WriteLine("================================================================================");
                Console.ResetColor();

                Console.WriteLine($"Caso ID            : {report.CaseId}");
                Console.WriteLine($"Mídia Original     : {report.SourceFileMasked}");
                Console.WriteLine($"SHA-256 Original   : {report.SourceSha256}");
                Console.WriteLine($"Adaptador Utilizado: {report.AdapterName} ({report.AdapterVersion})");
                Console.WriteLine($"Exportação ID      : {report.ExportId}");
                Console.WriteLine($"Formato Exportado  : {report.ExportFormat.ToUpperInvariant()}");
                Console.WriteLine();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("MÉTRICAS COMPARATIVAS:");
                Console.WriteLine("--------------------------------------------------------------------------------");
                Console.ResetColor();
                Console.WriteLine($"Mensagens Indexadas : {report.IndexedMessages.ToString().PadRight(6)} | Exportadas: {report.ExportedMessages}");
                Console.WriteLine($"Anexos Indexados    : {report.IndexedAttachments.ToString().PadRight(6)} | Exportados: {report.ExportedAttachments}");
                Console.WriteLine();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("AUDITORIA FÍSICA E SEGURANÇA:");
                Console.WriteLine("--------------------------------------------------------------------------------");
                Console.ResetColor();
                Console.WriteLine($"Arquivos Vazios (0 bytes)    : {report.EmptyExportedFiles}");
                Console.WriteLine($"Nomes de Arquivo Duplicados  : {report.DuplicateOutputNames}");
                Console.WriteLine($"Mensagens Exportadas Faltando: {report.MissingExpectedFiles}");
                Console.WriteLine($"Violações de Path Traversal  : {report.PathSafetyIssues}");
                Console.WriteLine();

                if (report.FolderResults.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("DIVERGÊNCIAS DETALHADAS POR PASTA:");
                    Console.WriteLine("--------------------------------------------------------------------------------");
                    Console.ResetColor();
                    foreach (var fRes in report.FolderResults)
                    {
                        string statusText = fRes.MismatchCount == 0 ? "OK" : $"DIVERGÊNCIA: {fRes.MismatchCount}";
                        Console.WriteLine($"  * {fRes.FolderName.PadRight(35)} -> Indexados: {fRes.IndexedMessages} | Exportados: {fRes.ExportedMessages} | {statusText}");
                    }
                    Console.WriteLine();
                }

                if (report.Issues.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("ALERTAS E FALHAS ESTRUTURAIS DETECTADAS:");
                    Console.WriteLine("--------------------------------------------------------------------------------");
                    Console.ResetColor();
                    foreach (var issue in report.Issues)
                    {
                        string colorSymbol = issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase) ? "[CRITICAL]" : "[AVISO]";
                        Console.WriteLine($"  {colorSymbol} ({issue.Code}): {issue.Message} (ID: {issue.ObjectId ?? "N/A"})");
                    }
                    Console.WriteLine();
                }

                Console.WriteLine("RESULTADO DA VALIDAÇÃO:");
                Console.WriteLine("--------------------------------------------------------------------------------");
                if (report.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine(">>> STATUS: PASSED (Integridade estrutural e segurança forense validadas!)");
                }
                else if (report.Status.Equals("PassedWithWarnings", StringComparison.OrdinalIgnoreCase))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(">>> STATUS: PASSED WITH WARNINGS (Validação concluída com alertas estruturais leves)");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(">>> STATUS: FAILED (Falhas de conformidade, mensagens faltantes ou problemas de segurança detectados!)");
                }
                Console.ResetColor();

                Console.WriteLine($"--------------------------------------------------------------------------------");
                Console.WriteLine($"[*] Relatório validation-report.json salvo em: {outPath}");
                Console.WriteLine("================================================================================");
            }

            await auditWriter.WriteEventAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString(),
                Timestamp: DateTimeOffset.Now,
                Action: "ValidationCommandCompleted",
                OperatorName: Environment.UserName,
                Details: $"Validação finalizada. Status final: {report.Status}. Erros: {report.ErrorCount}, Avisos: {report.WarningCount}."
            ), CancellationToken.None);

            return report.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[CRITICAL ERRO] Falha técnica durante validação forense: {ex.Message}");
            Console.ResetColor();

            await auditWriter.WriteEventAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString(),
                Timestamp: DateTimeOffset.Now,
                Action: "ValidationFailed",
                OperatorName: Environment.UserName,
                Details: $"FALHA DO COMANDO validate: {ex.Message}"
            ), CancellationToken.None);

            ExitCode = 14;
            return 14;
        }
    }

    private static async Task HandleCorpusScanAsync(DirectoryInfo corpusFolder, DirectoryInfo? outDir)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                  MailVault Recovery — Escaneamento de Corpus                  ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        if (!corpusFolder.Exists)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] O diretório do corpus especificado não existe: '{corpusFolder.FullName}'");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"[*] Inventariando arquivos em: {corpusFolder.FullName}...");
        Console.WriteLine();

        try
        {
            var files = Directory.GetFiles(corpusFolder.FullName, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase) && 
                            !Path.GetFileName(f).Equals(".gitkeep", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("ARQUIVOS DE CORPUS LOCALIZADOS:");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ResetColor();

            var records = new List<object>();
            var hashService = new HashService();

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                string ext = fileInfo.Extension.ToLowerInvariant();
                string relativePath = Path.GetRelativePath(corpusFolder.FullName, file);
                string maskedPath = MailVault.Validation.ValidationEngine.MaskPath(relativePath);

                string category = "Outros / Desconhecido";
                if (ext == ".ost") category = "OST Microsoft Outlook";
                else if (ext == ".pst") category = "PST Microsoft Outlook";
                else if (ext == ".eml") category = "EML RFC 822";
                else if (ext == ".mbox" || Path.GetFileName(file).Equals("mbox", StringComparison.OrdinalIgnoreCase)) category = "MBOX Unix Mailbox";

                string sha256 = await hashService.CalculateSha256Async(file, new NullProgressReporter(), CancellationToken.None);

                Console.WriteLine($"  * Caminho  : {maskedPath}");
                Console.WriteLine($"    Categoria: {category}");
                Console.WriteLine($"    Tamanho  : {fileInfo.Length:N0} bytes ({(double)fileInfo.Length / (1024 * 1024):F2} MB)");
                Console.WriteLine($"    SHA-256  : {sha256}");
                Console.WriteLine();

                records.Add(new {
                    CaminhoMascarado = maskedPath,
                    Extensao = ext,
                    TamanhoBytes = fileInfo.Length,
                    Sha256 = sha256,
                    CategoriaInferida = category,
                    Status = "validated"
                });
            }

            Console.WriteLine($"--------------------------------------------------------------------------------");
            Console.WriteLine($"[x] Varredura Concluída. Localizados {files.Length} arquivos.");

            if (outDir != null)
            {
                if (!outDir.Exists) outDir.Create();
                string reportPath = Path.Combine(outDir.FullName, "corpus-report.json");
                string json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(reportPath, json);
                Console.WriteLine($"[*] Relatório corpus-report.json salvo em: {reportPath}");
            }
            Console.WriteLine("================================================================================");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] Falha técnica durante varredura do corpus: {ex.Message}");
            Console.ResetColor();
        }
    }

    private sealed class NullProgressReporter : IProgressReporter
    {
        public void ReportProgress(double percentage, string status) { }
    }

    private class IndexJobConfig
    {
        public string? EvidencePath { get; set; }
        public string? CasePath { get; set; }
        public string? CaseId { get; set; }
        public string? OperatorId { get; set; }
        public string? EvidenceSha256 { get; set; }
        public long EvidenceSize { get; set; }
        public string? SelectedReaderEngine { get; set; }
        public bool NoBodyLogging { get; set; }
        public string? DeepRecoveryMode { get; set; }
    }

    private static async Task HandleIndexWorkerAsync(FileInfo jobFile)
    {
        var realStdout = Console.Out;
        Console.SetOut(Console.Error); // Redirect all standard console writes to stderr to prevent stdout contamination

        IndexJobConfig? jobConfig = null;
        try
        {
            if (!jobFile.Exists)
            {
                throw new FileNotFoundException($"Arquivo de configuração do job não encontrado: {jobFile.FullName}");
            }

            string jsonContent = await File.ReadAllTextAsync(jobFile.FullName);
            jobConfig = JsonSerializer.Deserialize<IndexJobConfig>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            var errEvent = new
            {
                type = "failed",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                engine = "Unknown",
                status = "Failed",
                message = $"Erro ao carregar job.json: {ex.Message}"
            };
            realStdout.WriteLine(JsonSerializer.Serialize(errEvent));
            realStdout.Flush();
            ExitCode = 9;
            return;
        }

        if (jobConfig == null || string.IsNullOrEmpty(jobConfig.EvidencePath) || string.IsNullOrEmpty(jobConfig.CasePath) || string.IsNullOrEmpty(jobConfig.CaseId))
        {
            var errEvent = new
            {
                type = "failed",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                engine = "Unknown",
                status = "Failed",
                message = "Configuração do job incompleta: campos obrigatórios ausentes."
            };
            realStdout.WriteLine(JsonSerializer.Serialize(errEvent));
            realStdout.Flush();
            ExitCode = 10;
            return;
        }

        string engineName = jobConfig.SelectedReaderEngine ?? "XstReader";
        var startedEvent = new
        {
            type = "started",
            timestampUtc = DateTime.UtcNow.ToString("o"),
            engine = engineName,
            message = $"Processamento do job iniciado. Motor: {engineName}."
        };
        realStdout.WriteLine(JsonSerializer.Serialize(startedEvent));
        realStdout.Flush();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var heartbeatCts = new CancellationTokenSource();
        
        var heartbeatTask = Task.Run(async () =>
        {
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, heartbeatCts.Token);
                    if (heartbeatCts.Token.IsCancellationRequested) break;

                    var hbEvent = new
                    {
                        type = "heartbeat",
                        timestampUtc = DateTime.UtcNow.ToString("o"),
                        engine = engineName,
                        phase = "Running",
                        currentFolderPath = "",
                        foldersProcessed = 0,
                        messagesProcessed = 0,
                        attachmentsProcessed = 0,
                        issuesCount = 0,
                        heartbeat = true,
                        message = "Pulsando heartbeat de atividade técnica..."
                    };
                    realStdout.WriteLine(JsonSerializer.Serialize(hbEvent));
                    realStdout.Flush();
                }
                catch
                {
                    // Ignore background delay cancellation exceptions
                }
            }
        }, heartbeatCts.Token);

        try
        {
            using var store = new SqliteCaseIndexStore();
            await store.InitializeAsync(jobConfig.CasePath, CancellationToken.None);

            var engineFactory = new ReaderEngineFactory();
            var engine = engineFactory.CreateEngine(engineName);
            engine.PrecalculatedSha256 = jobConfig.EvidenceSha256 ?? "";

            if (engine is LibpffExternalEngine pffEngine)
            {
                pffEngine.DeepRecoveryMode = jobConfig.DeepRecoveryMode ?? "items";
            }

            var progressReporter = new Progress<IndexingProgress>(p =>
            {
                var progressEvent = new
                {
                    type = "progress",
                    timestampUtc = DateTime.UtcNow.ToString("o"),
                    engine = engineName,
                    phase = p.Phase,
                    currentFolderPath = p.CurrentFolderPath,
                    foldersProcessed = p.FoldersProcessed,
                    messagesProcessed = p.MessagesProcessed,
                    attachmentsProcessed = p.AttachmentsProcessed,
                    issuesCount = p.IssuesCount,
                    heartbeat = false,
                    message = p.Message
                };
                realStdout.WriteLine(JsonSerializer.Serialize(progressEvent));
                realStdout.Flush();
            });

            var result = await engine.IndexAsync(
                jobConfig.EvidencePath,
                store,
                jobConfig.CaseId,
                jobConfig.OperatorId ?? Environment.UserName,
                cachePreview: true,
                limit: null,
                progress: progressReporter,
                ct: CancellationToken.None
            );

            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { }

            var completedEvent = new
            {
                type = "completed",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                engine = engineName,
                status = result.Status,
                folders = result.FoldersIndexed,
                messages = result.MessagesIndexed,
                attachments = result.AttachmentsIndexed,
                issues = result.IssuesDetected,
                elapsedMs = stopwatch.ElapsedMilliseconds
            };
            realStdout.WriteLine(JsonSerializer.Serialize(completedEvent));
            realStdout.Flush();
            ExitCode = (result.Status == "Success" || result.Status == "NativeExportSuccess" || result.Status == "NativeExportSuccessWithWarnings") ? 0 : 8;
        }
        catch (OperationCanceledException)
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { }

            var cancelEvent = new
            {
                type = "cancelled",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                engine = engineName,
                status = "Cancelled",
                message = "Indexação abortada por solicitação de cancelamento."
            };
            realStdout.WriteLine(JsonSerializer.Serialize(cancelEvent));
            realStdout.Flush();
            ExitCode = 130;
        }
        catch (Exception ex)
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { }

            var failedEvent = new
            {
                type = "failed",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                engine = engineName,
                status = "Failed",
                message = $"Erro durante execução do motor: {ex.Message}"
            };
            realStdout.WriteLine(JsonSerializer.Serialize(failedEvent));
            realStdout.Flush();
            ExitCode = 1;
        }
    }

    private class WorkerJobConfig
    {
        public string? JobKind { get; set; }
        public string? EvidencePath { get; set; }
        public string? CasePath { get; set; }
        public string? CaseId { get; set; }
        public string? OperatorId { get; set; }
        public string? EvidenceSha256 { get; set; }
        public long EvidenceSize { get; set; }
        public string? SelectedReaderEngine { get; set; }
        public bool NoBodyLogging { get; set; } = true;
        public bool IndexingModeMetadataOnly { get; set; } = true;

        // Export / Preview / Attachment parameters
        public string? OutputPath { get; set; }
        public string? ExportFormat { get; set; }
        public List<string>? SelectedMessageIds { get; set; }
        public bool IncludeAttachments { get; set; } = true;
        public bool ExtractAttachments { get; set; } = true;
        public string? MessageId { get; set; }
        public string? AttachmentId { get; set; }

        // Fallback tool diagnostic parameter
        public string? FallbackToolName { get; set; }
        public string? DeepRecoveryMode { get; set; }
    }

    private static async Task HandleWorkerJobAsync(FileInfo jobFile)
    {
        var realStdout = Console.Out;
        Console.SetOut(Console.Error); // Redirect stdout to stderr to avoid log contamination

        WorkerJobConfig? job = null;
        try
        {
            if (!jobFile.Exists)
            {
                throw new FileNotFoundException($"Arquivo de configuração do job não encontrado: {jobFile.FullName}");
            }

            string jsonContent = await File.ReadAllTextAsync(jobFile.FullName);
            job = JsonSerializer.Deserialize<WorkerJobConfig>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            var err = new { type = "failed", timestampUtc = DateTime.UtcNow.ToString("o"), status = "Failed", message = $"Erro ao carregar job: {ex.Message}" };
            realStdout.WriteLine(JsonSerializer.Serialize(err));
            realStdout.Flush();
            ExitCode = 9;
            return;
        }

        if (job == null || string.IsNullOrEmpty(job.JobKind))
        {
            var err = new { type = "failed", timestampUtc = DateTime.UtcNow.ToString("o"), status = "Failed", message = "Configuração do job inválida ou JobKind ausente." };
            realStdout.WriteLine(JsonSerializer.Serialize(err));
            realStdout.Flush();
            ExitCode = 10;
            return;
        }

        string kind = job.JobKind;
        if (kind.Equals("IndexMetadata", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteIndexMetadataJobAsync(job, realStdout);
        }
        else if (kind.Equals("Export", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteExportJobAsync(job, realStdout);
        }
        else if (kind.Equals("PreviewMessage", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePreviewMessageJobAsync(job, realStdout);
        }
        else if (kind.Equals("ExtractAttachment", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteExtractAttachmentJobAsync(job, realStdout);
        }
        else if (kind.Equals("ValidateExport", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteValidateExportJobAsync(job, realStdout);
        }
        else if (kind.Equals("NativeFallbackDiagnostic", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteNativeFallbackDiagnosticJobAsync(job, realStdout);
        }
        else
        {
            var err = new { type = "failed", timestampUtc = DateTime.UtcNow.ToString("o"), status = "Failed", message = $"JobKind '{kind}' não é suportado." };
            realStdout.WriteLine(JsonSerializer.Serialize(err));
            realStdout.Flush();
            ExitCode = 11;
        }
    }

    private static async Task ExecuteIndexMetadataJobAsync(WorkerJobConfig job, TextWriter realStdout)
    {
        string engineName = job.SelectedReaderEngine ?? "XstReader";
        var startedEvent = new { type = "started", timestampUtc = DateTime.UtcNow.ToString("o"), engine = engineName, message = $"Processamento do job IndexMetadata iniciado. Motor: {engineName}." };
        realStdout.WriteLine(JsonSerializer.Serialize(startedEvent));
        realStdout.Flush();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var heartbeatCts = new CancellationTokenSource();
        var heartbeatTask = Task.Run(async () =>
        {
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, heartbeatCts.Token);
                    if (heartbeatCts.Token.IsCancellationRequested) break;

                    var hb = new { type = "heartbeat", timestampUtc = DateTime.UtcNow.ToString("o"), engine = engineName, phase = "Running", heartbeat = true, message = "Pulsando heartbeat de atividade..." };
                    realStdout.WriteLine(JsonSerializer.Serialize(hb));
                    realStdout.Flush();
                }
                catch { }
            }
        });

        try
        {
            using var store = new SqliteCaseIndexStore();
            await store.InitializeAsync(job.CasePath!, CancellationToken.None);

            var engineFactory = new ReaderEngineFactory();
            var engine = engineFactory.CreateEngine(engineName);
            engine.PrecalculatedSha256 = job.EvidenceSha256 ?? "";

            if (engine is IMetadataOnlyEngine metadataEngine)
            {
                metadataEngine.MetadataOnly = job.IndexingModeMetadataOnly;
            }

            if (engine is LibpffExternalEngine pffEngine)
            {
                pffEngine.DeepRecoveryMode = job.DeepRecoveryMode ?? "items";
            }

            var progressReporter = new Progress<IndexingProgress>(p =>
            {
                var pr = new
                {
                    type = "progress",
                    timestampUtc = DateTime.UtcNow.ToString("o"),
                    engine = engineName,
                    phase = p.Phase,
                    currentFolderPath = p.CurrentFolderPath,
                    foldersProcessed = p.FoldersProcessed,
                    messagesProcessed = p.MessagesProcessed,
                    attachmentsProcessed = p.AttachmentsProcessed,
                    issuesCount = p.IssuesCount,
                    heartbeat = false,
                    message = p.Message
                };
                realStdout.WriteLine(JsonSerializer.Serialize(pr));
                realStdout.Flush();
            });

            var result = await engine.IndexAsync(
                job.EvidencePath!,
                store,
                job.CaseId!,
                job.OperatorId ?? Environment.UserName,
                cachePreview: !job.IndexingModeMetadataOnly,
                limit: null,
                progress: progressReporter,
                ct: CancellationToken.None
            );

            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { }

            // Write Manifest & Audit Log inside the worker itself for clean completion!
            var warnings = new List<ExtractionIssue>();
            if (result.IssuesDetected > 0)
            {
                warnings.Add(new ExtractionIssue(
                    Code: "MV-WARN-WORKER-ISSUES",
                    Severity: "Warning",
                    Message: $"Indexação concluída com {result.IssuesDetected} avisos técnicos detectados.",
                    ObjectId: job.CaseId!,
                    TechnicalDetails: $"Avisos acumulados durante o processamento do worker."
                ));
            }

            string actionText = engineName == "LibpffExternal"
                ? $"Executed out-of-process native recovery and structural diagnostics (NativeRecoveryDiagnostic: {engineName})"
                : $"Executed out-of-process indexer ({engineName})";

            var manifest = new RecoveryManifest(
                CaseId: job.CaseId!,
                SourceFile: job.EvidencePath!,
                SourceSizeBytes: job.EvidenceSize,
                SourceSha256: job.EvidenceSha256 ?? "",
                OperatorName: job.OperatorId ?? Environment.UserName,
                StartedAt: DateTimeOffset.UtcNow,
                CompletedAt: DateTimeOffset.UtcNow,
                ToolVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0.0",
                Actions: new List<string> { actionText },
                Warnings: warnings
            );

            string caseDirBase = Path.GetDirectoryName(job.CasePath!)!;
            await ManifestService.SaveManifestAsync(caseDirBase, manifest, CancellationToken.None);

            string auditLogFilePath = Path.Combine(job.CasePath!, "audit.log");
            var auditWriter = new FileAuditTrailWriter(auditLogFilePath);

            string auditAction = engineName == "LibpffExternal" ? "NativeRecoveryDiagnosticCompleted" : "IndexCompletedByWorker";
            string auditDetails = engineName == "LibpffExternal"
                ? "Recuperação nativa realizada via pffinfo/pffexport. Relatório native-fallback-report.json gerado."
                : $"Indexação concluída pelo worker process. Pastas: {result.FoldersIndexed}, E-mails: {result.MessagesIndexed}.";

            await auditWriter.WriteEventAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString(),
                Timestamp: DateTimeOffset.UtcNow,
                Action: auditAction,
                OperatorName: job.OperatorId ?? Environment.UserName,
                Details: auditDetails
            ), CancellationToken.None);

            // Close store cleanly
            store.Dispose();

            // Checkpoint-truncate WAL after connections are completely closed!
            try
            {
                string dbPath = Path.Combine(job.CasePath!, "case.db");
                if (File.Exists(dbPath))
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    using var checkpointConn = new SqliteConnection($"Data Source={dbPath};Pooling=False;");
                    await checkpointConn.OpenAsync();
                    using var checkpointCmd = new SqliteCommand("PRAGMA wal_checkpoint(TRUNCATE);", checkpointConn);
                    await checkpointCmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WAL Checkpoint Warning] {ex.Message}");
            }

            var completedEvent = new
            {
                type = "completed",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                engine = engineName,
                status = result.Status,
                folders = result.FoldersIndexed,
                messages = result.MessagesIndexed,
                attachments = result.AttachmentsIndexed,
                issues = result.IssuesDetected,
                elapsedMs = stopwatch.ElapsedMilliseconds
            };
            realStdout.WriteLine(JsonSerializer.Serialize(completedEvent));
            realStdout.Flush();
            ExitCode = (result.Status == "Success" || result.Status == "NativeExportSuccess" || result.Status == "NativeExportSuccessWithWarnings") ? 0 : 8;
        }
        catch (OperationCanceledException)
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { }

            var cancelEvent = new { type = "cancelled", timestampUtc = DateTime.UtcNow.ToString("o"), engine = engineName, status = "Cancelled", message = "Indexação cancelada por solicitação." };
            realStdout.WriteLine(JsonSerializer.Serialize(cancelEvent));
            realStdout.Flush();
            ExitCode = 130;
        }
        catch (Exception ex)
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { }

            // Write diagnostic report
            try
            {
                string reportPath = Path.Combine(job.CasePath!, "reader-diagnostic-report.json");
                var report = new
                {
                    lastPhase = "Failed",
                    lastFolder = "",
                    workerPid = System.Environment.ProcessId,
                    heartbeatAge = stopwatch.ElapsedMilliseconds,
                    exitCode = 1,
                    exceptionSummary = ex.Message,
                    issueCodes = new[] { "MV-ERR-INDEX-FATAL" }
                };
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }

            var failedEvent = new { type = "failed", timestampUtc = DateTime.UtcNow.ToString("o"), engine = engineName, status = "Failed", message = $"Erro durante execução do motor: {ex.Message}" };
            realStdout.WriteLine(JsonSerializer.Serialize(failedEvent));
            realStdout.Flush();
            ExitCode = 1;
        }
    }

    private static async Task ExecuteExportJobAsync(WorkerJobConfig job, TextWriter realStdout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string finalOutputDir = job.OutputPath ?? Path.Combine(job.CasePath!, "exports");

            using var store = new SqliteCaseIndexStore();
            await store.InitializeAsync(job.CasePath!, CancellationToken.None);

            using var caseReader = store.CreateReader();
            var adapterResolver = new ReflectionAdapterResolver();

            IMessageExporter innerExporter;
            if (job.ExportFormat != null && job.ExportFormat.Equals("mbox", StringComparison.OrdinalIgnoreCase))
            {
                var emlExporter = new MailVault.Exporters.Eml.EmlExporter();
                innerExporter = new MailVault.Exporters.Mbox.MboxExporter(emlExporter);
            }
            else
            {
                innerExporter = new MailVault.Exporters.Eml.EmlExporter();
            }

            var exportRunner = new ExportJobRunner();
            var options = new ExportJobOptions(
                CaseFolder: job.CasePath!,
                Format: job.ExportFormat ?? "eml",
                OutputDir: finalOutputDir,
                FolderIdOrPath: null,
                Limit: null,
                Offset: null,
                IncludeAttachments: job.IncludeAttachments,
                ExtractAttachments: job.ExtractAttachments,
                Overwrite: true,
                DryRun: false
            );

            var progressReporter = new WorkerProgressReporter(realStdout);

            var result = await exportRunner.RunExportJobAsync(options, caseReader, adapterResolver, innerExporter, progressReporter, CancellationToken.None);

            // Save export manifest
            var completedEvent = new
            {
                type = "completed",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                status = "Success",
                messages = result.MessagesExported,
                folders = 0,
                attachments = 0,
                issues = 0,
                elapsedMs = stopwatch.ElapsedMilliseconds
            };
            realStdout.WriteLine(JsonSerializer.Serialize(completedEvent));
            realStdout.Flush();
            ExitCode = 0;
        }
        catch (Exception ex)
        {
            var failedEvent = new { type = "failed", timestampUtc = DateTime.UtcNow.ToString("o"), status = "Failed", message = $"Erro durante exportação: {ex.Message}" };
            realStdout.WriteLine(JsonSerializer.Serialize(failedEvent));
            realStdout.Flush();
            ExitCode = 1;
        }
    }

    private sealed class WorkerProgressReporter : IProgressReporter
    {
        private readonly TextWriter _realStdout;

        public WorkerProgressReporter(TextWriter realStdout)
        {
            _realStdout = realStdout;
        }

        public void ReportProgress(double percentage, string status)
        {
            var pr = new
            {
                type = "progress",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                phase = "Exporting",
                message = $"Exportando mensagens... {percentage:N0}% completo."
            };
            _realStdout.WriteLine(JsonSerializer.Serialize(pr));
            _realStdout.Flush();
        }
    }

    private static async Task<MimeMessage> LoadMimeMessageForPreviewAsync(string evidencePath, string messageId, string engine, CancellationToken ct)
    {
        if (engine.Equals("EmlFolder", StringComparison.OrdinalIgnoreCase))
        {
            string fullPath = Path.Combine(evidencePath, messageId.Replace('/', '\\'));
            if (!File.Exists(fullPath))
            {
                string filename = Path.GetFileName(messageId);
                fullPath = Path.Combine(evidencePath, filename);
            }
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Arquivo EML não encontrado para preview: '{fullPath}'");
            }
            using var fs = File.OpenRead(fullPath);
            return await MimeMessage.LoadAsync(fs, ct);
        }
        else if (engine.Equals("Maildir", StringComparison.OrdinalIgnoreCase))
        {
            string logicalPath = Path.GetDirectoryName(messageId) ?? "";
            string filename = Path.GetFileName(messageId);
            
            string curPath = Path.Combine(evidencePath, logicalPath.Replace('/', '\\'), "cur", filename);
            string newPath = Path.Combine(evidencePath, logicalPath.Replace('/', '\\'), "new", filename);
            
            string fullPath = File.Exists(curPath) ? curPath : (File.Exists(newPath) ? newPath : "");
            if (string.IsNullOrEmpty(fullPath))
            {
                string curDirect = Path.Combine(evidencePath, "cur", filename);
                string newDirect = Path.Combine(evidencePath, "new", filename);
                fullPath = File.Exists(curDirect) ? curDirect : (File.Exists(newDirect) ? newDirect : "");
            }

            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Mensagem Maildir não encontrada para preview: '{filename}'");
            }

            using var fs = File.OpenRead(fullPath);
            return await MimeMessage.LoadAsync(fs, ct);
        }
        else if (engine.Equals("ThunderbirdMbox", StringComparison.OrdinalIgnoreCase))
        {
            int idx = messageId.LastIndexOf("/offset-");
            if (idx < 0)
            {
                throw new ArgumentException($"ID de mensagem inválido para preview MBOX: '{messageId}'");
            }

            string logicalPath = messageId.Substring(0, idx);
            string offsetStr = messageId.Substring(idx + 8);
            if (!long.TryParse(offsetStr, out long offset))
            {
                throw new ArgumentException($"Offset inválido no ID de mensagem MBOX: '{messageId}'");
            }

            string mboxPath = Path.Combine(evidencePath, logicalPath.Replace('/', '\\'));
            if (!File.Exists(mboxPath))
            {
                string filename = Path.GetFileName(logicalPath);
                mboxPath = Path.Combine(evidencePath, filename);
            }

            if (!File.Exists(mboxPath))
            {
                throw new FileNotFoundException($"Arquivo MBOX não encontrado para preview: '{mboxPath}'");
            }

            using var fs = File.OpenRead(mboxPath);
            fs.Position = offset;
            var parser = new MimeParser(fs, MimeFormat.Mbox);
            return await parser.ParseMessageAsync(ct);
        }

        throw new NotSupportedException($"Motor '{engine}' não suportado para preview direto.");
    }

    private static async Task ExecutePreviewMessageJobAsync(WorkerJobConfig job, TextWriter realStdout)
    {
        try
        {
            if (string.IsNullOrEmpty(job.MessageId) || string.IsNullOrEmpty(job.EvidencePath))
            {
                throw new ArgumentException("MessageId e EvidencePath são obrigatórios para PreviewMessage.");
            }

            MimeMessage? mimeMsg = null;
            MailItem? message = null;

            if (job.SelectedReaderEngine != null && 
                (job.SelectedReaderEngine.Equals("ThunderbirdMbox", StringComparison.OrdinalIgnoreCase) ||
                 job.SelectedReaderEngine.Equals("Maildir", StringComparison.OrdinalIgnoreCase) ||
                 job.SelectedReaderEngine.Equals("EmlFolder", StringComparison.OrdinalIgnoreCase)))
            {
                mimeMsg = await LoadMimeMessageForPreviewAsync(job.EvidencePath, job.MessageId, job.SelectedReaderEngine, CancellationToken.None);
            }
            else
            {
                string extension = Path.GetExtension(job.EvidencePath);
                var resolver = new ReflectionAdapterResolver();
                var res = resolver.ResolveAdapter(extension);
                if (!res.Success || res.Reader == null)
                {
                    throw new InvalidOperationException($"Não foi possível carregar o leitor para {extension}");
                }

                if (res.Reader is IMetadataOnlyAware metadataAware)
                {
                    metadataAware.MetadataOnly = false; // We want full body for preview!
                }

                if (res.Reader is ISessionAwareMailStoreReader sessionReader)
                {
                    await sessionReader.BeginReadSessionAsync(job.EvidencePath, CancellationToken.None);
                }

                var msgResult = await res.Reader.GetMessageAsync(new MessageId(job.MessageId), CancellationToken.None);

                if (res.Reader is ISessionAwareMailStoreReader sessionReaderEnd)
                {
                    await sessionReaderEnd.EndReadSessionAsync(CancellationToken.None);
                }

                if (!msgResult.Success || msgResult.Value == null)
                {
                    throw new InvalidOperationException($"Mensagem não encontrada ou inacessível: {(msgResult.Issues.Count > 0 ? msgResult.Issues[0].Message : "Erro desconhecido")}");
                }

                message = msgResult.Value;
            }

            object result;
            if (mimeMsg != null)
            {
                result = new
                {
                    type = "preview_result",
                    messageId = job.MessageId,
                    subject = mimeMsg.Subject ?? "",
                    body = mimeMsg.TextBody ?? "",
                    htmlBody = mimeMsg.HtmlBody ?? "",
                    headers = string.Join("\r\n", mimeMsg.Headers.Select(h => $"{h.Field}: {h.Value}")),
                    from = mimeMsg.From.Mailboxes.Select(m => $"{m.Name} <{m.Address}>").FirstOrDefault() ?? "",
                    to = string.Join("; ", mimeMsg.To.Mailboxes.Select(m => m.Address)),
                    cc = string.Join("; ", mimeMsg.Cc.Mailboxes.Select(m => m.Address)),
                    bcc = string.Join("; ", mimeMsg.Bcc.Mailboxes.Select(m => m.Address)),
                    sentAt = mimeMsg.Date.ToString("o"),
                    receivedAt = mimeMsg.Date.ToString("o"),
                    attachments = mimeMsg.BodyParts.Where(b => b.IsAttachment).Select((b, i) => new { fileName = b.ContentType.Name ?? $"anexo-{i+1}", sizeBytes = b is MimePart p && p.Content != null ? p.Content.Stream.Length : 0 }).ToList()
                };
            }
            else
            {
                result = new
                {
                    type = "preview_result",
                    messageId = message!.InternalId,
                    subject = message.Subject ?? "",
                    body = message.PlainTextBody ?? "",
                    htmlBody = message.HtmlBody ?? "",
                    headers = message.RawProperties.TryGetValue("PR_TRANSPORT_MESSAGE_HEADERS", out var headers) ? headers : "",
                    from = message.From != null ? $"{message.From.Name} <{message.From.Address}>" : "",
                    to = string.Join("; ", message.To.Select(r => r.Address)),
                    cc = string.Join("; ", message.Cc.Select(r => r.Address)),
                    bcc = string.Join("; ", message.Bcc.Select(r => r.Address)),
                    sentAt = message.SentAt?.ToString("o") ?? "",
                    receivedAt = message.ReceivedAt?.ToString("o") ?? "",
                    attachments = message.Attachments.Select(a => new { fileName = a.FileName, sizeBytes = a.SizeBytes }).ToList()
                };
            }

            realStdout.WriteLine(JsonSerializer.Serialize(result));
            realStdout.Flush();
            ExitCode = 0;
        }
        catch (Exception ex)
        {
            var failedEvent = new { type = "failed", timestampUtc = DateTime.UtcNow.ToString("o"), status = "Failed", message = $"Erro ao gerar preview da mensagem: {ex.Message}" };
            realStdout.WriteLine(JsonSerializer.Serialize(failedEvent));
            realStdout.Flush();
            ExitCode = 1;
        }
    }

    private static async Task ExecuteExtractAttachmentJobAsync(WorkerJobConfig job, TextWriter realStdout)
    {
        try
        {
            if (string.IsNullOrEmpty(job.EvidencePath) || string.IsNullOrEmpty(job.MessageId) || string.IsNullOrEmpty(job.AttachmentId) || string.IsNullOrEmpty(job.OutputPath))
            {
                throw new ArgumentException("EvidencePath, MessageId, AttachmentId e OutputPath são obrigatórios para ExtractAttachment.");
            }

            string extension = Path.GetExtension(job.EvidencePath);
            var resolver = new ReflectionAdapterResolver();
            var res = resolver.ResolveAdapter(extension);
            if (!res.Success || res.Reader == null)
            {
                throw new InvalidOperationException($"Não foi possível carregar o leitor para {extension}");
            }

            if (res.Reader is ISessionAwareMailStoreReader sessionReader)
            {
                await sessionReader.BeginReadSessionAsync(job.EvidencePath, CancellationToken.None);
            }

            using var stream = await res.Reader.OpenAttachmentStreamAsync(new MessageId(job.MessageId), new AttachmentId(job.AttachmentId), CancellationToken.None);
            
            string? dir = Path.GetDirectoryName(job.OutputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var outStream = File.Create(job.OutputPath))
            {
                await stream.CopyToAsync(outStream);
            }

            if (res.Reader is ISessionAwareMailStoreReader sessionReaderEnd)
            {
                await sessionReaderEnd.EndReadSessionAsync(CancellationToken.None);
            }

            var completedEvent = new { type = "completed", timestampUtc = DateTime.UtcNow.ToString("o"), status = "Success", message = $"Anexo extraído com sucesso para: {job.OutputPath}" };
            realStdout.WriteLine(JsonSerializer.Serialize(completedEvent));
            realStdout.Flush();
            ExitCode = 0;
        }
        catch (Exception ex)
        {
            var failedEvent = new { type = "failed", timestampUtc = DateTime.UtcNow.ToString("o"), status = "Failed", message = $"Erro ao extrair anexo: {ex.Message}" };
            realStdout.WriteLine(JsonSerializer.Serialize(failedEvent));
            realStdout.Flush();
            ExitCode = 1;
        }
    }

    private static async Task ExecuteValidateExportJobAsync(WorkerJobConfig job, TextWriter realStdout)
    {
        try
        {
            var validationService = new MailVault.Validation.ValidationEngine();
            var report = await validationService.ValidateAsync(
                job.CasePath!,
                exportFolderOverride: null,
                formatOverride: job.ExportFormat ?? "eml",
                strict: true,
                checkEmlParse: true,
                checkMboxStructure: false,
                checkAttachments: true,
                sampleSize: null,
                outDir: job.CasePath!,
                CancellationToken.None
            );

            var completedEvent = new { type = "completed", timestampUtc = DateTime.UtcNow.ToString("o"), status = "Success", message = $"Validação concluída. Status: {report.Status}" };
            realStdout.WriteLine(JsonSerializer.Serialize(completedEvent));
            realStdout.Flush();
            ExitCode = 0;
        }
        catch (Exception ex)
        {
            var failedEvent = new { type = "failed", timestampUtc = DateTime.UtcNow.ToString("o"), status = "Failed", message = $"Erro ao validar exportação: {ex.Message}" };
            realStdout.WriteLine(JsonSerializer.Serialize(failedEvent));
            realStdout.Flush();
            ExitCode = 1;
        }
    }

    private static async Task ExecuteNativeFallbackDiagnosticJobAsync(WorkerJobConfig job, TextWriter realStdout)
    {
        try
        {
            string toolName = job.FallbackToolName ?? "pffexport";
            var capability = await ExternalToolDetector.DetectToolAsync(toolName, CancellationToken.None);
            
            var report = new
            {
                toolName = toolName,
                isAvailable = capability.IsAvailable,
                executablePath = capability.ExecutablePath,
                version = capability.Version ?? "Unknown",
                licenseWarning = capability.LicenseWarning ?? "N/A",
                timestampUtc = DateTime.UtcNow.ToString("o")
            };

            string diagPath = Path.Combine(job.CasePath!, "native-tool-diagnostic-report.json");
            File.WriteAllText(diagPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            var completedEvent = new { type = "completed", timestampUtc = DateTime.UtcNow.ToString("o"), status = "Success", message = $"Diagnóstico técnico de {toolName} salvo em native-tool-diagnostic-report.json" };
            realStdout.WriteLine(JsonSerializer.Serialize(completedEvent));
            realStdout.Flush();
            ExitCode = 0;
        }
        catch (Exception ex)
        {
            var failedEvent = new { type = "failed", timestampUtc = DateTime.UtcNow.ToString("o"), status = "Failed", message = $"Erro ao executar diagnóstico de ferramentas nativas: {ex.Message}" };
            realStdout.WriteLine(JsonSerializer.Serialize(failedEvent));
            realStdout.Flush();
            ExitCode = 1;
        }
    }

    private static async Task<int> HandleRecoverEmlAsync(FileInfo file, string outputDir, string? folderPath)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("            MailVault Recovery — Recuperação Direta para EML                   ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        if (!file.Exists)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] Arquivo não encontrado: '{file.FullName}'");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine($"[*] Fonte  : {file.FullName}");
        Console.WriteLine($"[*] Saída  : {outputDir}");
        if (!string.IsNullOrEmpty(folderPath))
            Console.WriteLine($"[*] Pasta  : {folderPath}");
        Console.WriteLine();

        IMailStoreReader reader;
        try
        {
            reader = GetMailStoreReader(file.Extension);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] Motor de leitura não disponível: {ex.Message}");
            Console.ResetColor();
            return 2;
        }

        var runner = new MailVault.Core.RecoveryExportRunner();
        var exporter = new MailVault.Exporters.Eml.EmlExporter();

        var progress = new Progress<MailVault.Core.RecoveryExportProgress>(p =>
        {
            Console.WriteLine($"  [{p.FoldersProcessed}] {p.CurrentFolder} — {p.MessagesExported} exportados, {p.MessagesFailed} falhas");
        });

        RecoveryExportResult result;
        try
        {
            result = await runner.ExportToEmlAsync(
                reader, exporter, file.FullName, outputDir, folderPath, null, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO FATAL] {ex.Message}");
            Console.ResetColor();
            return 3;
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                          RELATÓRIO DE RECUPERAÇÃO                              ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();
        Console.WriteLine($"  Motor           : {result.Engine}");
        Console.WriteLine($"  Total Mensagens : {result.TotalMessages}");
        Console.WriteLine($"  Exportadas      : {result.ExportedMessages}");
        Console.WriteLine($"  Falhas          : {result.FailedMessages}");
        Console.WriteLine($"  Anexos OK       : {result.ExportedAttachments}");
        Console.WriteLine($"  Anexos Falhos   : {result.FailedAttachments}");
        Console.WriteLine($"  Duração         : {(result.FinishedAt - result.StartedAt).TotalSeconds:F1}s");
        Console.WriteLine($"  Relatório       : {System.IO.Path.Combine(outputDir, "_mailvault-export-report.json")}");

        if (result.FailedMessages > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n  AVISO: {result.FailedMessages} mensagem(ns) falharam. Ver _mailvault-export-errors.csv.");
            Console.ResetColor();
        }

        return result.ExportedMessages > 0 ? 0 : 4;
    }

    private static async Task<int> HandleRecoverMboxAsync(FileInfo file, string outputDir, string? folderPath)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("            MailVault Recovery — Recuperação Direta para MBOX                  ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        if (!file.Exists)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] Arquivo não encontrado: '{file.FullName}'");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine($"[*] Fonte  : {file.FullName}");
        Console.WriteLine($"[*] Saída  : {outputDir}");
        if (!string.IsNullOrEmpty(folderPath))
            Console.WriteLine($"[*] Pasta  : {folderPath}");
        Console.WriteLine();

        IMailStoreReader reader;
        try
        {
            reader = GetMailStoreReader(file.Extension);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] Motor de leitura não disponível: {ex.Message}");
            Console.ResetColor();
            return 2;
        }

        var runner = new MailVault.Core.RecoveryExportRunner();
        var emlExporter = new MailVault.Exporters.Eml.EmlExporter();
        var mboxExporter = new MailVault.Exporters.Mbox.MboxExporter(emlExporter);

        var progress = new Progress<MailVault.Core.RecoveryExportProgress>(p =>
        {
            Console.WriteLine($"  [{p.FoldersProcessed}] {p.CurrentFolder} — {p.MessagesExported} exportados, {p.MessagesFailed} falhas");
        });

        RecoveryExportResult result;
        try
        {
            result = await runner.ExportToMboxAsync(
                reader, mboxExporter, file.FullName, outputDir, folderPath, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO FATAL] {ex.Message}");
            Console.ResetColor();
            return 3;
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                          RELATÓRIO DE RECUPERAÇÃO                              ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();
        Console.WriteLine($"  Motor           : {result.Engine}");
        Console.WriteLine($"  Total Mensagens : {result.TotalMessages}");
        Console.WriteLine($"  Exportadas      : {result.ExportedMessages}");
        Console.WriteLine($"  Falhas          : {result.FailedMessages}");
        Console.WriteLine($"  Duração         : {(result.FinishedAt - result.StartedAt).TotalSeconds:F1}s");
        Console.WriteLine($"  Relatório       : {System.IO.Path.Combine(outputDir, "_mailvault-export-report.json")}");

        if (result.FailedMessages > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n  AVISO: {result.FailedMessages} mensagem(ns) falharam. Ver _mailvault-export-errors.csv.");
            Console.ResetColor();
        }

        return result.ExportedMessages > 0 ? 0 : 4;
    }
}

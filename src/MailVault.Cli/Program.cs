using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Audit;
using MailVault.Core;
using MailVault.Domain;

namespace MailVault.Cli;

public static class Program
{
    private static IMailStoreReader? _injectedReader;

    public static void InjectReader(IMailStoreReader reader)
    {
        _injectedReader = reader;
    }

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

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
        var fileArgPreview = new Argument<FileInfo>("file", "O caminho do arquivo .ost/.pst.") { Arity = ArgumentArity.ExactlyOne };
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

        rootCommand.AddCommand(inspectCommand);
        rootCommand.AddCommand(treeCommand);
        rootCommand.AddCommand(listCommand);
        rootCommand.AddCommand(previewCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static IMailStoreReader GetMailStoreReader()
    {
        if (_injectedReader != null)
        {
            return _injectedReader;
        }

        try
        {
            string basePath = AppContext.BaseDirectory;
            string adapterDll = Path.Combine(basePath, "MailVault.Adapters.XstReader.dll");
            if (File.Exists(adapterDll))
            {
                var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(adapterDll);
                var type = assembly.GetType("MailVault.Adapters.XstReader.XstReaderMailStoreReader");
                if (type != null && Activator.CreateInstance(type) is IMailStoreReader reader)
                {
                    return reader;
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Erro ao inicializar dinamicamente o adapter XstReader: {ex.Message}", ex);
        }

        throw new InvalidOperationException("Nenhum adapter de leitura PST/OST foi encontrado na pasta de build. Certifique-se de que MailVault.Adapters.XstReader está compilado.");
    }

    private static async Task<(string sha256, string caseId, string caseFolderPath, string auditLogFilePath, DateTimeOffset startedAt)> InitializeCaseAsync(FileInfo file, DirectoryInfo outDir, string commandName)
    {
        var startedAt = DateTimeOffset.Now;
        var caseId = ManifestService.GenerateCaseId(startedAt);
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

        // Update manifest.json
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
            Environment.Exit(1);
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
            Environment.Exit(2);
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
            Environment.Exit(3);
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
            Environment.Exit(1);
        }

        var caseDetails = await InitializeCaseAsync(file, outDir, "tree");
        var auditWriter = new FileAuditTrailWriter(caseDetails.auditLogFilePath);

        var reader = GetMailStoreReader();
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
            Environment.Exit(4);
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
            Environment.Exit(1);
        }

        var caseDetails = await InitializeCaseAsync(file, outDir, "list");
        var auditWriter = new FileAuditTrailWriter(caseDetails.auditLogFilePath);

        var reader = GetMailStoreReader();
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
                if (idStr.Length > 15) idStr = idStr.Substring(idStr.Length - 15); // Show tail of the ID if long

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
            Environment.Exit(5);
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
            Environment.Exit(1);
        }

        var caseDetails = await InitializeCaseAsync(file, outDir, "preview");
        var auditWriter = new FileAuditTrailWriter(caseDetails.auditLogFilePath);

        var reader = GetMailStoreReader();
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
                Environment.Exit(6);
            }

            var m = result.Value;
            await auditWriter.WriteEventAsync(new AuditEvent(Guid.NewGuid().ToString(), DateTimeOffset.Now, "MessagePreviewed", Environment.UserName, $"Preview da mensagem {messageId} carregado."), CancellationToken.None);

            // Print metadata fields
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

            // Print Truncated Body for Security Compliance
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
            Environment.Exit(7);
        }
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
}

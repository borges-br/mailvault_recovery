using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Domain;

namespace MailVault.Core;

public sealed class ExportJobRunner : IExportJobRunner
{
    public async Task<ExportJobResult> RunExportJobAsync(
        ExportJobOptions options,
        ICaseIndexReader caseReader,
        IAdapterResolver adapterResolver,
        IMessageExporter exporter,
        IProgressReporter progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string exportId = $"EXP-{Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant()}";

        // 1. Load case metadata from relational store
        var caseInfo = await caseReader.GetCaseInfoAsync(ct);
        if (caseInfo == null)
        {
            throw new InvalidOperationException("Metadados do caso não encontrados no banco relacional case.db.");
        }

        // 2. Forensically verify original mail store file exists
        if (!File.Exists(caseInfo.SourceFile))
        {
            throw new FileNotFoundException($"Arquivo de evidência original não encontrado em: '{caseInfo.SourceFile}'. A exportação forense exige a presença da mídia original.", caseInfo.SourceFile);
        }

        // 3. Forensically recalculate and validate SHA-256 signature (Gate 2 - No bypass allowed!)
        progress.ReportProgress(5, "Verificando integridade forense do arquivo de origem...");
        var hashService = new HashService();
        string sha256 = await hashService.CalculateSha256Async(caseInfo.SourceFile, progress, ct);
        if (!string.Equals(sha256, caseInfo.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ASSINATURA DE INTEGRIDADE FALHOU: O hash SHA-256 da mídia atual ('{sha256}') não corresponde ao hash registrado no início do caso ('{caseInfo.SourceSha256}'). A exportação foi abortada para garantir a cadeia de custódia.");
        }

        // 4. Resolve and initialize adapter in read-only mode (Gate 11 - Clean Core isolation)
        progress.ReportProgress(10, "Inicializando driver do adaptador de correio...");
        var resolverResult = adapterResolver.ResolveAdapter(Path.GetExtension(caseInfo.SourceFile));
        if (!resolverResult.Success || resolverResult.Reader == null)
        {
            throw new InvalidOperationException($"Falha ao carregar o driver de leitura adequado: {resolverResult.ErrorMessage}");
        }

        var storeReader = resolverResult.Reader;
        // Inspect in-place ensuring read-only locks
        await storeReader.InspectAsync(caseInfo.SourceFile, ct);

        // 5. Gather index metadata of folders and messages to export
        progress.ReportProgress(15, "Carregando escopo de exportação do índice relacional...");
        var targetFolderId = !string.IsNullOrEmpty(options.FolderIdOrPath) ? new FolderId(options.FolderIdOrPath) : null;
        
        var roots = await caseReader.GetFolderHierarchyAsync(ct).ToListAsync(ct);
        var allFolders = new List<FolderNode>();
        FlattenFolderHierarchy(roots, allFolders);
        var selectedFolders = new List<FolderNode>();
        
        if (targetFolderId != null)
        {
            var targetFolder = allFolders.FirstOrDefault(f => f.Id.Value == targetFolderId.Value || f.FullPath == targetFolderId.Value);
            if (targetFolder == null)
            {
                throw new InvalidOperationException($"A pasta especificada '{targetFolderId.Value}' não existe no índice relacional.");
            }
            selectedFolders.Add(targetFolder);
            // Collect children if preserving structure
            CollectChildFoldersRecursive(targetFolder, allFolders, selectedFolders);
        }
        else
        {
            selectedFolders.AddRange(allFolders);
        }

        // Load targeted messages
        var targetedMessages = new List<(MailItem Msg, FolderId Folder)>();
        foreach (var folder in selectedFolders)
        {
            var msgs = await caseReader.GetMessagesInFolderAsync(folder.Id, int.MaxValue, 0, ct).ToListAsync(ct);
            foreach (var m in msgs)
            {
                targetedMessages.Add((m, folder.Id));
            }
        }

        // Apply Limit and Offset globally to the entire set of targeted messages (Gate 6/8 alignment)
        if (options.Offset.HasValue && options.Offset.Value > 0)
        {
            targetedMessages = targetedMessages.Skip(options.Offset.Value).ToList();
        }
        if (options.Limit.HasValue && options.Limit.Value >= 0)
        {
            targetedMessages = targetedMessages.Take(options.Limit.Value).ToList();
        }

        int messagesSelected = targetedMessages.Count;
        int foldersSelected = selectedFolders.Count;

        // 6. Execute Dry Run if requested
        if (options.DryRun)
        {
            stopwatch.Stop();
            return new ExportJobResult(
                ExportId: exportId,
                Format: options.Format,
                FoldersSelected: foldersSelected,
                MessagesSelected: messagesSelected,
                MessagesExported: 0,
                MessagesFailed: 0,
                AttachmentsExported: 0,
                AttachmentsFailed: 0,
                Issues: new List<ExtractionIssue>(),
                ExportedFiles: new List<string>(),
                DurationMs: stopwatch.ElapsedMilliseconds
            );
        }

        // 7. Initialize target directories and writers
        if (!Directory.Exists(options.OutputDir))
        {
            Directory.CreateDirectory(options.OutputDir);
        }

        int messagesExported = 0;
        int messagesFailed = 0;
        int attachmentsExported = 0;
        int attachmentsFailed = 0;
        var exportedFilesList = new List<string>();
        var issuesList = new List<ExtractionIssue>();
        var exportedMsgRecords = new List<ExportedMessageRecord>();

        var attachmentProvider = new AdapterAttachmentContentProvider(storeReader);

        progress.ReportProgress(25, $"Iniciando gravação técnica física ({options.Format.ToUpperInvariant()})...");

        if (options.Format.Equals("eml", StringComparison.OrdinalIgnoreCase))
        {
            int seq = 1;
            foreach (var item in targetedMessages)
            {
                ct.ThrowIfCancellationRequested();
                var folderNode = allFolders.First(f => f.Id.Value == item.Folder.Value);
                
                string targetDir = options.OutputDir;
                if (options.PreserveFolderStructure)
                {
                    string safeSubdir = string.Join("/", folderNode.FullPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Select(s => SanitiseFilename(s, "Folder")));
                    targetDir = Path.Combine(options.OutputDir, "eml", safeSubdir);
                }
                else
                {
                    targetDir = Path.Combine(options.OutputDir, "eml");
                }

                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                // Generate deterministic and safe file name (Gate 7)
                string safeSubject = SanitiseFilename(item.Msg.Subject ?? "(Sem Assunto)", "Email");
                string fileBaseName = $"{seq:D6}-{safeSubject}";
                string targetFilePath = Path.Combine(targetDir, $"{fileBaseName}.eml");

                // Security: Ensure no path traversal and we remain inside output directory base (Gate 7)
                EnsureSafeWritePath(targetFilePath, options.OutputDir);

                if (File.Exists(targetFilePath) && !options.Overwrite)
                {
                    issuesList.Add(new ExtractionIssue(
                        Code: "MV-ERR-EXPORT-OVERWRITE",
                        Severity: "Error",
                        Message: $"O arquivo de destino já existe e a opção --overwrite está inativa: '{targetFilePath}'",
                        ObjectId: item.Msg.InternalId,
                        TechnicalDetails: null
                    ));
                    messagesFailed++;
                    exportedMsgRecords.Add(new ExportedMessageRecord(item.Msg.InternalId, folderNode.FullPath, TruncateString(item.Msg.Subject ?? "(Sem Assunto)", 60), Path.GetRelativePath(options.OutputDir, targetFilePath), "Failed (Overwrite blocked)", item.Msg.Attachments.Count));
                    continue;
                }

                // Re-read full message details from read-only physical file (minimizaton of data on db)
                var readResult = await storeReader.GetMessageAsync(new MessageId(item.Msg.InternalId), ct);
                if (!readResult.Success || readResult.Value == null)
                {
                    string err = string.Join("; ", readResult.Issues.Select(i => i.Message));
                    issuesList.Add(new ExtractionIssue(
                        Code: "MV-ERR-EXPORT-READMSG",
                        Severity: "Error",
                        Message: $"Falha ao ler mensagem original do PST/OST: {err}",
                        ObjectId: item.Msg.InternalId,
                        TechnicalDetails: null
                    ));
                    messagesFailed++;
                    exportedMsgRecords.Add(new ExportedMessageRecord(item.Msg.InternalId, folderNode.FullPath, TruncateString(item.Msg.Subject ?? "(Sem Assunto)", 60), Path.GetRelativePath(options.OutputDir, targetFilePath), "Failed (PST Read)", item.Msg.Attachments.Count));
                    continue;
                }

                var fullMail = readResult.Value!;

                try
                {
                    using (var fs = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await exporter.ExportMessageAsync(fullMail, attachmentProvider, fs, ct);
                    }

                    messagesExported++;
                    exportedFilesList.Add(targetFilePath);
                    exportedMsgRecords.Add(new ExportedMessageRecord(fullMail.InternalId, folderNode.FullPath, TruncateString(fullMail.Subject ?? "(Sem Assunto)", 60), Path.GetRelativePath(options.OutputDir, targetFilePath), "Success", fullMail.Attachments.Count));

                    // Process attachments extraction if requested
                    if (options.IncludeAttachments && options.ExtractAttachments && fullMail.Attachments.Count > 0)
                    {
                        string attDirName = $"{fileBaseName}-attachments";
                        string attPathDir = Path.Combine(targetDir, attDirName);
                        if (!Directory.Exists(attPathDir))
                        {
                            Directory.CreateDirectory(attPathDir);
                        }

                        foreach (var att in fullMail.Attachments)
                        {
                            string safeAttName = SanitiseFilename(att.FileName ?? $"anexo-{att.InternalId}", "anexo");
                            string attTargetPath = Path.Combine(attPathDir, safeAttName);

                            EnsureSafeWritePath(attTargetPath, options.OutputDir);

                            try
                            {
                                using (var attStream = await storeReader.OpenAttachmentStreamAsync(new MessageId(fullMail.InternalId), new AttachmentId(att.InternalId), ct))
                                using (var attOutFs = new FileStream(attTargetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    await attStream.CopyToAsync(attOutFs, ct);
                                }
                                attachmentsExported++;
                                exportedFilesList.Add(attTargetPath);
                            }
                            catch (Exception attEx)
                            {
                                attachmentsFailed++;
                                issuesList.Add(new ExtractionIssue(
                                    Code: "MV-ERR-EXPORT-ATTACH",
                                    Severity: "Warning",
                                    Message: $"Falha ao exportar anexo avulso '{att.FileName}': {attEx.Message}",
                                    ObjectId: att.InternalId,
                                    TechnicalDetails: attEx.ToString()
                                ));
                            }
                        }
                    }
                    else if (options.IncludeAttachments)
                    {
                        attachmentsExported += fullMail.Attachments.Count; // Count attachments embedded
                    }
                }
                catch (Exception ex)
                {
                    messagesFailed++;
                    issuesList.Add(new ExtractionIssue(
                        Code: "MV-ERR-EXPORT-WRITEEML",
                        Severity: "Error",
                        Message: $"Falha técnica ao escrever EML em disco: {ex.Message}",
                        ObjectId: item.Msg.InternalId,
                        TechnicalDetails: ex.ToString()
                    ));
                    exportedMsgRecords.Add(new ExportedMessageRecord(item.Msg.InternalId, folderNode.FullPath, TruncateString(item.Msg.Subject ?? "(Sem Assunto)", 60), Path.GetRelativePath(options.OutputDir, targetFilePath), "Failed (Write)", item.Msg.Attachments.Count));
                }

                seq++;
                double percentage = 25 + (65.0 * seq / messagesSelected);
                progress.ReportProgress(percentage, $"Exportado e-mail {seq} de {messagesSelected}...");
            }
        }
        else if (options.Format.Equals("mbox", StringComparison.OrdinalIgnoreCase))
        {
            // Group by folder to export incrementally per folder (Gate 6)
            var groups = targetedMessages.GroupBy(t => t.Folder).ToList();
            int seq = 1;

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                var folderNode = allFolders.First(f => f.Id.Value == group.Key.Value);
                
                string targetDir = options.OutputDir;
                if (options.PreserveFolderStructure)
                {
                    string safeSubdir = string.Join("/", folderNode.FullPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Select(s => SanitiseFilename(s, "Folder")));
                    targetDir = Path.Combine(options.OutputDir, "mbox", safeSubdir);
                }
                else
                {
                    targetDir = Path.Combine(options.OutputDir, "mbox");
                }

                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                string targetFilePath = Path.Combine(targetDir, "mbox");
                EnsureSafeWritePath(targetFilePath, options.OutputDir);

                if (File.Exists(targetFilePath) && !options.Overwrite)
                {
                    issuesList.Add(new ExtractionIssue(
                        Code: "MV-ERR-EXPORT-OVERWRITE",
                        Severity: "Error",
                        Message: $"O arquivo mbox já existe e a opção --overwrite está inativa em: '{targetFilePath}'",
                        ObjectId: folderNode.Id.Value,
                        TechnicalDetails: null
                    ));
                    messagesFailed += group.Count();
                    foreach (var item in group)
                    {
                        exportedMsgRecords.Add(new ExportedMessageRecord(item.Msg.InternalId, folderNode.FullPath, TruncateString(item.Msg.Subject ?? "(Sem Assunto)", 60), Path.GetRelativePath(options.OutputDir, targetFilePath), "Failed (Overwrite blocked)", item.Msg.Attachments.Count));
                    }
                    continue;
                }

                // Incremental file writing session per folder (Gate 6 - Never load entire mbox in memory)
                try
                {
                    using (var mboxFs = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        foreach (var item in group)
                        {
                            ct.ThrowIfCancellationRequested();
                            
                            var readResult = await storeReader.GetMessageAsync(new MessageId(item.Msg.InternalId), ct);
                            if (!readResult.Success || readResult.Value == null)
                            {
                                string err = string.Join("; ", readResult.Issues.Select(i => i.Message));
                                issuesList.Add(new ExtractionIssue(
                                    Code: "MV-ERR-EXPORT-READMSG",
                                    Severity: "Error",
                                    Message: $"Falha ao ler mensagem original do PST/OST para MBOX: {err}",
                                    ObjectId: item.Msg.InternalId,
                                    TechnicalDetails: null
                                ));
                                messagesFailed++;
                                exportedMsgRecords.Add(new ExportedMessageRecord(item.Msg.InternalId, folderNode.FullPath, TruncateString(item.Msg.Subject ?? "(Sem Assunto)", 60), string.Empty, "Failed (PST Read)", item.Msg.Attachments.Count));
                                continue;
                            }

                            var fullMail = readResult.Value!;

                            try
                            {
                                await exporter.ExportMessageAsync(fullMail, attachmentProvider, mboxFs, ct);
                                messagesExported++;
                                if (options.IncludeAttachments)
                                {
                                    attachmentsExported += fullMail.Attachments.Count;
                                }
                                exportedMsgRecords.Add(new ExportedMessageRecord(fullMail.InternalId, folderNode.FullPath, TruncateString(fullMail.Subject ?? "(Sem Assunto)", 60), Path.GetRelativePath(options.OutputDir, targetFilePath), "Success", fullMail.Attachments.Count));
                            }
                            catch (Exception mboxItemEx)
                            {
                                messagesFailed++;
                                issuesList.Add(new ExtractionIssue(
                                    Code: "MV-ERR-EXPORT-WRITEMBOX-ITEM",
                                    Severity: "Error",
                                    Message: $"Falha técnica ao adicionar item no MBOX: {mboxItemEx.Message}",
                                    ObjectId: fullMail.InternalId,
                                    TechnicalDetails: mboxItemEx.ToString()
                                ));
                                exportedMsgRecords.Add(new ExportedMessageRecord(fullMail.InternalId, folderNode.FullPath, TruncateString(fullMail.Subject ?? "(Sem Assunto)", 60), Path.GetRelativePath(options.OutputDir, targetFilePath), "Failed (Write Item)", fullMail.Attachments.Count));
                            }

                            seq++;
                            double percentage = 25 + (65.0 * seq / messagesSelected);
                            progress.ReportProgress(percentage, $"Exportado e-mail {seq} de {messagesSelected} para MBOX...");
                        }
                    }

                    exportedFilesList.Add(targetFilePath);
                }
                catch (Exception mboxEx)
                {
                    issuesList.Add(new ExtractionIssue(
                        Code: "MV-ERR-EXPORT-WRITEMBOX",
                        Severity: "Error",
                        Message: $"Falha técnica ao iniciar gravação do MBOX: {mboxEx.Message}",
                        ObjectId: folderNode.Id.Value,
                        TechnicalDetails: mboxEx.ToString()
                    ));
                }
            }
        }

        stopwatch.Stop();

        // 8. Generate export-manifest.json (Gate 10 - No body, raw headers or private user info)
        progress.ReportProgress(95, "Gerando manifesto forense da exportação...");
        string manifestPath = Path.Combine(options.OutputDir, "export-manifest.json");

        var manifest = new ExportManifest(
            ExportId: exportId,
            CaseId: caseInfo.CaseId,
            SourceFile: caseInfo.SourceFile,
            SourceSha256: caseInfo.SourceSha256,
            AdapterName: caseInfo.AdapterName,
            AdapterVersion: caseInfo.AdapterVersion,
            ExportFormat: options.Format.ToLowerInvariant(),
            StartedAt: DateTimeOffset.Now.AddMilliseconds(-stopwatch.ElapsedMilliseconds),
            CompletedAt: DateTimeOffset.Now,
            OutputDirectory: options.OutputDir,
            FoldersSelected: foldersSelected,
            MessagesSelected: messagesSelected,
            MessagesExported: messagesExported,
            MessagesFailed: messagesFailed,
            AttachmentsExported: attachmentsExported,
            AttachmentsFailed: attachmentsFailed,
            Issues: issuesList,
            ExportedMessages: exportedMsgRecords,
            ToolVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0"
        );

        string json = System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, json, ct);
        exportedFilesList.Add(manifestPath);

        return new ExportJobResult(
            ExportId: exportId,
            Format: options.Format,
            FoldersSelected: foldersSelected,
            MessagesSelected: messagesSelected,
            MessagesExported: messagesExported,
            MessagesFailed: messagesFailed,
            AttachmentsExported: attachmentsExported,
            AttachmentsFailed: attachmentsFailed,
            Issues: issuesList,
            ExportedFiles: exportedFilesList,
            DurationMs: stopwatch.ElapsedMilliseconds
        );
    }

    private void CollectChildFoldersRecursive(FolderNode current, List<FolderNode> source, List<FolderNode> target)
    {
        foreach (var sub in current.Children)
        {
            var matched = source.FirstOrDefault(f => f.Id.Value == sub.Id.Value);
            if (matched != null && !target.Any(t => t.Id.Value == matched.Id.Value))
            {
                target.Add(matched);
                CollectChildFoldersRecursive(matched, source, target);
            }
        }
    }

    private void FlattenFolderHierarchy(IEnumerable<FolderNode> nodes, List<FolderNode> target)
    {
        foreach (var node in nodes)
        {
            target.Add(node);
            FlattenFolderHierarchy(node.Children, target);
        }
    }

    public static string SanitiseFilename(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        // Replace path traversal blocks first
        string clean = name.Replace("../", "_").Replace("..\\", "_").Replace("..", "_");

        // Remove invalid chars
        var invalidChars = Path.GetInvalidFileNameChars();
        clean = new string(clean.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());

        // Truncate to reasonable length (80 chars)
        if (clean.Length > 80)
        {
            clean = clean.Substring(0, 77) + "...";
        }

        return clean.Trim();
    }

    public static void EnsureSafeWritePath(string targetPath, string baseOutputDir)
    {
        string fullTarget = Path.GetFullPath(targetPath);
        string fullBase = Path.GetFullPath(baseOutputDir);

        if (!fullTarget.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"TENTATIVA DE VIOLAÇÃO DE DIRETÓRIO DETECTADA: O caminho de gravação '{fullTarget}' está fora do diretório de exportação base homologado '{fullBase}'.");
        }
    }

    private static string TruncateString(string input, int maxLen)
    {
        if (input.Length <= maxLen) return input;
        return input.Substring(0, maxLen - 3) + "...";
    }

    private sealed class AdapterAttachmentContentProvider : IAttachmentContentProvider
    {
        private readonly IMailStoreReader _reader;
        public AdapterAttachmentContentProvider(IMailStoreReader reader) => _reader = reader;
        
        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct)
        {
            return _reader.OpenAttachmentStreamAsync(messageId, attachmentId, ct);
        }
    }
}

public static class ListExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source, CancellationToken ct)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(ct))
        {
            list.Add(item);
        }
        return list;
    }
}

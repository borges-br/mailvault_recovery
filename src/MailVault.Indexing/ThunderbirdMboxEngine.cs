using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using MimeKit;

namespace MailVault.Indexing;

public sealed class ThunderbirdMboxEngine : IReaderEngine
{
    public ReaderEngineDescriptor Descriptor => new ReaderEngineDescriptor(
        "ThunderbirdMbox",
        "1.0.0",
        "Motor de recuperação de e-mails para perfis do Mozilla Thunderbird e arquivos MBOX."
    );

    public string PrecalculatedSha256 { get; set; } = string.Empty;

    public Task<ReaderEngineCapability> CheckCapabilityAsync(CancellationToken ct)
    {
        return Task.FromResult(new ReaderEngineCapability(true, "Embedded Assembly", "1.0.0"));
    }

    public async Task<ReaderEngineResult> IndexAsync(
        string filePath,
        ICaseIndexStore store,
        string caseId,
        string operatorName,
        bool cachePreview,
        int? limit,
        IProgress<IndexingProgress>? progress,
        CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int foldersIndexed = 0;
        int messagesIndexed = 0;
        int attachmentsIndexed = 0;
        int issuesDetected = 0;

        string targetPath = filePath;
        
        // 1. Check if path is profiles.ini, if so, resolve to the default profile directory
        if (Path.GetFileName(filePath).Equals("profiles.ini", StringComparison.OrdinalIgnoreCase))
        {
            targetPath = ResolveProfileFromIni(filePath);
        }

        if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
        {
            throw new DirectoryNotFoundException($"Caminho Thunderbird ou MBOX não encontrado: '{targetPath}'");
        }

        // Calculate SHA-256 (best-effort placeholder for folders)
        string sha256 = PrecalculatedSha256;
        if (string.IsNullOrEmpty(sha256))
        {
            sha256 = "thunderbird-mbox-folder-hash";
        }

        using var writer = store.CreateWriter();
        await writer.BeginTransactionAsync(ct);
        try
        {
            await writer.SaveCaseInfoAsync(caseId, filePath, 0, sha256, operatorName, DateTimeOffset.Now, Descriptor.Name, Descriptor.Version, ct);
            await writer.SaveIndexRunAsync(Guid.NewGuid().ToString("N"), caseId, DateTime.UtcNow, "Running", 0, 0, 0, 0, 0, ct);
            await writer.CommitTransactionAsync(CancellationToken.None);
        }
        catch
        {
            await writer.RollbackTransactionAsync(CancellationToken.None);
            throw;
        }

        // 2. Discover MBOX files and their logical paths
        var mboxEntries = new List<MboxDiscoveryEntry>();
        if (Directory.Exists(targetPath))
        {
            DiscoverMboxFilesRecursive(new DirectoryInfo(targetPath), "", mboxEntries);
        }
        else if (File.Exists(targetPath))
        {
            // Direct single MBOX file index
            mboxEntries.Add(new MboxDiscoveryEntry(new FileInfo(targetPath), Path.GetFileNameWithoutExtension(targetPath), ""));
        }

        progress?.Report(new IndexingProgress("Indexing", "", 0, 0, 0, 0, 10.0, stopwatch.Elapsed, $"Localizados {mboxEntries.Count} arquivos MBOX para indexação.", true));

        // 3. Process each MBOX file sequentially
        var savedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in mboxEntries)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new IndexingProgress("Indexing", entry.LogicalPath, foldersIndexed, messagesIndexed, attachmentsIndexed, issuesDetected, null, stopwatch.Elapsed, $"Indexando pasta: {entry.LogicalPath}", true));

            var folderNode = new FolderNode(
                Id: new FolderId(entry.LogicalPath),
                ParentId: string.IsNullOrEmpty(entry.ParentLogicalPath) ? null : new FolderId(entry.ParentLogicalPath),
                DisplayName: entry.DisplayName,
                FullPath: entry.LogicalPath,
                MessageCount: 0,
                Children: new List<FolderNode>()
            );

            await writer.BeginTransactionAsync(ct);
            try
            {
                if (!string.IsNullOrEmpty(entry.ParentLogicalPath))
                {
                    await EnsureParentFoldersSavedAsync(entry.ParentLogicalPath, writer, savedFolders, ct);
                }

                await writer.SaveFolderAsync(folderNode, ct);
                savedFolders.Add(entry.LogicalPath);
                foldersIndexed++;
                await writer.CommitTransactionAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                await writer.RollbackTransactionAsync(CancellationToken.None);
                issuesDetected++;
                await writer.BeginTransactionAsync(ct);
                await writer.SaveIssueAsync(new ExtractionIssue("MV-ERR-TB-FOLDER-SAVE", "Error", $"Falha ao salvar pasta lógica '{entry.LogicalPath}': {ex.Message}", entry.LogicalPath, ex.ToString()), ct);
                await writer.CommitTransactionAsync(CancellationToken.None);
                continue;
            }

            // Stream read MBOX file using MimeParser
            try
            {
                using var fs = new FileStream(entry.File.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var parser = new MimeParser(fs, MimeFormat.Mbox);
                // Use a sequential counter (not stream position) for unique message IDs.
                // fs.Position is unreliable after MimeKit's buffered reads.
                int fileMessageSeq = 0;
                int count = 0;
                int batchCount = 0;

                await writer.BeginTransactionAsync(ct);
                while (!parser.IsEndOfStream)
                {
                    ct.ThrowIfCancellationRequested();
                    if (limit.HasValue && messagesIndexed >= limit.Value)
                    {
                        break;
                    }

                    MimeMessage message;
                    try
                    {
                        message = await parser.ParseMessageAsync(ct);
                    }
                    catch (Exception parseEx)
                    {
                        issuesDetected++;
                        await writer.SaveIssueAsync(new ExtractionIssue("MV-ERR-TB-MSG-PARSE", "Warning", $"Falha ao ler mensagem #{fileMessageSeq} de '{entry.File.Name}': {parseEx.Message}", entry.LogicalPath, parseEx.ToString()), ct);
                        fileMessageSeq++;
                        continue;
                    }

                    try
                    {
                        var mailItem = MapMimeMessageToMailItem(message, entry.LogicalPath, fileMessageSeq);
                        fileMessageSeq++;
                        await writer.SaveMessageAsync(mailItem, folderNode.Id, ct);
                        messagesIndexed++;
                        attachmentsIndexed += mailItem.Attachments.Count;
                        count++;
                        batchCount++;

                        if (batchCount >= 100)
                        {
                            await writer.CommitTransactionAsync(CancellationToken.None);
                            batchCount = 0;
                            progress?.Report(new IndexingProgress("Indexing", entry.LogicalPath, foldersIndexed, messagesIndexed, attachmentsIndexed, issuesDetected, null, stopwatch.Elapsed, $"Indexando pasta: {entry.LogicalPath} ({count} mensagens)", true));
                            await writer.BeginTransactionAsync(ct);
                        }
                    }
                    catch (Exception mapEx)
                    {
                        issuesDetected++;
                        await writer.SaveIssueAsync(new ExtractionIssue("MV-ERR-TB-MSG-MAP", "Warning", $"Falha ao salvar mensagem #{fileMessageSeq} de '{entry.File.Name}': {mapEx.Message}", entry.LogicalPath, mapEx.ToString()), ct);
                        fileMessageSeq++;
                    }
                }

                if (batchCount > 0)
                {
                    await writer.CommitTransactionAsync(CancellationToken.None);
                }
                else
                {
                    await writer.RollbackTransactionAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                issuesDetected++;
                await writer.BeginTransactionAsync(ct);
                await writer.SaveIssueAsync(new ExtractionIssue("MV-ERR-TB-MBOX-READ", "Error", $"Falha ao abrir ou ler arquivo MBOX '{entry.File.FullName}': {ex.Message}", entry.LogicalPath, ex.ToString()), ct);
                await writer.CommitTransactionAsync(CancellationToken.None);
            }
        }

        stopwatch.Stop();

        // Finalize index_run
        await writer.BeginTransactionAsync(CancellationToken.None);
        try
        {
            await writer.SaveIndexRunAsync(
                Guid.NewGuid().ToString("N"),
                caseId,
                DateTime.UtcNow,
                "Success",
                stopwatch.ElapsedMilliseconds,
                foldersIndexed,
                messagesIndexed,
                attachmentsIndexed,
                issuesDetected,
                CancellationToken.None);
            await writer.CommitTransactionAsync(CancellationToken.None);
        }
        catch
        {
            await writer.RollbackTransactionAsync(CancellationToken.None);
        }

        return new ReaderEngineResult(
            "Success",
            null,
            foldersIndexed,
            messagesIndexed,
            attachmentsIndexed,
            issuesDetected
        );
    }

    private static string ResolveProfileFromIni(string iniPath)
    {
        try
        {
            var lines = File.ReadAllLines(iniPath);
            string? relativePath = null;
            string? isRelative = null;

            foreach (var line in lines)
            {
                if (line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = line.Substring(5).Trim();
                }
                else if (line.StartsWith("IsRelative=", StringComparison.OrdinalIgnoreCase))
                {
                    isRelative = line.Substring(11).Trim();
                }

                if (relativePath != null && isRelative != null)
                {
                    if (isRelative == "1")
                    {
                        return Path.Combine(Path.GetDirectoryName(iniPath)!, relativePath.Replace('/', '\\'));
                    }
                    return relativePath;
                }
            }
            if (relativePath != null) return relativePath;
        }
        catch
        {
            // fallback to directory of iniPath
        }
        return Path.GetDirectoryName(iniPath)!;
    }

    private static void DiscoverMboxFilesRecursive(DirectoryInfo dir, string parentLogicalPath, List<MboxDiscoveryEntry> mboxEntries)
    {
        var files = dir.GetFiles("*", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            // Ignore .msf and helper files
            if (file.Extension.Equals(".msf", StringComparison.OrdinalIgnoreCase) ||
                file.Name.Equals("msgFilterRules.dat", StringComparison.OrdinalIgnoreCase) ||
                file.Name.Equals("filterlog.html", StringComparison.OrdinalIgnoreCase) ||
                file.Name.Equals("popstate.dat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // MBOX is typically extensionless or ends in .mbox, and has non-zero size, is not a directory
            bool isMbox = false;
            if (file.Extension.Equals(".mbox", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(file.Extension))
            {
                // Verify by reading first 100 bytes to see if it starts with "From "
                try
                {
                    using var fs = file.OpenRead();
                    byte[] buffer = new byte[5];
                    int read = fs.Read(buffer, 0, 5);
                    if (read == 5 && Encoding.UTF8.GetString(buffer).Equals("From "))
                    {
                        isMbox = true;
                    }
                }
                catch
                {
                    // Ignore verify failures
                }
            }

            if (isMbox)
            {
                string logicalPath = string.IsNullOrEmpty(parentLogicalPath) ? file.Name : $"{parentLogicalPath}/{file.Name}";
                mboxEntries.Add(new MboxDiscoveryEntry(file, file.Name, parentLogicalPath));
            }
        }

        var subDirs = dir.GetDirectories("*", SearchOption.TopDirectoryOnly);
        foreach (var sub in subDirs)
        {
            if (sub.Name.EndsWith(".sbd", StringComparison.OrdinalIgnoreCase))
            {
                string folderName = Path.GetFileNameWithoutExtension(sub.Name);
                string currentLogicalPath = string.IsNullOrEmpty(parentLogicalPath) ? folderName : $"{parentLogicalPath}/{folderName}";
                DiscoverMboxFilesRecursive(sub, currentLogicalPath, mboxEntries);
            }
            else if (sub.Name.Equals("Mail", StringComparison.OrdinalIgnoreCase) || 
                     sub.Name.Equals("ImapMail", StringComparison.OrdinalIgnoreCase))
            {
                DiscoverMboxFilesRecursive(sub, parentLogicalPath, mboxEntries);
            }
            else if (dir.Name.Equals("Mail", StringComparison.OrdinalIgnoreCase) || 
                     dir.Name.Equals("ImapMail", StringComparison.OrdinalIgnoreCase))
            {
                string currentLogicalPath = string.IsNullOrEmpty(parentLogicalPath) ? sub.Name : $"{parentLogicalPath}/{sub.Name}";
                DiscoverMboxFilesRecursive(sub, currentLogicalPath, mboxEntries);
            }
        }
    }

    private static MailItem MapMimeMessageToMailItem(MimeMessage message, string folderLogicalPath, int seq)
    {
        string internalId = $"{folderLogicalPath}/seq-{seq}";

        var fromRef = message.From.Mailboxes.Select(m => new MailAddressRef(m.Name, m.Address)).FirstOrDefault();
        var toList = message.To.Mailboxes.Select(m => new MailAddressRef(m.Name, m.Address)).ToList();
        var ccList = message.Cc.Mailboxes.Select(m => new MailAddressRef(m.Name, m.Address)).ToList();
        var bccList = message.Bcc.Mailboxes.Select(m => new MailAddressRef(m.Name, m.Address)).ToList();

        var attachList = new List<AttachmentRef>();
        int attIdx = 1;
        foreach (var bodyPart in message.BodyParts)
        {
            if (bodyPart.IsAttachment)
            {
                string fileName = bodyPart.ContentType.Name ?? $"anexo-{attIdx}";
                long size = 0;
                if (bodyPart is MimePart part && part.Content != null)
                {
                    // Estimated size
                    size = part.Content.Stream.Length;
                }

                attachList.Add(new AttachmentRef(
                    InternalId: $"{internalId}/att-{attIdx}",
                    FileName: fileName,
                    ContentType: bodyPart.ContentType.MimeType,
                    SizeBytes: size,
                    ContentId: bodyPart.ContentId,
                    IsInline: false
                ));
                attIdx++;
            }
        }

        var rawProperties = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(message.InReplyTo))
        {
            rawProperties["PR_IN_REPLY_TO_ID"] = message.InReplyTo;
        }

        // Format headers block for PR_TRANSPORT_MESSAGE_HEADERS
        var headersBuilder = new StringBuilder();
        foreach (var h in message.Headers)
        {
            headersBuilder.AppendLine($"{h.Field}: {h.Value}");
        }
        rawProperties["PR_TRANSPORT_MESSAGE_HEADERS"] = headersBuilder.ToString();

        return new MailItem(
            InternalId: internalId,
            InternetMessageId: message.MessageId,
            Subject: message.Subject,
            From: fromRef,
            To: toList,
            Cc: ccList,
            Bcc: bccList,
            SentAt: message.Date,
            ReceivedAt: message.Date,
            PlainTextBody: message.TextBody,
            HtmlBody: message.HtmlBody,
            Attachments: attachList,
            RawProperties: rawProperties,
            Issues: new List<ExtractionIssue>()
        );
    }

    /// <summary>
    /// Recursively ensures that every ancestor folder in the logical path hierarchy is saved to the DB
    /// before the child entry is saved, preventing SQLite FOREIGN KEY constraint violations.
    /// </summary>
    private static async Task EnsureParentFoldersSavedAsync(
        string? parentPath,
        ICaseIndexWriter writer,
        HashSet<string> savedFolders,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(parentPath) || savedFolders.Contains(parentPath))
        {
            return;
        }

        int slashIdx = parentPath.LastIndexOf('/');
        string? grandparentPath = slashIdx >= 0 ? parentPath.Substring(0, slashIdx) : null;

        // Recurse up before saving this level
        await EnsureParentFoldersSavedAsync(grandparentPath, writer, savedFolders, ct);

        string displayName = slashIdx >= 0 ? parentPath.Substring(slashIdx + 1) : parentPath;
        var parentNode = new FolderNode(
            Id: new FolderId(parentPath),
            ParentId: string.IsNullOrEmpty(grandparentPath) ? null : new FolderId(grandparentPath),
            DisplayName: displayName,
            FullPath: parentPath,
            MessageCount: 0,
            Children: new List<FolderNode>()
        );

        await writer.SaveFolderAsync(parentNode, ct);
        savedFolders.Add(parentPath);
    }

    private class MboxDiscoveryEntry
    {
        public FileInfo File { get; }
        public string DisplayName { get; }
        public string ParentLogicalPath { get; }
        public string LogicalPath => string.IsNullOrEmpty(ParentLogicalPath) ? DisplayName : $"{ParentLogicalPath}/{DisplayName}";

        public MboxDiscoveryEntry(FileInfo file, string displayName, string parentLogicalPath)
        {
            File = file;
            DisplayName = displayName;
            ParentLogicalPath = parentLogicalPath;
        }
    }
}

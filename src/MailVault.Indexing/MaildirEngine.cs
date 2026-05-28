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

public sealed class MaildirEngine : IReaderEngine
{
    public ReaderEngineDescriptor Descriptor => new ReaderEngineDescriptor(
        "Maildir",
        "1.0.0",
        "Motor de recuperação de e-mails para estruturas de diretórios Maildir."
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

        if (!Directory.Exists(filePath))
        {
            throw new DirectoryNotFoundException($"Diretório Maildir não encontrado: '{filePath}'");
        }

        string sha256 = PrecalculatedSha256;
        if (string.IsNullOrEmpty(sha256))
        {
            sha256 = "maildir-folder-hash";
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

        // Discover Maildir subfolders containing cur/ and new/
        var maildirDirs = new List<MaildirFolderEntry>();
        
        // Check if root is a Maildir itself
        if (IsMaildirFolder(filePath))
        {
            maildirDirs.Add(new MaildirFolderEntry(filePath, "Inbox", ""));
        }

        // Recursively search for other Maildir folders
        FindMaildirFoldersRecursive(new DirectoryInfo(filePath), "", maildirDirs);

        if (maildirDirs.Count == 0)
        {
            // If none found recursively, assume root directory is just containing emails directly or scan anyway
            maildirDirs.Add(new MaildirFolderEntry(filePath, "Root", ""));
        }

        progress?.Report(new IndexingProgress("Indexing", "", 0, 0, 0, 0, 10.0, stopwatch.Elapsed, $"Localizados {maildirDirs.Count} diretórios Maildir para indexação.", true));

        foreach (var entry in maildirDirs)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new IndexingProgress("Indexing", entry.LogicalPath, foldersIndexed, messagesIndexed, attachmentsIndexed, issuesDetected, null, stopwatch.Elapsed, $"Indexando pasta Maildir: {entry.LogicalPath}", true));

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
                await writer.SaveFolderAsync(folderNode, ct);
                foldersIndexed++;
                await writer.CommitTransactionAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                await writer.RollbackTransactionAsync(CancellationToken.None);
                issuesDetected++;
                await writer.BeginTransactionAsync(ct);
                await writer.SaveIssueAsync(new ExtractionIssue("MV-ERR-MD-FOLDER-SAVE", "Error", $"Falha ao salvar pasta lógica '{entry.LogicalPath}': {ex.Message}", entry.LogicalPath, ex.ToString()), ct);
                await writer.CommitTransactionAsync(CancellationToken.None);
                continue;
            }

            // Gather files from new and cur, ignoring tmp
            var filesToProcess = new List<FileInfo>();
            string curDir = Path.Combine(entry.Path, "cur");
            string newDir = Path.Combine(entry.Path, "new");

            if (Directory.Exists(curDir))
            {
                filesToProcess.AddRange(new DirectoryInfo(curDir).GetFiles());
            }
            if (Directory.Exists(newDir))
            {
                filesToProcess.AddRange(new DirectoryInfo(newDir).GetFiles());
            }

            int count = 0;
            int batchCount = 0;

            await writer.BeginTransactionAsync(ct);
            foreach (var file in filesToProcess)
            {
                ct.ThrowIfCancellationRequested();
                if (limit.HasValue && messagesIndexed >= limit.Value)
                {
                    break;
                }

                try
                {
                    using var fs = file.OpenRead();
                    var message = await MimeMessage.LoadAsync(fs, ct);

                    // Parse flags from Maildir filename
                    string flags = ParseMaildirFlags(file.Name);

                    var mailItem = MapMimeMessageToMailItem(message, entry.LogicalPath, file.Name, flags);
                    await writer.SaveMessageAsync(mailItem, folderNode.Id, ct);
                    messagesIndexed++;
                    attachmentsIndexed += mailItem.Attachments.Count;
                    count++;
                    batchCount++;

                    if (batchCount >= 100)
                    {
                        await writer.CommitTransactionAsync(CancellationToken.None);
                        batchCount = 0;
                        progress?.Report(new IndexingProgress("Indexing", entry.LogicalPath, foldersIndexed, messagesIndexed, attachmentsIndexed, issuesDetected, null, stopwatch.Elapsed, $"Indexando pasta Maildir: {entry.LogicalPath} ({count} mensagens)", true));
                        await writer.BeginTransactionAsync(ct);
                    }
                }
                catch (Exception ex)
                {
                    issuesDetected++;
                    await writer.SaveIssueAsync(new ExtractionIssue("MV-ERR-MD-MSG-PARSE", "Warning", $"Falha ao ler mensagem de Maildir '{file.FullName}': {ex.Message}", entry.LogicalPath, ex.ToString()), ct);
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

    private static bool IsMaildirFolder(string path)
    {
        return Directory.Exists(Path.Combine(path, "cur")) || Directory.Exists(Path.Combine(path, "new"));
    }

    private static void FindMaildirFoldersRecursive(DirectoryInfo dir, string parentLogicalPath, List<MaildirFolderEntry> maildirDirs)
    {
        foreach (var sub in dir.GetDirectories())
        {
            if (sub.Name.Equals("cur", StringComparison.OrdinalIgnoreCase) ||
                sub.Name.Equals("new", StringComparison.OrdinalIgnoreCase) ||
                sub.Name.Equals("tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsMaildirFolder(sub.FullName))
            {
                string logicalPath = string.IsNullOrEmpty(parentLogicalPath) ? sub.Name : $"{parentLogicalPath}/{sub.Name}";
                maildirDirs.Add(new MaildirFolderEntry(sub.FullName, sub.Name, parentLogicalPath));
            }
            else
            {
                string logicalPath = string.IsNullOrEmpty(parentLogicalPath) ? sub.Name : $"{parentLogicalPath}/{sub.Name}";
                FindMaildirFoldersRecursive(sub, logicalPath, maildirDirs);
            }
        }
    }

    private static string ParseMaildirFlags(string filename)
    {
        // Filename format: <unique_id>:2,<flags> or <unique_id>;2,<flags> (Windows safe)
        int idx = filename.LastIndexOf(":2,");
        if (idx < 0)
        {
            idx = filename.LastIndexOf(";2,");
        }
        if (idx >= 0 && idx + 3 < filename.Length)
        {
            string flagPart = filename.Substring(idx + 3);
            var flagList = new List<string>();
            foreach (char c in flagPart)
            {
                switch (c)
                {
                    case 'P': flagList.Add("Passed"); break;
                    case 'R': flagList.Add("Replied"); break;
                    case 'S': flagList.Add("Seen"); break;
                    case 'T': flagList.Add("Trashed"); break;
                    case 'D': flagList.Add("Draft"); break;
                    case 'F': flagList.Add("Flagged"); break;
                }
            }
            return string.Join("; ", flagList);
        }
        return string.Empty;
    }

    private static MailItem MapMimeMessageToMailItem(MimeMessage message, string folderLogicalPath, string filename, string flags)
    {
        string internalId = $"{folderLogicalPath}/{filename}";

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
        if (!string.IsNullOrEmpty(flags))
        {
            rawProperties["MaildirFlags"] = flags;
        }
        if (!string.IsNullOrEmpty(message.InReplyTo))
        {
            rawProperties["PR_IN_REPLY_TO_ID"] = message.InReplyTo;
        }

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

    private class MaildirFolderEntry
    {
        public string Path { get; }
        public string DisplayName { get; }
        public string ParentLogicalPath { get; }
        public string LogicalPath => string.IsNullOrEmpty(ParentLogicalPath) ? DisplayName : $"{ParentLogicalPath}/{DisplayName}";

        public MaildirFolderEntry(string path, string displayName, string parentLogicalPath)
        {
            Path = path;
            DisplayName = displayName;
            ParentLogicalPath = parentLogicalPath;
        }
    }
}

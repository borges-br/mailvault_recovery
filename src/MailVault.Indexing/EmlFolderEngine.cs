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

public sealed class EmlFolderEngine : IReaderEngine
{
    public ReaderEngineDescriptor Descriptor => new ReaderEngineDescriptor(
        "EmlFolder",
        "1.0.0",
        "Motor de recuperação de e-mails para pastas contendo arquivos EML."
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
            throw new DirectoryNotFoundException($"Pasta EML não encontrada: '{filePath}'");
        }

        string sha256 = PrecalculatedSha256;
        if (string.IsNullOrEmpty(sha256))
        {
            sha256 = "eml-folder-hash";
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

        // 1. Discover all folders recursively that contain .eml files
        var folderList = new List<EmlFolderEntry>();
        DiscoverEmlFoldersRecursive(new DirectoryInfo(filePath), "", folderList);

        if (folderList.Count == 0)
        {
            // If no EML folders found recursively, assume root directory might have some EMLs directly
            folderList.Add(new EmlFolderEntry(filePath, "Inbox", ""));
        }

        progress?.Report(new IndexingProgress("Indexing", "", 0, 0, 0, 0, 10.0, stopwatch.Elapsed, $"Localizados {folderList.Count} diretórios para indexação.", true));

        // 2. Process folders and their EML files
        foreach (var entry in folderList)
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
                await writer.SaveFolderAsync(folderNode, ct);
                foldersIndexed++;
                await writer.CommitTransactionAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                await writer.RollbackTransactionAsync(CancellationToken.None);
                issuesDetected++;
                await writer.BeginTransactionAsync(ct);
                await writer.SaveIssueAsync(new ExtractionIssue("MV-ERR-EML-FOLDER-SAVE", "Error", $"Falha ao salvar pasta lógica '{entry.LogicalPath}': {ex.Message}", entry.LogicalPath, ex.ToString()), ct);
                await writer.CommitTransactionAsync(CancellationToken.None);
                continue;
            }

            var emlFiles = new DirectoryInfo(entry.Path).GetFiles("*.eml", SearchOption.TopDirectoryOnly);
            int count = 0;
            int batchCount = 0;

            await writer.BeginTransactionAsync(ct);
            foreach (var file in emlFiles)
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

                    var mailItem = MapMimeMessageToMailItem(message, entry.LogicalPath, file.Name);
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
                catch (Exception ex)
                {
                    issuesDetected++;
                    await writer.SaveIssueAsync(new ExtractionIssue("MV-ERR-EML-MSG-PARSE", "Warning", $"Falha ao ler arquivo EML '{file.FullName}': {ex.Message}", entry.LogicalPath, ex.ToString()), ct);
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

    private static void DiscoverEmlFoldersRecursive(DirectoryInfo dir, string parentLogicalPath, List<EmlFolderEntry> folderList)
    {
        var files = dir.GetFiles("*.eml", SearchOption.TopDirectoryOnly);
        if (files.Length > 0)
        {
            string logicalPath = string.IsNullOrEmpty(parentLogicalPath) ? dir.Name : $"{parentLogicalPath}/{dir.Name}";
            folderList.Add(new EmlFolderEntry(dir.FullName, dir.Name, parentLogicalPath));
        }

        foreach (var sub in dir.GetDirectories())
        {
            string logicalPath = string.IsNullOrEmpty(parentLogicalPath) ? dir.Name : $"{parentLogicalPath}/{dir.Name}";
            DiscoverEmlFoldersRecursive(sub, logicalPath, folderList);
        }
    }

    private static MailItem MapMimeMessageToMailItem(MimeMessage message, string folderLogicalPath, string filename)
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

    private class EmlFolderEntry
    {
        public string Path { get; }
        public string DisplayName { get; }
        public string ParentLogicalPath { get; }
        public string LogicalPath => string.IsNullOrEmpty(ParentLogicalPath) ? DisplayName : $"{ParentLogicalPath}/{DisplayName}";

        public EmlFolderEntry(string path, string displayName, string parentLogicalPath)
        {
            Path = path;
            DisplayName = displayName;
            ParentLogicalPath = parentLogicalPath;
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Core.Normalization;
using MailVault.Domain;

namespace MailVault.Indexing;

public sealed class IndexingService : IIndexingService
{
    public async Task<IndexResult> RunIndexAsync(
        string filePath,
        ICaseIndexStore store,
        IMailStoreReader reader,
        string caseId,
        string operatorName,
        bool cachePreview,
        int? limit,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        // 1. Calculate file basic properties
        var fileInfo = new FileInfo(filePath);
        long size = fileInfo.Length;

        // Hash streaming service in Core
        var hashService = new HashService();
        string sha256 = await hashService.CalculateSha256Async(filePath, new NullProgressReporter(), ct);

        // 2. Open and inspect store
        var metadata = await reader.InspectAsync(filePath, ct);

        // 3. Open index store connection (assumed already initialized by the caller)

        int foldersIndexed = 0;
        int messagesIndexed = 0;
        int attachmentsIndexed = 0;
        int issuesDetected = 0;

        using (var writer = store.CreateWriter())
        {
            await writer.BeginTransactionAsync(ct);

            try
            {
                // A. Save Case and Media Info
                await writer.SaveCaseInfoAsync(caseId, filePath, size, sha256, operatorName, DateTimeOffset.Now, ct);

                // Save store-level issues if any
                if (metadata.Issues != null)
                {
                    foreach (var issue in metadata.Issues)
                    {
                        await writer.SaveIssueAsync(issue, ct);
                        issuesDetected++;
                    }
                }

                // B. Enumerate and save folders and messages recursively
                await foreach (var rootFolder in reader.EnumerateFoldersAsync(ct))
                {
                    await IndexFolderRecursiveAsync(rootFolder, writer, reader, folderId => {
                        foldersIndexed++;
                    }, (msg, fId) => {
                        messagesIndexed++;
                        attachmentsIndexed += msg.Attachments.Count;
                        issuesDetected += msg.Issues.Count;
                    }, cachePreview, limit, ct);
                }

                // C. Log index run stats in database
                stopwatch.Stop();
                string runId = Guid.NewGuid().ToString("N");
                await writer.SaveIndexRunAsync(runId, caseId, DateTime.UtcNow, "Success", stopwatch.ElapsedMilliseconds, foldersIndexed, messagesIndexed, attachmentsIndexed, issuesDetected, ct);

                await writer.CommitTransactionAsync(ct);
            }
            catch (Exception)
            {
                await writer.RollbackTransactionAsync(ct);
                throw;
            }
        }

        return new IndexResult(
            CaseId: caseId,
            DbPath: store.DatabasePath,
            FoldersIndexed: foldersIndexed,
            MessagesIndexed: messagesIndexed,
            AttachmentsIndexed: attachmentsIndexed,
            IssuesDetected: issuesDetected,
            DurationMs: stopwatch.ElapsedMilliseconds,
            Sha256: sha256
        );
    }

    private async Task IndexFolderRecursiveAsync(
        FolderNode folder,
        ICaseIndexWriter writer,
        IMailStoreReader reader,
        Action<FolderId> onFolderIndexed,
        Action<MailItem, FolderId> onMessageIndexed,
        bool cachePreview,
        int? limit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Normalize folder path
        string cleanPath = FolderPathNormalizer.Normalize(folder.FullPath);
        var normalizedFolder = new FolderNode(
            Id: folder.Id,
            ParentId: folder.ParentId,
            DisplayName: folder.DisplayName.Trim(),
            FullPath: cleanPath,
            MessageCount: folder.MessageCount,
            Children: folder.Children
        );

        // 2. Save folder to index
        await writer.SaveFolderAsync(normalizedFolder, ct);
        onFolderIndexed(folder.Id);

        // 3. Enumerate folder messages
        int count = 0;
        await foreach (var msg in reader.EnumerateMessagesAsync(folder.Id, ct))
        {
            if (limit.HasValue && count >= limit.Value)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();

            // Apply normalization pipeline
            var normalizedMsg = MailItemNormalizer.Normalize(msg, cachePreview);

            // Save message, attachments and issues
            await writer.SaveMessageAsync(normalizedMsg, folder.Id, ct);
            onMessageIndexed(normalizedMsg, folder.Id);

            count++;
        }

        // 4. Index subfolders
        foreach (var sub in folder.Children)
        {
            await IndexFolderRecursiveAsync(sub, writer, reader, onFolderIndexed, onMessageIndexed, cachePreview, limit, ct);
        }
    }

    private sealed class NullProgressReporter : IProgressReporter
    {
        public void ReportProgress(double percentage, string status)
        {
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        var counters = new IndexingCounters();
        string? errorMessage = null;

        var fileInfo = new FileInfo(filePath);
        long size = fileInfo.Length;

        var hashService = new HashService();
        string sha256 = await hashService.CalculateSha256Async(filePath, new NullProgressReporter(), ct);

        string adapterName = reader.ReaderName;
        string adapterVersion = reader.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0.0";

        using var writer = store.CreateWriter();

        await CommitAsync(writer, async () =>
        {
            await writer.SaveCaseInfoAsync(caseId, filePath, size, sha256, operatorName, DateTimeOffset.Now, adapterName, adapterVersion, ct);
            await writer.SaveIndexRunAsync(Guid.NewGuid().ToString("N"), caseId, DateTime.UtcNow, "Running", 0, 0, 0, 0, 0, ct);
        }, ct);

        try
        {
            if (reader is ISessionAwareMailStoreReader sessionReader)
            {
                await sessionReader.BeginReadSessionAsync(filePath, ct);
            }

            var metadata = await reader.InspectAsync(filePath, ct);
            await SaveIssuesAsync(writer, metadata.Issues, counters, ct);
            await DrainReaderIssuesAsync(writer, reader, counters, ct);

            await foreach (var rootFolder in reader.EnumerateFoldersAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                await IndexFolderRecursiveAsync(rootFolder, writer, reader, counters, cachePreview, limit, ct);
                await DrainReaderIssuesAsync(writer, reader, counters, ct);
            }

            await DrainReaderIssuesAsync(writer, reader, counters, ct);
        }
        catch (OperationCanceledException)
        {
            counters.FatalError = true;
            errorMessage = "Indexação cancelada.";
            await SaveFatalIssueAsync(writer, caseId, "MV-ERR-INDEX-CANCELLED", errorMessage, filePath, null, counters, ct);
        }
        catch (Exception ex)
        {
            counters.FatalError = true;
            errorMessage = ex.Message;
            await SaveFatalIssueAsync(writer, caseId, "MV-ERR-INDEX-FATAL", "Falha fatal controlada durante indexação.", filePath, ex, counters, ct);
        }
        finally
        {
            if (reader is ISessionAwareMailStoreReader sessionReader)
            {
                try
                {
                    await sessionReader.EndReadSessionAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    counters.HadRecoverableErrors = true;
                    errorMessage ??= ex.Message;
                    await SaveFatalIssueAsync(writer, caseId, "MV-ERR-INDEX-SESSION-END", "Falha ao encerrar sessão de leitura.", filePath, ex, counters, CancellationToken.None);
                }
            }
        }

        if (counters.MessagesIndexed == 0)
        {
            string severity = counters.FoldersIndexed == 0 ? "Error" : "Warning";
            await SaveIssuesAsync(writer, new[]
            {
                new ExtractionIssue(
                    Code: "MV-WARN-INDEX-NO-MESSAGES",
                    Severity: severity,
                    Message: "Indexação terminou sem mensagens gravadas no case.db.",
                    ObjectId: caseId,
                    TechnicalDetails: null)
            }, counters, ct);
        }

        stopwatch.Stop();
        string status = DetermineStatus(counters);

        await CommitAsync(writer, async () =>
        {
            await writer.SaveIndexRunAsync(
                Guid.NewGuid().ToString("N"),
                caseId,
                DateTime.UtcNow,
                status,
                stopwatch.ElapsedMilliseconds,
                counters.FoldersIndexed,
                counters.MessagesIndexed,
                counters.AttachmentsIndexed,
                counters.IssuesDetected,
                ct);
        }, ct);

        return new IndexResult(
            CaseId: caseId,
            DbPath: store.DatabasePath,
            FoldersIndexed: counters.FoldersIndexed,
            MessagesIndexed: counters.MessagesIndexed,
            AttachmentsIndexed: counters.AttachmentsIndexed,
            IssuesDetected: counters.IssuesDetected,
            DurationMs: stopwatch.ElapsedMilliseconds,
            Sha256: sha256,
            Status: status,
            ErrorMessage: errorMessage);
    }

    private static async Task IndexFolderRecursiveAsync(
        FolderNode folder,
        ICaseIndexWriter writer,
        IMailStoreReader reader,
        IndexingCounters counters,
        bool cachePreview,
        int? limit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        bool folderCommitted = await TryIndexSingleFolderAsync(folder, writer, reader, counters, cachePreview, limit, ct);
        if (!folderCommitted)
        {
            return;
        }

        foreach (var sub in folder.Children)
        {
            await IndexFolderRecursiveAsync(sub, writer, reader, counters, cachePreview, limit, ct);
        }
    }

    private static async Task<bool> TryIndexSingleFolderAsync(
        FolderNode folder,
        ICaseIndexWriter writer,
        IMailStoreReader reader,
        IndexingCounters counters,
        bool cachePreview,
        int? limit,
        CancellationToken ct)
    {
        string cleanPath = FolderPathNormalizer.Normalize(folder.FullPath);
        var normalizedFolder = new FolderNode(
            Id: folder.Id,
            ParentId: folder.ParentId,
            DisplayName: folder.DisplayName.Trim(),
            FullPath: cleanPath,
            MessageCount: folder.MessageCount,
            Children: folder.Children);

        await writer.BeginTransactionAsync(ct);
        try
        {
            await writer.SaveFolderAsync(normalizedFolder, ct);
            counters.FoldersIndexed++;

            int count = 0;
            await foreach (var msg in reader.EnumerateMessagesAsync(folder.Id, ct))
            {
                if (limit.HasValue && count >= limit.Value)
                {
                    break;
                }

                ct.ThrowIfCancellationRequested();

                var normalizedMsg = MailItemNormalizer.Normalize(msg, cachePreview);
                await writer.SaveMessageAsync(normalizedMsg, folder.Id, ct);
                counters.MessagesIndexed++;
                counters.AttachmentsIndexed += normalizedMsg.Attachments.Count;
                counters.IssuesDetected += normalizedMsg.Issues.Count;
                if (normalizedMsg.Issues.Any(issue => IsError(issue.Severity)))
                {
                    counters.HadRecoverableErrors = true;
                }

                count++;
            }

            if (reader is IExtractionIssueSource issueSource)
            {
                await SaveIssuesInCurrentTransactionAsync(writer, issueSource.DrainIssues(), counters, ct);
            }

            await writer.CommitTransactionAsync(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            await writer.RollbackTransactionAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await writer.RollbackTransactionAsync(CancellationToken.None);
            counters.HadRecoverableErrors = true;
            await SaveIssuesAsync(writer, new[]
            {
                new ExtractionIssue(
                    Code: "MV-ERR-INDEX-FOLDER",
                    Severity: "Error",
                    Message: "Falha ao indexar pasta; pasta ignorada.",
                    ObjectId: folder.Id.Value,
                    TechnicalDetails: $"{ex.GetType().Name}: {ex.Message}")
            }, counters, ct);
            return false;
        }
    }

    private static async Task SaveFatalIssueAsync(
        ICaseIndexWriter writer,
        string caseId,
        string code,
        string message,
        string filePath,
        Exception? ex,
        IndexingCounters counters,
        CancellationToken ct)
    {
        await SaveIssuesAsync(writer, new[]
        {
            new ExtractionIssue(
                Code: code,
                Severity: "Error",
                Message: message,
                ObjectId: caseId,
                TechnicalDetails: ex == null ? Path.GetFileName(filePath) : $"{ex.GetType().Name}: {ex.Message}")
        }, counters, ct);
    }

    private static async Task DrainReaderIssuesAsync(ICaseIndexWriter writer, IMailStoreReader reader, IndexingCounters counters, CancellationToken ct)
    {
        if (reader is IExtractionIssueSource issueSource)
        {
            await SaveIssuesAsync(writer, issueSource.DrainIssues(), counters, ct);
        }
    }

    private static async Task SaveIssuesAsync(ICaseIndexWriter writer, System.Collections.Generic.IReadOnlyList<ExtractionIssue>? issues, IndexingCounters counters, CancellationToken ct)
    {
        if (issues == null || issues.Count == 0)
        {
            return;
        }

        await CommitAsync(writer, async () =>
        {
            await SaveIssuesInCurrentTransactionAsync(writer, issues, counters, ct);
        }, ct);
    }

    private static async Task SaveIssuesInCurrentTransactionAsync(ICaseIndexWriter writer, System.Collections.Generic.IReadOnlyList<ExtractionIssue> issues, IndexingCounters counters, CancellationToken ct)
    {
        foreach (var issue in issues)
        {
            await writer.SaveIssueAsync(issue, ct);
            counters.IssuesDetected++;
            if (IsError(issue.Severity))
            {
                counters.HadRecoverableErrors = true;
            }
        }
    }

    private static async Task CommitAsync(ICaseIndexWriter writer, Func<Task> action, CancellationToken ct)
    {
        await writer.BeginTransactionAsync(ct);
        try
        {
            await action();
            await writer.CommitTransactionAsync(ct);
        }
        catch
        {
            await writer.RollbackTransactionAsync(CancellationToken.None);
            throw;
        }
    }

    private static string DetermineStatus(IndexingCounters counters)
    {
        if (counters.FatalError)
        {
            return counters.FoldersIndexed > 0 || counters.MessagesIndexed > 0 ? "Partial" : "Failed";
        }

        if (counters.MessagesIndexed == 0)
        {
            return counters.FoldersIndexed > 0 ? "Partial" : "Failed";
        }

        return counters.HadRecoverableErrors ? "Partial" : "Success";
    }

    private static bool IsError(string severity)
    {
        return severity.Equals("Error", StringComparison.OrdinalIgnoreCase)
            || severity.Equals("Critical", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class IndexingCounters
    {
        public int FoldersIndexed { get; set; }
        public int MessagesIndexed { get; set; }
        public int AttachmentsIndexed { get; set; }
        public int IssuesDetected { get; set; }
        public bool FatalError { get; set; }
        public bool HadRecoverableErrors { get; set; }
    }

    private sealed class NullProgressReporter : IProgressReporter
    {
        public void ReportProgress(double percentage, string status)
        {
        }
    }
}

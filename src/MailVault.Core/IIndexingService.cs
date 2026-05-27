using System;
using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Core;

public interface IIndexingService
{
    Task<IndexResult> RunIndexAsync(
        string filePath,
        ICaseIndexStore store,
        IMailStoreReader reader,
        string caseId,
        string operatorName,
        bool cachePreview,
        int? limit,
        CancellationToken ct);

    Task<IndexResult> RunIndexAsync(
        string filePath,
        ICaseIndexStore store,
        IMailStoreReader reader,
        string caseId,
        string operatorName,
        bool cachePreview,
        int? limit,
        IProgress<IndexingProgress>? progress,
        CancellationToken ct);
}

public record IndexResult(
    string CaseId,
    string DbPath,
    int FoldersIndexed,
    int MessagesIndexed,
    int AttachmentsIndexed,
    int IssuesDetected,
    long DurationMs,
    string Sha256,
    string Status = "Success",
    string? ErrorMessage = null
);

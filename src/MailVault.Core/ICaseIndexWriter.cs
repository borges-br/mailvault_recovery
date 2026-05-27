using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Domain;

namespace MailVault.Core;

public interface ICaseIndexWriter : System.IDisposable
{
    string? ActiveRunId { get; set; }
    Task BeginTransactionAsync(CancellationToken ct);
    Task CommitTransactionAsync(CancellationToken ct);
    Task RollbackTransactionAsync(CancellationToken ct);
    
    Task SaveCaseInfoAsync(string caseId, string sourceFile, long sourceSize, string sourceSha256, string operatorName, DateTimeOffset startedAt, string adapterName, string adapterVersion, CancellationToken ct);
    Task SaveFolderAsync(FolderNode folder, CancellationToken ct);
    Task SaveMessageAsync(MailItem message, FolderId folderId, CancellationToken ct);
    Task SaveIssueAsync(ExtractionIssue issue, CancellationToken ct);
    Task SaveIndexRunAsync(string runId, string caseId, System.DateTime timestamp, string status, long durationMs, int foldersCount, int messagesCount, int attachmentsCount, int issuesCount, CancellationToken ct);
}

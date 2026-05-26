using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Domain;

namespace MailVault.Core;

public interface ICaseIndexReader : System.IDisposable
{
    Task<int> GetFolderCountAsync(CancellationToken ct);
    Task<int> GetMessageCountAsync(CancellationToken ct);
    Task<int> GetAttachmentCountAsync(CancellationToken ct);
    Task<int> GetIssueCountAsync(CancellationToken ct);
    Task<Dictionary<string, int>> GetTopFoldersByMessageCountAsync(int limit, CancellationToken ct);
    Task<long> GetTotalAttachmentSizeAsync(CancellationToken ct);
    Task<(string fileName, long sizeBytes)> GetLargestAttachmentAsync(CancellationToken ct);
    
    IAsyncEnumerable<MailItem> SearchMessagesAsync(string queryText, string? folderPath, int limit, int offset, CancellationToken ct);
    IAsyncEnumerable<FolderNode> GetFolderHierarchyAsync(CancellationToken ct);
    IAsyncEnumerable<MailItem> GetMessagesInFolderAsync(FolderId folderId, int limit, int offset, CancellationToken ct);
    Task<MailItem?> GetMessageByIdAsync(MessageId messageId, CancellationToken ct);
}

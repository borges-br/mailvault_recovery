using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Domain;

namespace MailVault.Core;

public interface IMailStoreReader
{
    string ReaderName { get; }
    Task<StoreMetadata> InspectAsync(string filePath, CancellationToken ct);
    IAsyncEnumerable<FolderNode> EnumerateFoldersAsync(CancellationToken ct);
    IAsyncEnumerable<MailItem> EnumerateMessagesAsync(FolderId folderId, CancellationToken ct);
    Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct);
    Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct);
    Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct);
}

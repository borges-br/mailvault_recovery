using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Domain;

namespace MailVault.Core;

public interface IAttachmentContentProvider
{
    Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct);
}

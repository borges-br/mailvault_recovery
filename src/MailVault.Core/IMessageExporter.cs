using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Domain;

namespace MailVault.Core;

public interface IMessageExporter
{
    string FormatName { get; } // e.g. "EML" or "MBOX"
    Task ExportMessageAsync(MailItem message, IAttachmentContentProvider attachmentProvider, Stream outputStream, CancellationToken ct);
}

using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Domain;

public interface IAuditTrailWriter
{
    Task WriteEventAsync(AuditEvent auditEvent, CancellationToken ct);
}

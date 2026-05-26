using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Core;

public interface ISessionAwareMailStoreReader
{
    Task BeginReadSessionAsync(string filePath, CancellationToken ct);
    Task EndReadSessionAsync(CancellationToken ct);
}

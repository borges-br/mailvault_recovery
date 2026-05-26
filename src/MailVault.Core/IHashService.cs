using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Core;

public interface IHashService
{
    Task<string> CalculateSha256Async(string filePath, IProgressReporter? progress, CancellationToken ct);
}

using System.Threading;
using System.Threading.Tasks;
using MailVault.Domain;

namespace MailVault.Core;

public interface IRecoveryJobRunner
{
    Task<RecoveryManifest> RunJobAsync(RecoveryJob job, IMailStoreReader reader, IProgressReporter progress, CancellationToken ct);
}

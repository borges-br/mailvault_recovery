using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Core;

public interface IExportJobRunner
{
    Task<ExportJobResult> RunExportJobAsync(
        ExportJobOptions options,
        ICaseIndexReader caseReader,
        IAdapterResolver adapterResolver,
        IMessageExporter exporter,
        IProgressReporter progress,
        CancellationToken ct);
}

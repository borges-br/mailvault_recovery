using System;
using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Core;

public interface ICaseIndexStore : IDisposable
{
    string ConnectionString { get; }
    string DatabasePath { get; }
    Task InitializeAsync(string caseFolderPath, CancellationToken ct);
    ICaseIndexWriter CreateWriter();
    ICaseIndexReader CreateReader();
}

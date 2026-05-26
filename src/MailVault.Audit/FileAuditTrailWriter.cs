using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Domain;

namespace MailVault.Audit;

public sealed class FileAuditTrailWriter : IAuditTrailWriter
{
    private readonly string _logFilePath;

    public FileAuditTrailWriter(string logFilePath)
    {
        _logFilePath = logFilePath;
    }

    public async Task WriteEventAsync(AuditEvent auditEvent, CancellationToken ct)
    {
        string logLine = JsonSerializer.Serialize(auditEvent) + Environment.NewLine;
        
        string? dir = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.AppendAllTextAsync(_logFilePath, logLine, ct).ConfigureAwait(false);
    }
}

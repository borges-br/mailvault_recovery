using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Core;

public sealed class HashService : IHashService
{
    private readonly int _bufferSize;
    private readonly TimeProvider _timeProvider;

    public HashService() : this(4 * 1024 * 1024, TimeProvider.System)
    {
    }

    public HashService(int bufferSize, TimeProvider? timeProvider = null)
    {
        _bufferSize = bufferSize > 0 ? bufferSize : 4 * 1024 * 1024;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> CalculateSha256Async(string filePath, IProgressReporter? progress, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Arquivo não encontrado para cálculo de hash.", filePath);
        }

        // Open file with SequentialScan option for efficient streaming reads
        using var stream = new FileStream(
            filePath, 
            FileMode.Open, 
            FileAccess.Read, 
            FileShare.Read, 
            bufferSize: 4096, 
            options: FileOptions.SequentialScan | FileOptions.Asynchronous);

        using var sha256 = SHA256.Create();

        long totalBytes = stream.Length;
        long totalBytesRead = 0;
        byte[] buffer = new byte[_bufferSize];
        int bytesRead;

        long startTimeStamp = _timeProvider.GetTimestamp();
        long lastReportTimeStamp = startTimeStamp;
        double lastReportedPct = -1.0;
        int progressEventsEmitted = 0;

        // Structured progress reporting callback according to user refinement 1:
        // "Hash started: fileSize=..., bufferSize=..., progressIntervalMs=..."
        string startMessage = $"Hash started: fileSize={totalBytes}, bufferSize={_bufferSize}, progressIntervalMs=250";
        progress?.ReportProgress(0.0, startMessage);
        progressEventsEmitted++;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            totalBytesRead += bytesRead;

            if (totalBytes > 0)
            {
                double pct = (double)totalBytesRead / totalBytes * 100.0;
                long now = _timeProvider.GetTimestamp();
                double elapsedSec = _timeProvider.GetElapsedTime(startTimeStamp, now).TotalSeconds;

                double? mbps = null;
                TimeSpan? eta = null;

                if (elapsedSec > 0.05) // Avoid initial spike/division by zero
                {
                    double bytesPerSec = totalBytesRead / elapsedSec;
                    mbps = bytesPerSec / (1024 * 1024);
                    
                    if (bytesPerSec > 0)
                    {
                        long remainingBytes = totalBytes - totalBytesRead;
                        eta = TimeSpan.FromSeconds(remainingBytes / bytesPerSec);
                    }
                }

                // Format progress details
                string statusMsg = $"Calculando hash SHA-256: {pct:F1}% concluído.";
                if (mbps.HasValue)
                {
                    statusMsg += $" Speed: {mbps.Value:F1} MB/s.";
                }
                if (eta.HasValue)
                {
                    statusMsg += $" ETA: {eta.Value:hh\\:mm\\:ss}.";
                }

                // Check if progress should be reported (Throttle criteria from user refinement 5)
                // Criteria: Elapsed interval >= 250ms OR percent delta >= 0.5% OR phase/msg changed
                double elapsedMsSinceLastReport = _timeProvider.GetElapsedTime(lastReportTimeStamp, now).TotalMilliseconds;
                double pctDelta = Math.Abs(pct - lastReportedPct);

                if (pctDelta >= 0.5 || elapsedMsSinceLastReport >= 250)
                {
                    progress?.ReportProgress(pct, statusMsg);
                    lastReportedPct = pct;
                    lastReportTimeStamp = now;
                    progressEventsEmitted++;
                }
            }
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        byte[]? hashBytes = sha256.Hash;
        if (hashBytes == null)
        {
            throw new InvalidOperationException("Falha ao calcular hash SHA-256.");
        }

        string hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        
        long endTimestamp = _timeProvider.GetTimestamp();
        TimeSpan elapsedTotal = _timeProvider.GetElapsedTime(startTimeStamp, endTimestamp);
        double avgMBps = elapsedTotal.TotalSeconds > 0 ? (totalBytes / elapsedTotal.TotalSeconds) / (1024 * 1024) : 0.0;

        // Structured progress reporting callback according to user refinement 1:
        // "Hash completed: elapsed=..., avgMBps=..., progressEventsEmitted=..."
        string completionMessage = $"Hash completed: elapsed={elapsedTotal.TotalMilliseconds:F0}ms, avgMBps={avgMBps:F1}, progressEventsEmitted={progressEventsEmitted + 1}";
        
        progress?.ReportProgress(100.0, completionMessage);
        progress?.ReportProgress(100.0, $"Cálculo de hash concluído com sucesso: {hashHex}");

        return hashHex;
    }
}

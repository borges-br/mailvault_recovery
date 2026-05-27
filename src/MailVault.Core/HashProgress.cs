using System;

namespace MailVault.Core;

public sealed class HashProgress
{
    public long BytesProcessed { get; }
    public long TotalBytes { get; }
    public double Percent { get; }
    public double? MegabytesPerSecond { get; }
    public TimeSpan? EstimatedRemaining { get; }
    public string Phase { get; }
    public string Message { get; }

    public HashProgress(
        long bytesProcessed,
        long totalBytes,
        double percent,
        double? megabytesPerSecond,
        TimeSpan? estimatedRemaining,
        string phase,
        string message)
    {
        BytesProcessed = bytesProcessed;
        TotalBytes = totalBytes;
        Percent = percent;
        MegabytesPerSecond = megabytesPerSecond;
        EstimatedRemaining = estimatedRemaining;
        Phase = phase;
        Message = message;
    }
}

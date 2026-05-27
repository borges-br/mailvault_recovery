using System;

namespace MailVault.Core;

public sealed class IndexingProgress
{
    public string Phase { get; }
    public string CurrentFolderPath { get; }
    public int FoldersProcessed { get; }
    public int MessagesProcessed { get; }
    public int AttachmentsProcessed { get; }
    public int IssuesCount { get; }
    public double? Percent { get; }
    public TimeSpan Elapsed { get; }
    public string Message { get; }
    public bool IsCancellable { get; }

    public IndexingProgress(
        string phase,
        string currentFolderPath,
        int foldersProcessed,
        int messagesProcessed,
        int attachmentsProcessed,
        int issuesCount,
        double? percent,
        TimeSpan elapsed,
        string message,
        bool isCancellable)
    {
        Phase = phase;
        CurrentFolderPath = currentFolderPath;
        FoldersProcessed = foldersProcessed;
        MessagesProcessed = messagesProcessed;
        AttachmentsProcessed = attachmentsProcessed;
        IssuesCount = issuesCount;
        Percent = percent;
        Elapsed = elapsed;
        Message = message;
        IsCancellable = isCancellable;
    }
}

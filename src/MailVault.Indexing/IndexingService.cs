using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Core.Normalization;
using MailVault.Domain;

namespace MailVault.Indexing;

public sealed class IndexingService : IIndexingService
{
    public string? PrecalculatedSha256 { get; set; }

    public Task<IndexResult> RunIndexAsync(
        string filePath,
        ICaseIndexStore store,
        IMailStoreReader reader,
        string caseId,
        string operatorName,
        bool cachePreview,
        int? limit,
        CancellationToken ct)
    {
        return RunIndexAsync(filePath, store, reader, caseId, operatorName, cachePreview, limit, null, ct);
    }

    public async Task<IndexResult> RunIndexAsync(
        string filePath,
        ICaseIndexStore store,
        IMailStoreReader reader,
        string caseId,
        string operatorName,
        bool cachePreview,
        int? limit,
        IProgress<IndexingProgress>? progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var counters = new IndexingCounters();
        string? errorMessage = null;

        var fileInfo = new FileInfo(filePath);
        long size = fileInfo.Length;

        ct.ThrowIfCancellationRequested();
        progress?.Report(new IndexingProgress(
            phase: "Preparing",
            currentFolderPath: "",
            foldersProcessed: 0,
            messagesProcessed: 0,
            attachmentsProcessed: 0,
            issuesCount: 0,
            percent: 0.0,
            elapsed: stopwatch.Elapsed,
            message: "Iniciando pipeline de indexação...",
            isCancellable: true
        ));

        // Step 1: Calculate SHA-256 if not precalculated
        string sha256 = PrecalculatedSha256;
        if (string.IsNullOrEmpty(sha256))
        {
            progress?.Report(new IndexingProgress(
                phase: "Preparing",
                currentFolderPath: "",
                foldersProcessed: 0,
                messagesProcessed: 0,
                attachmentsProcessed: 0,
                issuesCount: 0,
                percent: 5.0,
                elapsed: stopwatch.Elapsed,
                message: "Calculando hash (SHA-256) da evidência...",
                isCancellable: true
            ));
            var hashService = new HashService();
            sha256 = await hashService.CalculateSha256Async(filePath, new NullProgressReporter(), ct);
        }

        string adapterName = reader.ReaderName;
        string adapterVersion = reader.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0.0";

        using var writer = store.CreateWriter();

        ct.ThrowIfCancellationRequested();
        progress?.Report(new IndexingProgress(
            phase: "Preparing",
            currentFolderPath: "",
            foldersProcessed: 0,
            messagesProcessed: 0,
            attachmentsProcessed: 0,
            issuesCount: 0,
            percent: 10.0,
            elapsed: stopwatch.Elapsed,
            message: "Registrando metadados do caso...",
            isCancellable: true
        ));

        await CommitAsync(writer, async () =>
        {
            await writer.SaveCaseInfoAsync(caseId, filePath, size, sha256, operatorName, DateTimeOffset.Now, adapterName, adapterVersion, ct);
            await writer.SaveIndexRunAsync(Guid.NewGuid().ToString("N"), caseId, DateTime.UtcNow, "Running", 0, 0, 0, 0, 0, ct);
        }, ct);

        try
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new IndexingProgress(
                phase: "Preparing",
                currentFolderPath: "",
                foldersProcessed: 0,
                messagesProcessed: 0,
                attachmentsProcessed: 0,
                issuesCount: 0,
                percent: 15.0,
                elapsed: stopwatch.Elapsed,
                message: "Abrindo arquivo OST/PST...",
                isCancellable: true
            ));

            if (reader is ISessionAwareMailStoreReader sessionReader)
            {
                await sessionReader.BeginReadSessionAsync(filePath, ct);
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new IndexingProgress(
                phase: "Preparing",
                currentFolderPath: "",
                foldersProcessed: 0,
                messagesProcessed: 0,
                attachmentsProcessed: 0,
                issuesCount: 0,
                percent: 20.0,
                elapsed: stopwatch.Elapsed,
                message: "Enumerando pastas...",
                isCancellable: true
            ));

            var metadata = await reader.InspectAsync(filePath, ct);
            await SaveIssuesAsync(writer, metadata.Issues, counters, ct);
            await DrainReaderIssuesAsync(writer, reader, counters, ct);

            await foreach (var rootFolder in reader.EnumerateFoldersAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                await IndexFolderRecursiveAsync(rootFolder, writer, reader, counters, cachePreview, limit, progress, stopwatch, ct);
                await DrainReaderIssuesAsync(writer, reader, counters, ct);
            }

            await DrainReaderIssuesAsync(writer, reader, counters, ct);
        }
        catch (OperationCanceledException)
        {
            counters.FatalError = true;
            counters.IsCancelled = true;
            errorMessage = "Operação cancelada pelo operador.";
            
            // Try to write cancellation issue safely to DB
            try
            {
                await SaveFatalIssueAsync(writer, caseId, "MV-ERR-INDEX-CANCELLED", errorMessage, filePath, null, counters, CancellationToken.None);
            }
            catch
            {
                // Best-effort database write on cancellation
            }
        }
        catch (Exception ex)
        {
            counters.FatalError = true;
            errorMessage = ex.Message;
            try
            {
                await SaveFatalIssueAsync(writer, caseId, "MV-ERR-INDEX-FATAL", "Falha fatal controlada durante indexação.", filePath, ex, counters, CancellationToken.None);
            }
            catch
            {
                // Best-effort database write on fatal crash
            }
        }
        finally
        {
            if (reader is ISessionAwareMailStoreReader sessionReader)
            {
                try
                {
                    await sessionReader.EndReadSessionAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    counters.HadRecoverableErrors = true;
                    errorMessage ??= ex.Message;
                    try
                    {
                        await SaveFatalIssueAsync(writer, caseId, "MV-ERR-INDEX-SESSION-END", "Falha ao encerrar sessão de leitura.", filePath, ex, counters, CancellationToken.None);
                    }
                    catch
                    {
                        // Best-effort database write
                    }
                }
            }
        }

        if (counters.MessagesIndexed == 0 && !counters.IsCancelled)
        {
            string severity = counters.FoldersIndexed == 0 ? "Error" : "Warning";
            try
            {
                await SaveIssuesAsync(writer, new[]
                {
                    new ExtractionIssue(
                        Code: "MV-WARN-INDEX-NO-MESSAGES",
                        Severity: severity,
                        Message: "Indexação terminou sem mensagens gravadas no case.db.",
                        ObjectId: caseId,
                        TechnicalDetails: null)
                }, counters, CancellationToken.None);
            }
            catch
            {
                // Best-effort database write
            }
        }

        stopwatch.Stop();
        string status = DetermineStatus(counters);

        // Finalize index_run in DB
        try
        {
            await CommitAsync(writer, async () =>
            {
                await writer.SaveIndexRunAsync(
                    Guid.NewGuid().ToString("N"),
                    caseId,
                    DateTime.UtcNow,
                    status,
                    stopwatch.ElapsedMilliseconds,
                    counters.FoldersIndexed,
                    counters.MessagesIndexed,
                    counters.AttachmentsIndexed,
                    counters.IssuesDetected,
                    CancellationToken.None);
            }, CancellationToken.None);
        }
        catch
        {
            // Best-effort database write
        }

        // Send final progress report
        progress?.Report(new IndexingProgress(
            phase: status == "Cancelled" ? "Cancelled" : "Completed",
            currentFolderPath: "",
            foldersProcessed: counters.FoldersIndexed,
            messagesProcessed: counters.MessagesIndexed,
            attachmentsProcessed: counters.AttachmentsIndexed,
            issuesCount: counters.IssuesDetected,
            percent: 100.0,
            elapsed: stopwatch.Elapsed,
            message: status == "Cancelled" ? "Cancelamento solicitado, finalizando estado seguro..." : "Indexação concluída!",
            isCancellable: false
        ));

        if (counters.IsCancelled)
        {
            throw new OperationCanceledException("Indexação cancelada.");
        }

        return new IndexResult(
            CaseId: caseId,
            DbPath: store.DatabasePath,
            FoldersIndexed: counters.FoldersIndexed,
            MessagesIndexed: counters.MessagesIndexed,
            AttachmentsIndexed: counters.AttachmentsIndexed,
            IssuesDetected: counters.IssuesDetected,
            DurationMs: stopwatch.ElapsedMilliseconds,
            Sha256: sha256,
            Status: status,
            ErrorMessage: errorMessage);
    }

    private static async Task IndexFolderRecursiveAsync(
        FolderNode folder,
        ICaseIndexWriter writer,
        IMailStoreReader reader,
        IndexingCounters counters,
        bool cachePreview,
        int? limit,
        IProgress<IndexingProgress>? progress,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        bool folderCommitted = await TryIndexSingleFolderAsync(folder, writer, reader, counters, cachePreview, limit, progress, stopwatch, ct);
        if (!folderCommitted)
        {
            return;
        }

        foreach (var sub in folder.Children)
        {
            ct.ThrowIfCancellationRequested();
            await IndexFolderRecursiveAsync(sub, writer, reader, counters, cachePreview, limit, progress, stopwatch, ct);
        }
    }

    private static async Task<bool> TryIndexSingleFolderAsync(
        FolderNode folder,
        ICaseIndexWriter writer,
        IMailStoreReader reader,
        IndexingCounters counters,
        bool cachePreview,
        int? limit,
        IProgress<IndexingProgress>? progress,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        string cleanPath = FolderPathNormalizer.Normalize(folder.FullPath);
        var normalizedFolder = new FolderNode(
            Id: folder.Id,
            ParentId: folder.ParentId,
            DisplayName: folder.DisplayName.Trim(),
            FullPath: cleanPath,
            MessageCount: folder.MessageCount,
            Children: folder.Children);

        // Report beginning of folder enumeration
        progress?.Report(new IndexingProgress(
            phase: "Indexing",
            currentFolderPath: cleanPath,
            foldersProcessed: counters.FoldersIndexed,
            messagesProcessed: counters.MessagesIndexed,
            attachmentsProcessed: counters.AttachmentsIndexed,
            issuesCount: counters.IssuesDetected,
            percent: null,
            elapsed: stopwatch.Elapsed,
            message: $"Enumerando pasta: {normalizedFolder.DisplayName}",
            isCancellable: true
        ));

        // Save folder metadata first
        await CommitAsync(writer, async () =>
        {
            await writer.SaveFolderAsync(normalizedFolder, ct);
            counters.FoldersIndexed++;
        }, ct);

        try
        {
            int count = 0;
            int batchCount = 0;
            const int BatchSize = 500;

            await writer.BeginTransactionAsync(ct);

            var itemWatch = System.Diagnostics.Stopwatch.StartNew();
            await foreach (var msg in reader.EnumerateMessagesAsync(folder.Id, ct))
            {
                if (limit.HasValue && count >= limit.Value)
                {
                    break;
                }

                ct.ThrowIfCancellationRequested();
                itemWatch.Restart();

                try
                {
                    var normalizedMsg = MailItemNormalizer.Normalize(msg, cachePreview);
                    await writer.SaveMessageAsync(normalizedMsg, folder.Id, ct);
                    counters.MessagesIndexed++;
                    counters.AttachmentsIndexed += normalizedMsg.Attachments.Count;
                    counters.IssuesDetected += normalizedMsg.Issues.Count;
                    if (normalizedMsg.Issues.Any(issue => IsError(issue.Severity)))
                    {
                        counters.HadRecoverableErrors = true;
                    }

                    if (itemWatch.Elapsed.TotalSeconds > 30)
                    {
                        counters.HadRecoverableErrors = true;
                        await writer.SaveIssueAsync(new ExtractionIssue(
                            Code: "MV-WARN-INDEX-SLOW",
                            Severity: "Warning",
                            Message: $"Mensagem demorou {itemWatch.Elapsed.TotalSeconds:F1}s para ser indexada (>30s).",
                            ObjectId: normalizedMsg.InternalId,
                            TechnicalDetails: null
                        ), ct);
                    }
                }
                catch (Exception ex)
                {
                    counters.HadRecoverableErrors = true;
                    counters.IssuesDetected++;
                    string msgId = msg?.InternalId ?? $"{folder.Id.Value}/error-{Guid.NewGuid():N}";
                    await writer.SaveIssueAsync(new ExtractionIssue(
                        Code: "MV-ERR-INDEX-MSG-FAILED",
                        Severity: "Error",
                        Message: $"Falha ao ler mensagem: {ex.Message}",
                        ObjectId: msgId,
                        TechnicalDetails: ex.ToString()
                    ), ct);
                }

                count++;
                batchCount++;

                // Commit batch periodically to prevent database write locks and allow UI polling
                if (batchCount >= BatchSize)
                {
                    await writer.CommitTransactionAsync(CancellationToken.None);
                    batchCount = 0;

                    // Report batch progress metrics
                    progress?.Report(new IndexingProgress(
                        phase: "Indexing",
                        currentFolderPath: cleanPath,
                        foldersProcessed: counters.FoldersIndexed,
                        messagesProcessed: counters.MessagesIndexed,
                        attachmentsProcessed: counters.AttachmentsIndexed,
                        issuesCount: counters.IssuesDetected,
                        percent: null,
                        elapsed: stopwatch.Elapsed,
                        message: $"Indexando pasta: {normalizedFolder.DisplayName} ({counters.MessagesIndexed} emails)",
                        isCancellable: true
                    ));

                    ct.ThrowIfCancellationRequested();
                    await writer.BeginTransactionAsync(ct);
                }
            }

            // Commit final remaining messages in current transaction
            if (batchCount > 0)
            {
                await writer.CommitTransactionAsync(CancellationToken.None);
            }
            else
            {
                await writer.RollbackTransactionAsync(CancellationToken.None);
            }

            if (reader is IExtractionIssueSource issueSource)
            {
                await CommitAsync(writer, async () =>
                {
                    await SaveIssuesInCurrentTransactionAsync(writer, issueSource.DrainIssues(), counters, ct);
                }, ct);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            await writer.RollbackTransactionAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await writer.RollbackTransactionAsync(CancellationToken.None);
            counters.HadRecoverableErrors = true;
            await SaveIssuesAsync(writer, new[]
            {
                new ExtractionIssue(
                    Code: "MV-ERR-INDEX-FOLDER",
                    Severity: "Error",
                    Message: "Falha ao indexar pasta; pasta ignorada.",
                    ObjectId: folder.Id.Value,
                    TechnicalDetails: $"{ex.GetType().Name}: {ex.Message}")
            }, counters, CancellationToken.None);
            return false;
        }
    }

    private static async Task SaveFatalIssueAsync(
        ICaseIndexWriter writer,
        string caseId,
        string code,
        string message,
        string filePath,
        Exception? ex,
        IndexingCounters counters,
        CancellationToken ct)
    {
        await SaveIssuesAsync(writer, new[]
        {
            new ExtractionIssue(
                Code: code,
                Severity: "Error",
                Message: message,
                ObjectId: caseId,
                TechnicalDetails: ex == null ? Path.GetFileName(filePath) : $"{ex.GetType().Name}: {ex.Message}")
        }, counters, ct);
    }

    private static async Task DrainReaderIssuesAsync(ICaseIndexWriter writer, IMailStoreReader reader, IndexingCounters counters, CancellationToken ct)
    {
        if (reader is IExtractionIssueSource issueSource)
        {
            await SaveIssuesAsync(writer, issueSource.DrainIssues(), counters, ct);
        }
    }

    private static async Task SaveIssuesAsync(ICaseIndexWriter writer, System.Collections.Generic.IReadOnlyList<ExtractionIssue>? issues, IndexingCounters counters, CancellationToken ct)
    {
        if (issues == null || issues.Count == 0)
        {
            return;
        }

        await CommitAsync(writer, async () =>
        {
            await SaveIssuesInCurrentTransactionAsync(writer, issues, counters, ct);
        }, ct);
    }

    private static async Task SaveIssuesInCurrentTransactionAsync(ICaseIndexWriter writer, System.Collections.Generic.IReadOnlyList<ExtractionIssue> issues, IndexingCounters counters, CancellationToken ct)
    {
        foreach (var issue in issues)
        {
            await writer.SaveIssueAsync(issue, ct);
            counters.IssuesDetected++;
            if (IsError(issue.Severity))
            {
                counters.HadRecoverableErrors = true;
            }
        }
    }

    private static async Task CommitAsync(ICaseIndexWriter writer, Func<Task> action, CancellationToken ct)
    {
        await writer.BeginTransactionAsync(ct);
        try
        {
            await action();
            await writer.CommitTransactionAsync(CancellationToken.None);
        }
        catch
        {
            await writer.RollbackTransactionAsync(CancellationToken.None);
            throw;
        }
    }

    private static string DetermineStatus(IndexingCounters counters)
    {
        if (counters.IsCancelled)
        {
            return "Cancelled";
        }

        if (counters.FatalError)
        {
            return counters.FoldersIndexed > 0 || counters.MessagesIndexed > 0 ? "Partial" : "Failed";
        }

        if (counters.MessagesIndexed == 0)
        {
            return counters.FoldersIndexed > 0 ? "Partial" : "Failed";
        }

        return counters.HadRecoverableErrors ? "Partial" : "Success";
    }

    private static bool IsError(string severity)
    {
        return severity.Equals("Error", StringComparison.OrdinalIgnoreCase)
            || severity.Equals("Critical", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class IndexingCounters
    {
        public int FoldersIndexed { get; set; }
        public int MessagesIndexed { get; set; }
        public int AttachmentsIndexed { get; set; }
        public int IssuesDetected { get; set; }
        public bool FatalError { get; set; }
        public bool HadRecoverableErrors { get; set; }
        public bool IsCancelled { get; set; }
    }

    private sealed class NullProgressReporter : IProgressReporter
    {
        public void ReportProgress(double percentage, string status)
        {
        }
    }
}

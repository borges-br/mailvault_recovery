using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Indexing;
using Microsoft.Data.Sqlite;

namespace MailVault.Desktop.Services;

public record WorkerJobConfig(
    string EvidencePath,
    string CasePath,
    string CaseId,
    string OperatorId,
    string EvidenceSha256,
    long EvidenceSize,
    string SelectedReaderEngine,
    bool NoBodyLogging = true
)
{
    public string? JobKind { get; init; } = "IndexMetadata";
    public bool IndexingModeMetadataOnly { get; init; } = true;
    public string? OutputPath { get; init; }
    public string? ExportFormat { get; init; }
    public List<string>? SelectedMessageIds { get; init; }
    public bool IncludeAttachments { get; init; } = true;
    public bool ExtractAttachments { get; init; } = true;
    public string? MessageId { get; init; }
    public string? AttachmentId { get; init; }
    public string? FallbackToolName { get; init; }
}

public record WorkerProgressEvent(
    string Type,
    string TimestampUtc,
    string Engine,
    string Phase,
    string CurrentFolderPath,
    int FoldersProcessed,
    int MessagesProcessed,
    int AttachmentsProcessed,
    int IssuesCount,
    bool Heartbeat,
    string Message
);

public record WorkerOrchestrationResult(
    string Status,
    string? ErrorMessage,
    int FoldersIndexed,
    int MessagesIndexed,
    int AttachmentsIndexed,
    int IssuesDetected
);

public sealed class WorkerProcessOrchestrator
{
    private readonly IWorkerExecutableResolver _resolver;
    private Process? _activeProcess;
    private WorkerProgressEvent? _lastKnownEvent;
    private readonly object _eventLock = new();

    public string? CliPathOverride { get; set; }
    public string? CliArgumentsOverride { get; set; }

    public WorkerProcessOrchestrator() : this(new WorkerExecutableResolver())
    {
    }

    public WorkerProcessOrchestrator(IWorkerExecutableResolver resolver)
    {
        _resolver = resolver;
    }

    public WorkerProgressEvent? LastKnownEvent
    {
        get
        {
            lock (_eventLock) return _lastKnownEvent;
        }
        private set
        {
            lock (_eventLock) _lastKnownEvent = value;
        }
    }

    public static string FindCliExecutablePath()
    {
        string baseDir = AppContext.BaseDirectory;
        string exeExtension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        
        // 1. Look in the same folder as base directory
        string sameFolder = Path.Combine(baseDir, $"MailVault.Cli{exeExtension}");
        if (File.Exists(sameFolder)) return sameFolder;

        // 2. Look in relative dev directories (net10.0 sibling)
        string siblingDev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "MailVault.Cli", "bin", "Debug", "net10.0", $"MailVault.Cli{exeExtension}"));
        if (File.Exists(siblingDev)) return siblingDev;

        string siblingDevRelease = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "MailVault.Cli", "bin", "Release", "net10.0", $"MailVault.Cli{exeExtension}"));
        if (File.Exists(siblingDevRelease)) return siblingDevRelease;

        // 3. Fallback to standard command name
        return $"MailVault.Cli{exeExtension}";
    }

    public async Task<WorkerOrchestrationResult> RunJobAsync(
        WorkerJobConfig job,
        Action<WorkerProgressEvent> onProgress,
        CancellationToken ct)
    {
        LastKnownEvent = null;

        WorkerLaunchInfo launchInfo;
        try
        {
            if (!string.IsNullOrEmpty(CliPathOverride))
            {
                launchInfo = new WorkerLaunchInfo(
                    FileName: CliPathOverride,
                    ArgumentsPrefix: CliArgumentsOverride,
                    WorkingDirectory: Path.GetDirectoryName(Path.GetFullPath(CliPathOverride)) ?? AppContext.BaseDirectory,
                    LaunchMode: WorkerLaunchMode.Exe,
                    DiagnosticDescription: "Variável/Caminho forçado via testes/configurações legadas",
                    ProbedPaths: new[] { CliPathOverride },
                    IsPublishedLayout: false,
                    IsDevelopmentLayout: false
                );
            }
            else
            {
                launchInfo = _resolver.Resolve();
            }
        }
        catch (Exception ex)
        {
            return new WorkerOrchestrationResult("Failed", $"Falha ao resolver executável do worker CLI: {ex.Message}", 0, 0, 0, 0);
        }

        // Create temporary job file inside case folder for safety - ensure absolute paths per requirement 2
        string absCasePath = Path.GetFullPath(job.CasePath);
        string tempJobFile = Path.Combine(absCasePath, $"job-{Guid.NewGuid():N}.json");
        if (!Directory.Exists(absCasePath))
        {
            Directory.CreateDirectory(absCasePath);
        }

        var absoluteJob = job with { 
            EvidencePath = Path.GetFullPath(job.EvidencePath),
            CasePath = absCasePath
        };

        string jobJson = JsonSerializer.Serialize(absoluteJob, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tempJobFile, jobJson, ct);

        string jobArgs = $"worker --job \"{tempJobFile}\"";
        string finalArguments = string.IsNullOrEmpty(launchInfo.ArgumentsPrefix) 
            ? jobArgs 
            : $"{launchInfo.ArgumentsPrefix} {jobArgs}";

        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = launchInfo.FileName,
            Arguments = finalArguments,
            WorkingDirectory = launchInfo.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _activeProcess = proc;

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            SafeDeleteFile(tempJobFile);
            return new WorkerOrchestrationResult("Failed", $"Falha ao iniciar subprocesso worker: {ex.Message}", 0, 0, 0, 0);
        }

        // Consume stderr to prevent deadlock buffer leaks
        var readStderrTask = Task.Run(async () =>
        {
            try
            {
                while (!proc.StandardError.EndOfStream)
                {
                    string? errLine = await proc.StandardError.ReadLineAsync(ct);
                    if (errLine != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Worker Stderr] {errLine}");
                    }
                }
            }
            catch { }
        }, ct);

        string? completedStatus = null;
        string? errorMessage = null;
        int folders = 0;
        int messages = 0;
        int attachments = 0;
        int issues = 0;

        try
        {
            while (!proc.StandardOutput.EndOfStream)
            {
                string? line = await proc.StandardOutput.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var progress = JsonSerializer.Deserialize<WorkerProgressEvent>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (progress != null)
                    {
                        if (progress.Type == "progress" || progress.Type == "heartbeat")
                        {
                            LastKnownEvent = progress;
                            folders = progress.FoldersProcessed;
                            messages = progress.MessagesProcessed;
                            attachments = progress.AttachmentsProcessed;
                            issues = progress.IssuesCount;

                            onProgress(progress);
                        }
                        else if (progress.Type == "completed")
                        {
                            completedStatus = progress.Message; 
                            // Note: Completed event schema uses standard completed structure
                            var compJson = JsonDocument.Parse(line);
                            if (compJson.RootElement.TryGetProperty("status", out var stProp)) completedStatus = stProp.GetString();
                            if (compJson.RootElement.TryGetProperty("folders", out var fProp)) folders = fProp.GetInt32();
                            if (compJson.RootElement.TryGetProperty("messages", out var mProp)) messages = mProp.GetInt32();
                            if (compJson.RootElement.TryGetProperty("attachments", out var aProp)) attachments = aProp.GetInt32();
                            if (compJson.RootElement.TryGetProperty("issues", out var iProp)) issues = iProp.GetInt32();
                        }
                        else if (progress.Type == "failed")
                        {
                            completedStatus = "Failed";
                            var failJson = JsonDocument.Parse(line);
                            if (failJson.RootElement.TryGetProperty("message", out var msgProp)) errorMessage = msgProp.GetString();
                        }
                        else if (progress.Type == "cancelled")
                        {
                            completedStatus = "Cancelled";
                        }
                    }
                }
                catch (JsonException)
                {
                    // Handle contaminated/non-JSON stdout gracefully per adjustment 7
                    var protocolIssue = new ExtractionIssue(
                        Code: ExtractionIssue.Codes.ProtocolError,
                        Severity: "Warning",
                        Message: $"Worker stdout contaminação não estruturada: {line.Trim()}",
                        ObjectId: "CLI-Worker-Stdout",
                        TechnicalDetails: $"Stdout bruto contaminado recebido do processo do leitor: {line}"
                    );
                    
                    // Log it as an issue event without crashing parent
                    var protocolEvent = new WorkerProgressEvent(
                        Type: "issue",
                        TimestampUtc: DateTime.UtcNow.ToString("o"),
                        Engine: job.SelectedReaderEngine,
                        Phase: LastKnownEvent?.Phase ?? "Indexing",
                        CurrentFolderPath: LastKnownEvent?.CurrentFolderPath ?? "",
                        FoldersProcessed: folders,
                        MessagesProcessed: messages,
                        AttachmentsProcessed: attachments,
                        IssuesCount: issues + 1,
                        Heartbeat: false,
                        Message: protocolIssue.Message
                    );

                    issues++;
                    onProgress(protocolEvent);
                }
            }

            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // cancellation requested gracefully
            completedStatus = "Cancelled";
        }
        finally
        {
            SafeDeleteFile(tempJobFile);
            _activeProcess = null;
        }

        if (completedStatus == null)
        {
            completedStatus = proc.ExitCode == 0 ? "Success" : "Failed";
            if (proc.ExitCode != 0) errorMessage = $"Processo do leitor encerrou inesperadamente com exit code: {proc.ExitCode}.";
        }

        return new WorkerOrchestrationResult(completedStatus, errorMessage, folders, messages, attachments, issues);
    }

    public async Task ForceKillWorkerAsync()
    {
        var proc = _activeProcess;
        if (proc != null && !proc.HasExited)
        {
            try
            {
                proc.Kill(true); // Kill process and the entire child process tree
                await proc.WaitForExitAsync();
            }
            catch
            {
                // Safe process kill bypass
            }
        }
        _activeProcess = null;
    }

    public async Task IdempotentRepairCaseDbAsync(
        string casePath,
        string caseId,
        string status,
        string? errorMessage)
    {
        try
        {
            string dbPath = Path.Combine(casePath, "case.db");
            if (!File.Exists(dbPath)) return;

            // Securely open in ReadWrite mode with connection pooling disabled
            string connStr = $"Data Source={dbPath};Pooling=False;";
            using var conn = new SqliteConnection(connStr);
            await conn.OpenAsync();

            // Execute safe PRAGMAs
            using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;";
                await pragmaCmd.ExecuteNonQueryAsync();
            }

            // 1. Verify schema structures exist
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS issues (issue_code TEXT, severity TEXT, message TEXT, object_id TEXT, technical_details TEXT);";
                await cmd.ExecuteNonQueryAsync();
            }

            // 2. Fetch partial counts to ensure diagnostic repair accuracy
            int folders = 0;
            int messages = 0;
            int attachments = 0;
            int issues = 0;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM folders";
                folders = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM messages";
                messages = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM attachments";
                attachments = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM issues";
                issues = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }

            // 3. Perform idempotent update of Running index run
            using (var trans = conn.BeginTransaction())
            {
                try
                {
                    bool hasRunning = false;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(*) FROM index_runs WHERE case_id = $caseId AND status = 'Running';";
                        cmd.Parameters.AddWithValue("$caseId", caseId);
                        cmd.Transaction = trans;
                        hasRunning = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                    }

                    if (hasRunning)
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                            UPDATE index_runs 
                            SET status = $status, 
                                duration_ms = 0, 
                                folders_indexed = $folders, 
                                messages_indexed = $messages, 
                                attachments_indexed = $attachments, 
                                issues_detected = $issues 
                            WHERE case_id = $caseId AND status = 'Running';";
                        cmd.Parameters.AddWithValue("$status", status);
                        cmd.Parameters.AddWithValue("$folders", folders);
                        cmd.Parameters.AddWithValue("$messages", messages);
                        cmd.Parameters.AddWithValue("$attachments", attachments);
                        cmd.Parameters.AddWithValue("$issues", issues);
                        cmd.Parameters.AddWithValue("$caseId", caseId);
                        cmd.Transaction = trans;
                        await cmd.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        // Check if duplicate index run status is already present
                        bool hasTargetStatus = false;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT COUNT(*) FROM index_runs WHERE case_id = $caseId AND status = $status;";
                            cmd.Parameters.AddWithValue("$caseId", caseId);
                            cmd.Parameters.AddWithValue("$status", status);
                            cmd.Transaction = trans;
                            hasTargetStatus = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                        }

                        if (!hasTargetStatus)
                        {
                            using var cmd = conn.CreateCommand();
                            cmd.CommandText = @"
                                INSERT INTO index_runs (run_id, case_id, timestamp, status, duration_ms, folders_indexed, messages_indexed, attachments_indexed, issues_detected)
                                VALUES ($runId, $caseId, $timestamp, $status, 0, $folders, $messages, $attachments, $issues);";
                            cmd.Parameters.AddWithValue("$runId", Guid.NewGuid().ToString("N"));
                            cmd.Parameters.AddWithValue("$caseId", caseId);
                            cmd.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("o"));
                            cmd.Parameters.AddWithValue("$status", status);
                            cmd.Parameters.AddWithValue("$folders", folders);
                            cmd.Parameters.AddWithValue("$messages", messages);
                            cmd.Parameters.AddWithValue("$attachments", attachments);
                            cmd.Parameters.AddWithValue("$issues", issues);
                            cmd.Transaction = trans;
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }

                    // 4. Update case completed_at
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE case_info SET completed_at = $completed WHERE case_id = $caseId;";
                        cmd.Parameters.AddWithValue("$completed", DateTimeOffset.Now.ToString("o"));
                        cmd.Parameters.AddWithValue("$caseId", caseId);
                        cmd.Transaction = trans;
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // 5. Idempotently insert Diagnostic ExtractionIssue record
                    string issueCode = status == "Cancelled" ? ExtractionIssue.Codes.ReaderKilledByUser : ExtractionIssue.Codes.ReaderStallDetected;
                    string message = status == "Cancelled" ? "Indexação cancelada sob demanda pelo operador." : "O leitor parou de responder e foi encerrado pelo watchdog.";

                    bool issueExists = false;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(*) FROM issues WHERE issue_code = $code AND message = $msg;";
                        cmd.Parameters.AddWithValue("$code", issueCode);
                        cmd.Parameters.AddWithValue("$msg", message);
                        cmd.Transaction = trans;
                        issueExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                    }

                    if (!issueExists)
                    {
                        var lastEv = LastKnownEvent;
                        string techDetails = $"Phase: {lastEv?.Phase ?? "Unknown"}. Engine: {lastEv?.Engine ?? "Unknown"}. Folder: {lastEv?.CurrentFolderPath ?? "None"}. FoldersIndexed: {folders}. MessagesIndexed: {messages}. IssuesCount: {issues}. ErrorMessage: {errorMessage ?? "N/A"}. No email body text or attachments stored.";
                        
                        // Enforce technical details limit
                        if (techDetails.Length > 500) techDetails = techDetails.Substring(0, 497) + "...";

                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "INSERT INTO issues (issue_code, severity, message, object_id, technical_details) VALUES ($code, 'Error', $msg, 'CLI-Worker-Diagnostics', $details);";
                        cmd.Parameters.AddWithValue("$code", issueCode);
                        cmd.Parameters.AddWithValue("$msg", message);
                        cmd.Parameters.AddWithValue("$details", techDetails);
                        cmd.Transaction = trans;
                        await cmd.ExecuteNonQueryAsync();
                    }

                    await trans.CommitAsync();
                }
                catch
                {
                    await trans.RollbackAsync();
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                string repairFailedFile = Path.Combine(casePath, "reader-repair-failed.json");
                var errorObj = new
                {
                    Timestamp = DateTimeOffset.Now.ToString("o"),
                    Error = ex.Message,
                    StackTrace = ex.StackTrace,
                    CaseId = caseId,
                    Status = status,
                    ErrorMessage = errorMessage
                };
                string errorJson = JsonSerializer.Serialize(errorObj, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(repairFailedFile, errorJson);
            }
            catch
            {
                // Safe ignore if filesystem is completely read-only or full
            }
        }
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Safe ignore
        }
    }
}

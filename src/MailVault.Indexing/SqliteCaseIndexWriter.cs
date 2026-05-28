using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using Microsoft.Data.Sqlite;

namespace MailVault.Indexing;

public sealed class SqliteCaseIndexWriter : ICaseIndexWriter
{
    private readonly SqliteConnection _connection;
    private SqliteTransaction? _transaction;

    // Reusable prepared statements — compiled once, executed per row
    private SqliteCommand? _msgInsertCmd;
    private SqliteCommand? _attInsertCmd;

    public string? ActiveRunId { get; set; }

    public SqliteCaseIndexWriter(SqliteConnection connection)
    {
        _connection = connection;
    }

    public async Task BeginTransactionAsync(CancellationToken ct)
    {
        // Enforce foreign keys on this active connection before starting transaction
        using (var pragmaCmd = new SqliteCommand("PRAGMA foreign_keys = ON;", _connection))
        {
            await pragmaCmd.ExecuteNonQueryAsync(ct);
        }
        _transaction = await _connection.BeginTransactionAsync(ct) as SqliteTransaction;
    }

    public async Task CommitTransactionAsync(CancellationToken ct)
    {
        if (_transaction != null)
        {
            await _transaction.CommitAsync(ct);
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken ct)
    {
        if (_transaction != null)
        {
            await _transaction.RollbackAsync(ct);
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public async Task SaveCaseInfoAsync(string caseId, string sourceFile, long sourceSize, string sourceSha256, string operatorName, DateTimeOffset startedAt, string adapterName, string adapterVersion, CancellationToken ct)
    {
        const string query = @"
            INSERT OR REPLACE INTO case_info (case_id, source_file, source_size, source_sha256, operator_name, started_at, completed_at, adapter_name, adapter_version)
            VALUES ($caseId, $sourceFile, $sourceSize, $sourceSha256, $operatorName, $startedAt, $completedAt, $adapterName, $adapterVersion);";

        using var cmd = new SqliteCommand(query, _connection, _transaction);
        cmd.Parameters.AddWithValue("$caseId", caseId);
        cmd.Parameters.AddWithValue("$sourceFile", sourceFile);
        cmd.Parameters.AddWithValue("$sourceSize", sourceSize);
        cmd.Parameters.AddWithValue("$sourceSha256", sourceSha256);
        cmd.Parameters.AddWithValue("$operatorName", operatorName);
        cmd.Parameters.AddWithValue("$startedAt", startedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$completedAt", DBNull.Value);
        cmd.Parameters.AddWithValue("$adapterName", adapterName);
        cmd.Parameters.AddWithValue("$adapterVersion", adapterVersion);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveFolderAsync(FolderNode folder, CancellationToken ct)
    {
        const string query = @"
            INSERT OR REPLACE INTO folders (folder_id, parent_id, display_name, full_path, message_count, run_id)
            VALUES ($folderId, $parentId, $displayName, $fullPath, $messageCount, $runId);";

        using var cmd = new SqliteCommand(query, _connection, _transaction);
        cmd.Parameters.AddWithValue("$folderId", folder.Id.Value);
        object parentValue = DBNull.Value;
        if (folder.ParentId != null && 
            !string.IsNullOrEmpty(folder.ParentId.Value) && 
            !folder.ParentId.Value.EndsWith("\\Root", StringComparison.OrdinalIgnoreCase) &&
            !folder.ParentId.Value.EndsWith("/Root", StringComparison.OrdinalIgnoreCase) &&
            !folder.ParentId.Value.Equals("Root", StringComparison.OrdinalIgnoreCase))
        {
            parentValue = folder.ParentId.Value;
        }
        cmd.Parameters.AddWithValue("$parentId", parentValue);
        cmd.Parameters.AddWithValue("$displayName", folder.DisplayName);
        cmd.Parameters.AddWithValue("$fullPath", folder.FullPath);
        cmd.Parameters.AddWithValue("$messageCount", (object?)folder.MessageCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$runId", (object?)ActiveRunId ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveMessageAsync(MailItem message, FolderId folderId, CancellationToken ct)
    {
        var cmd = GetOrCreateMsgInsertCmd();
        cmd.Parameters["$messageId"].Value = message.InternalId;
        cmd.Parameters["$internetMessageId"].Value = (object?)message.InternetMessageId ?? DBNull.Value;
        cmd.Parameters["$folderId"].Value = folderId.Value;
        cmd.Parameters["$subject"].Value = (object?)message.Subject ?? DBNull.Value;
        cmd.Parameters["$sender"].Value = (object?)FormatAddress(message.From) ?? DBNull.Value;
        cmd.Parameters["$to"].Value = FormatAddressList(message.To);
        cmd.Parameters["$cc"].Value = FormatAddressList(message.Cc);
        cmd.Parameters["$bcc"].Value = FormatAddressList(message.Bcc);
        cmd.Parameters["$sentAt"].Value = (object?)message.SentAt?.ToString("o") ?? DBNull.Value;
        cmd.Parameters["$receivedAt"].Value = (object?)message.ReceivedAt?.ToString("o") ?? DBNull.Value;
        cmd.Parameters["$hasText"].Value = !string.IsNullOrEmpty(message.PlainTextBody) ? 1 : 0;
        cmd.Parameters["$hasHtml"].Value = !string.IsNullOrEmpty(message.HtmlBody) ? 1 : 0;
        cmd.Parameters["$bodyPreview"].Value = (object?)message.PlainTextBody ?? DBNull.Value;
        cmd.Parameters["$attachCount"].Value = message.Attachments.Count;
        cmd.Parameters["$mapiCount"].Value = message.RawProperties.Count;
        cmd.Parameters["$runId"].Value = (object?)ActiveRunId ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct);

        foreach (var att in message.Attachments)
            await SaveAttachmentAsync(att, message.InternalId, ct);

        foreach (var issue in message.Issues)
            await SaveIssueAsync(issue, ct);
    }

    private async Task SaveAttachmentAsync(AttachmentRef att, string messageId, CancellationToken ct)
    {
        var cmd = GetOrCreateAttInsertCmd();
        cmd.Parameters["$attachmentId"].Value = att.InternalId;
        cmd.Parameters["$messageId"].Value = messageId;
        cmd.Parameters["$fileName"].Value = (object?)att.FileName ?? DBNull.Value;
        cmd.Parameters["$contentType"].Value = (object?)att.ContentType ?? DBNull.Value;
        cmd.Parameters["$sizeBytes"].Value = (object?)att.SizeBytes ?? DBNull.Value;
        cmd.Parameters["$contentId"].Value = (object?)att.ContentId ?? DBNull.Value;
        cmd.Parameters["$isInline"].Value = att.IsInline ? 1 : 0;
        cmd.Parameters["$runId"].Value = (object?)ActiveRunId ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private SqliteCommand GetOrCreateMsgInsertCmd()
    {
        if (_msgInsertCmd == null)
        {
            const string query = @"
                INSERT OR REPLACE INTO messages (
                    message_id, internet_message_id, folder_id, subject, sender,
                    recipients_to, recipients_cc, recipients_bcc, sent_at, received_at,
                    has_text_body, has_html_body, body_preview, attachment_count, mapi_properties_count, run_id
                )
                VALUES (
                    $messageId, $internetMessageId, $folderId, $subject, $sender,
                    $to, $cc, $bcc, $sentAt, $receivedAt,
                    $hasText, $hasHtml, $bodyPreview, $attachCount, $mapiCount, $runId
                );";
            _msgInsertCmd = new SqliteCommand(query, _connection);
            _msgInsertCmd.Parameters.Add("$messageId", SqliteType.Text);
            _msgInsertCmd.Parameters.Add("$internetMessageId", SqliteType.Text);
            _msgInsertCmd.Parameters.Add("$folderId", SqliteType.Text);
            _msgInsertCmd.Parameters.Add("$subject", SqliteType.Text);
            _msgInsertCmd.Parameters.Add("$sender", SqliteType.Text);
            _msgInsertCmd.Parameters.Add("$to", SqliteType.Text);
            _msgInsertCmd.Parameters.Add("$cc", SqliteType.Text);
            _msgInsertCmd.Parameters.Add("$bcc", SqliteType.Text);
            _msgInsertCmd.Parameters.Add("$sentAt", SqliteType.Text);
            _msgInsertCmd.Parameters.Add("$receivedAt", SqliteType.Text);
            _msgInsertCmd.Parameters.Add("$hasText", SqliteType.Integer);
            _msgInsertCmd.Parameters.Add("$hasHtml", SqliteType.Integer);
            _msgInsertCmd.Parameters.Add("$bodyPreview", SqliteType.Text);
            _msgInsertCmd.Parameters.Add("$attachCount", SqliteType.Integer);
            _msgInsertCmd.Parameters.Add("$mapiCount", SqliteType.Integer);
            _msgInsertCmd.Parameters.Add("$runId", SqliteType.Text);
            _msgInsertCmd.Prepare();
        }
        _msgInsertCmd.Transaction = _transaction;
        return _msgInsertCmd;
    }

    private SqliteCommand GetOrCreateAttInsertCmd()
    {
        if (_attInsertCmd == null)
        {
            const string query = @"
                INSERT OR REPLACE INTO attachments (attachment_id, message_id, file_name, content_type, size_bytes, content_id, is_inline, run_id)
                VALUES ($attachmentId, $messageId, $fileName, $contentType, $sizeBytes, $contentId, $isInline, $runId);";
            _attInsertCmd = new SqliteCommand(query, _connection);
            _attInsertCmd.Parameters.Add("$attachmentId", SqliteType.Text);
            _attInsertCmd.Parameters.Add("$messageId", SqliteType.Text);
            _attInsertCmd.Parameters.Add("$fileName", SqliteType.Text);
            _attInsertCmd.Parameters.Add("$contentType", SqliteType.Text);
            _attInsertCmd.Parameters.Add("$sizeBytes", SqliteType.Integer);
            _attInsertCmd.Parameters.Add("$contentId", SqliteType.Text);
            _attInsertCmd.Parameters.Add("$isInline", SqliteType.Integer);
            _attInsertCmd.Parameters.Add("$runId", SqliteType.Text);
            _attInsertCmd.Prepare();
        }
        _attInsertCmd.Transaction = _transaction;
        return _attInsertCmd;
    }

    public async Task SaveIssueAsync(ExtractionIssue issue, CancellationToken ct)
    {
        // Sanitise and truncate technical_details for safety and storage compliance
        string? cleanDetails = SanitiseTechnicalDetails(issue.TechnicalDetails);

        const string query = @"
            INSERT INTO issues (issue_code, severity, message, object_id, technical_details, run_id, folder_id, message_id, phase, folder_path)
            VALUES ($code, $severity, $message, $objectId, $technicalDetails, $runId, NULL, NULL, NULL, NULL);";

        using var cmd = new SqliteCommand(query, _connection, _transaction);
        cmd.Parameters.AddWithValue("$code", issue.Code);
        cmd.Parameters.AddWithValue("$severity", issue.Severity);
        cmd.Parameters.AddWithValue("$message", issue.Message);
        cmd.Parameters.AddWithValue("$objectId", issue.ObjectId);
        cmd.Parameters.AddWithValue("$technicalDetails", (object?)cleanDetails ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$runId", (object?)ActiveRunId ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? SanitiseTechnicalDetails(string? details)
    {
        if (string.IsNullOrEmpty(details)) return null;

        string result = details;

        try
        {
            // 1. Mask private user paths (e.g. C:\Users\natha\Documents\... or similar)
            result = System.Text.RegularExpressions.Regex.Replace(
                result, 
                @"(?i)(c:\\users\\[a-z0-9_.-]+)", 
                @"C:\Users\<USER>", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
            // 2. Mask email patterns to prevent data leaks
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",
                "<email_masked>");

            // 3. Summarize massive MAPI dumps/properties
            if (result.Contains("MAPI") || result.Contains("Properties"))
            {
                if (result.Length > 200)
                {
                    result = "[MAPI Dump Sanitized] " + result.Substring(0, 150) + "...";
                }
            }
        }
        catch
        {
            // Fallback to basic string truncation if regex fails
        }

        // 4. Truncate strictly to 500 characters
        if (result.Length > 500)
        {
            result = result.Substring(0, 497) + "...";
        }

        return result;
    }

    public async Task SaveIndexRunAsync(string runId, string caseId, DateTime timestamp, string status, long durationMs, int foldersCount, int messagesCount, int attachmentsCount, int issuesCount, CancellationToken ct)
    {
        string actualRunId = runId;
        if (status.Equals("Running", StringComparison.OrdinalIgnoreCase))
        {
            ActiveRunId = runId;
        }
        else if (!string.IsNullOrEmpty(ActiveRunId))
        {
            actualRunId = ActiveRunId;
        }

        if (!status.Equals("Running", StringComparison.OrdinalIgnoreCase))
        {
            const string updateCaseQuery = "UPDATE case_info SET completed_at = $completedAt WHERE case_id = $caseId;";
            using var updateCaseCmd = new SqliteCommand(updateCaseQuery, _connection, _transaction);
            updateCaseCmd.Parameters.AddWithValue("$completedAt", DateTimeOffset.Now.ToString("o"));
            updateCaseCmd.Parameters.AddWithValue("$caseId", caseId);
            await updateCaseCmd.ExecuteNonQueryAsync(ct);
        }

        // Check if the run already exists to avoid INSERT OR REPLACE which triggers ON DELETE CASCADE in SQLite
        bool exists = false;
        using (var checkCmd = new SqliteCommand("SELECT 1 FROM index_runs WHERE run_id = $runId;", _connection, _transaction))
        {
            checkCmd.Parameters.AddWithValue("$runId", actualRunId);
            var res = checkCmd.ExecuteScalar();
            exists = res != null;
        }

        if (exists)
        {
            const string updateQuery = @"
                UPDATE index_runs 
                SET case_id = $caseId, timestamp = $timestamp, status = $status, duration_ms = $durationMs, 
                    folders_indexed = $foldersCount, messages_indexed = $messagesCount, 
                    attachments_indexed = $attachmentsCount, issues_detected = $issuesCount
                WHERE run_id = $runId;";
            using var cmd = new SqliteCommand(updateQuery, _connection, _transaction);
            cmd.Parameters.AddWithValue("$runId", actualRunId);
            cmd.Parameters.AddWithValue("$caseId", caseId);
            cmd.Parameters.AddWithValue("$timestamp", timestamp.ToString("o"));
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$durationMs", durationMs);
            cmd.Parameters.AddWithValue("$foldersCount", foldersCount);
            cmd.Parameters.AddWithValue("$messagesCount", messagesCount);
            cmd.Parameters.AddWithValue("$attachmentsCount", attachmentsCount);
            cmd.Parameters.AddWithValue("$issuesCount", issuesCount);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            const string insertQuery = @"
                INSERT INTO index_runs (run_id, case_id, timestamp, status, duration_ms, folders_indexed, messages_indexed, attachments_indexed, issues_detected)
                VALUES ($runId, $caseId, $timestamp, $status, $durationMs, $foldersCount, $messagesCount, $attachmentsCount, $issuesCount);";
            using var cmd = new SqliteCommand(insertQuery, _connection, _transaction);
            cmd.Parameters.AddWithValue("$runId", actualRunId);
            cmd.Parameters.AddWithValue("$caseId", caseId);
            cmd.Parameters.AddWithValue("$timestamp", timestamp.ToString("o"));
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$durationMs", durationMs);
            cmd.Parameters.AddWithValue("$foldersCount", foldersCount);
            cmd.Parameters.AddWithValue("$messagesCount", messagesCount);
            cmd.Parameters.AddWithValue("$attachmentsCount", attachmentsCount);
            cmd.Parameters.AddWithValue("$issuesCount", issuesCount);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string? FormatAddress(MailAddressRef? addr)
    {
        if (addr == null) return null;
        if (!string.IsNullOrEmpty(addr.Name) && !string.IsNullOrEmpty(addr.Address))
        {
            return $"{addr.Name} <{addr.Address}>";
        }
        return addr.Name ?? addr.Address;
    }

    private static string FormatAddressList(IEnumerable<MailAddressRef>? list)
    {
        if (list == null) return string.Empty;
        var formatted = new List<string>();
        foreach (var addr in list)
        {
            var str = FormatAddress(addr);
            if (!string.IsNullOrEmpty(str))
            {
                formatted.Add(str);
            }
        }
        return string.Join("; ", formatted);
    }

    public void Dispose()
    {
        _msgInsertCmd?.Dispose();
        _msgInsertCmd = null;
        _attInsertCmd?.Dispose();
        _attInsertCmd = null;

        if (_transaction != null)
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }
}

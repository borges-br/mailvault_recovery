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

    public async Task SaveCaseInfoAsync(string caseId, string sourceFile, long sourceSize, string sourceSha256, string operatorName, DateTimeOffset startedAt, CancellationToken ct)
    {
        const string query = @"
            INSERT OR REPLACE INTO case_info (case_id, source_file, source_size, source_sha256, operator_name, started_at, completed_at)
            VALUES ($caseId, $sourceFile, $sourceSize, $sourceSha256, $operatorName, $startedAt, $completedAt);";

        using var cmd = new SqliteCommand(query, _connection, _transaction);
        cmd.Parameters.AddWithValue("$caseId", caseId);
        cmd.Parameters.AddWithValue("$sourceFile", sourceFile);
        cmd.Parameters.AddWithValue("$sourceSize", sourceSize);
        cmd.Parameters.AddWithValue("$sourceSha256", sourceSha256);
        cmd.Parameters.AddWithValue("$operatorName", operatorName);
        cmd.Parameters.AddWithValue("$startedAt", startedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$completedAt", string.Empty);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveFolderAsync(FolderNode folder, CancellationToken ct)
    {
        const string query = @"
            INSERT OR REPLACE INTO folders (folder_id, parent_id, display_name, full_path, message_count)
            VALUES ($folderId, $parentId, $displayName, $fullPath, $messageCount);";

        using var cmd = new SqliteCommand(query, _connection, _transaction);
        cmd.Parameters.AddWithValue("$folderId", folder.Id.Value);
        cmd.Parameters.AddWithValue("$parentId", (object?)folder.ParentId?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$displayName", folder.DisplayName);
        cmd.Parameters.AddWithValue("$fullPath", folder.FullPath);
        cmd.Parameters.AddWithValue("$messageCount", (object?)folder.MessageCount ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveMessageAsync(MailItem message, FolderId folderId, CancellationToken ct)
    {
        const string query = @"
            INSERT OR REPLACE INTO messages (
                message_id, internet_message_id, folder_id, subject, sender, 
                recipients_to, recipients_cc, recipients_bcc, sent_at, received_at, 
                has_text_body, has_html_body, body_preview, attachment_count, mapi_properties_count
            )
            VALUES (
                $messageId, $internetMessageId, $folderId, $subject, $sender,
                $to, $cc, $bcc, $sentAt, $receivedAt,
                $hasText, $hasHtml, $bodyPreview, $attachCount, $mapiCount
            );";

        using var cmd = new SqliteCommand(query, _connection, _transaction);
        cmd.Parameters.AddWithValue("$messageId", message.InternalId);
        cmd.Parameters.AddWithValue("$internetMessageId", (object?)message.InternetMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$folderId", folderId.Value);
        cmd.Parameters.AddWithValue("$subject", (object?)message.Subject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sender", (object?)FormatAddress(message.From) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", FormatAddressList(message.To));
        cmd.Parameters.AddWithValue("$cc", FormatAddressList(message.Cc));
        cmd.Parameters.AddWithValue("$bcc", FormatAddressList(message.Bcc));
        cmd.Parameters.AddWithValue("$sentAt", (object?)message.SentAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$receivedAt", (object?)message.ReceivedAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hasText", !string.IsNullOrEmpty(message.PlainTextBody) ? 1 : 0);
        cmd.Parameters.AddWithValue("$hasHtml", !string.IsNullOrEmpty(message.HtmlBody) ? 1 : 0);
        cmd.Parameters.AddWithValue("$bodyPreview", (object?)message.PlainTextBody ?? DBNull.Value); // Body preview stored in PlainTextBody
        cmd.Parameters.AddWithValue("$attachCount", message.Attachments.Count);
        cmd.Parameters.AddWithValue("$mapiCount", message.RawProperties.Count);

        await cmd.ExecuteNonQueryAsync(ct);

        // Save attachments associated
        foreach (var att in message.Attachments)
        {
            await SaveAttachmentAsync(att, message.InternalId, ct);
        }

        // Save extraction issues associated
        foreach (var issue in message.Issues)
        {
            await SaveIssueAsync(issue, ct);
        }
    }

    private async Task SaveAttachmentAsync(AttachmentRef att, string messageId, CancellationToken ct)
    {
        const string query = @"
            INSERT OR REPLACE INTO attachments (attachment_id, message_id, file_name, content_type, size_bytes, content_id, is_inline)
            VALUES ($attachmentId, $messageId, $fileName, $contentType, $sizeBytes, $contentId, $isInline);";

        using var cmd = new SqliteCommand(query, _connection, _transaction);
        cmd.Parameters.AddWithValue("$attachmentId", att.InternalId);
        cmd.Parameters.AddWithValue("$messageId", messageId);
        cmd.Parameters.AddWithValue("$fileName", (object?)att.FileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$contentType", (object?)att.ContentType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sizeBytes", (object?)att.SizeBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$contentId", (object?)att.ContentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$isInline", att.IsInline ? 1 : 0);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveIssueAsync(ExtractionIssue issue, CancellationToken ct)
    {
        // Sanitise and truncate technical_details for safety and storage compliance
        string? cleanDetails = SanitiseTechnicalDetails(issue.TechnicalDetails);

        const string query = @"
            INSERT INTO issues (issue_code, severity, message, object_id, technical_details)
            VALUES ($code, $severity, $message, $objectId, $technicalDetails);";

        using var cmd = new SqliteCommand(query, _connection, _transaction);
        cmd.Parameters.AddWithValue("$code", issue.Code);
        cmd.Parameters.AddWithValue("$severity", issue.Severity);
        cmd.Parameters.AddWithValue("$message", issue.Message);
        cmd.Parameters.AddWithValue("$objectId", issue.ObjectId);
        cmd.Parameters.AddWithValue("$technicalDetails", (object?)cleanDetails ?? DBNull.Value);

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
        // 1. Update case completed time
        const string updateCaseQuery = "UPDATE case_info SET completed_at = $completedAt WHERE case_id = $caseId;";
        using (var updateCaseCmd = new SqliteCommand(updateCaseQuery, _connection, _transaction))
        {
            updateCaseCmd.Parameters.AddWithValue("$completedAt", DateTimeOffset.Now.ToString("o"));
            updateCaseCmd.Parameters.AddWithValue("$caseId", caseId);
            await updateCaseCmd.ExecuteNonQueryAsync(ct);
        }

        // 2. Insert index run
        const string query = @"
            INSERT INTO index_runs (run_id, case_id, timestamp, status, duration_ms, folders_indexed, messages_indexed, attachments_indexed, issues_detected)
            VALUES ($runId, $caseId, $timestamp, $status, $durationMs, $foldersCount, $messagesCount, $attachmentsCount, $issuesCount);";

        using var cmd = new SqliteCommand(query, _connection, _transaction);
        cmd.Parameters.AddWithValue("$runId", runId);
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
        if (_transaction != null)
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }
}

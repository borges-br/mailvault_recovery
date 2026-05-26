using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using Microsoft.Data.Sqlite;

namespace MailVault.Indexing;

public sealed class SqliteCaseIndexReader : ICaseIndexReader
{
    private readonly SqliteConnection _connection;

    public SqliteCaseIndexReader(SqliteConnection connection)
    {
        _connection = connection;
    }

    private async Task EnsureForeignKeysAsync(CancellationToken ct)
    {
        using (var cmd = new SqliteCommand("PRAGMA foreign_keys = ON;", _connection))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<int> GetFolderCountAsync(CancellationToken ct)
    {
        await EnsureForeignKeysAsync(ct);
        const string query = "SELECT COUNT(*) FROM folders;";
        using var cmd = new SqliteCommand(query, _connection);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res != null ? Convert.ToInt32(res) : 0;
    }

    public async Task<int> GetMessageCountAsync(CancellationToken ct)
    {
        await EnsureForeignKeysAsync(ct);
        const string query = "SELECT COUNT(*) FROM messages;";
        using var cmd = new SqliteCommand(query, _connection);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res != null ? Convert.ToInt32(res) : 0;
    }

    public async Task<int> GetAttachmentCountAsync(CancellationToken ct)
    {
        await EnsureForeignKeysAsync(ct);
        const string query = "SELECT COUNT(*) FROM attachments;";
        using var cmd = new SqliteCommand(query, _connection);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res != null ? Convert.ToInt32(res) : 0;
    }

    public async Task<int> GetIssueCountAsync(CancellationToken ct)
    {
        await EnsureForeignKeysAsync(ct);
        const string query = "SELECT COUNT(*) FROM issues;";
        using var cmd = new SqliteCommand(query, _connection);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res != null ? Convert.ToInt32(res) : 0;
    }

    public async Task<Dictionary<string, int>> GetTopFoldersByMessageCountAsync(int limit, CancellationToken ct)
    {
        await EnsureForeignKeysAsync(ct);
        const string query = @"
            SELECT display_name, message_count 
            FROM folders 
            WHERE message_count IS NOT NULL AND message_count > 0
            ORDER BY message_count DESC 
            LIMIT $limit;";

        using var cmd = new SqliteCommand(query, _connection);
        cmd.Parameters.AddWithValue("$limit", limit);

        var dict = new Dictionary<string, int>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string name = reader.GetString(0);
            int count = reader.GetInt32(1);
            dict[name] = count;
        }

        return dict;
    }

    public async Task<long> GetTotalAttachmentSizeAsync(CancellationToken ct)
    {
        await EnsureForeignKeysAsync(ct);
        const string query = "SELECT SUM(size_bytes) FROM attachments;";
        using var cmd = new SqliteCommand(query, _connection);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res != DBNull.Value && res != null ? Convert.ToInt64(res) : 0;
    }

    public async Task<(string fileName, long sizeBytes)> GetLargestAttachmentAsync(CancellationToken ct)
    {
        await EnsureForeignKeysAsync(ct);
        const string query = "SELECT file_name, size_bytes FROM attachments ORDER BY size_bytes DESC LIMIT 1;";
        using var cmd = new SqliteCommand(query, _connection);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            string name = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0);
            long size = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            return (name, size);
        }
        return ("Nenhum", 0);
    }

    public async IAsyncEnumerable<MailItem> SearchMessagesAsync(string queryText, string? folderPath, int limit, int offset, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureForeignKeysAsync(ct);
        string query = @"
            SELECT m.message_id, m.internet_message_id, m.folder_id, m.subject, m.sender, 
                   m.recipients_to, m.recipients_cc, m.recipients_bcc, m.sent_at, m.received_at, 
                   m.has_text_body, m.has_html_body, m.body_preview, m.attachment_count
            FROM messages m
            INNER JOIN folders f ON m.folder_id = f.folder_id
            WHERE (m.subject LIKE $searchTerm OR m.sender LIKE $searchTerm OR m.body_preview LIKE $searchTerm)";

        if (!string.IsNullOrEmpty(folderPath))
        {
            query += " AND (f.full_path = $folderPath OR f.folder_id = $folderPath)";
        }

        query += " ORDER BY m.received_at DESC, m.sent_at DESC LIMIT $limit OFFSET $offset;";

        using var cmd = new SqliteCommand(query, _connection);
        cmd.Parameters.AddWithValue("$searchTerm", $"%{queryText}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        if (!string.IsNullOrEmpty(folderPath))
        {
            cmd.Parameters.AddWithValue("$folderPath", folderPath);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return await MapReaderToMailItemAsync(reader, ct);
        }
    }

    public async IAsyncEnumerable<FolderNode> GetFolderHierarchyAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureForeignKeysAsync(ct);
        const string query = "SELECT folder_id, parent_id, display_name, full_path, message_count FROM folders;";
        using var cmd = new SqliteCommand(query, _connection);

        var allFolders = new List<FolderNodeRaw>();
        using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                allFolders.Add(new FolderNodeRaw(
                    Id: reader.GetString(0),
                    ParentId: reader.IsDBNull(1) ? null : reader.GetString(1),
                    DisplayName: reader.GetString(2),
                    FullPath: reader.GetString(3),
                    MessageCount: reader.IsDBNull(4) ? 0 : reader.GetInt32(4)
                ));
            }
        }

        var roots = new List<FolderNode>();
        var folderLookup = new Dictionary<string, List<FolderNode>>();

        foreach (var f in allFolders)
        {
            if (f.ParentId != null)
            {
                if (!folderLookup.ContainsKey(f.ParentId))
                {
                    folderLookup[f.ParentId] = new List<FolderNode>();
                }
            }
        }

        var rootItems = allFolders.FindAll(f => f.ParentId == null);
        foreach (var r in rootItems)
        {
            roots.Add(BuildFolderNodeRecursive(r, allFolders, folderLookup));
        }

        foreach (var root in roots)
        {
            yield return root;
        }
    }

    private FolderNode BuildFolderNodeRecursive(FolderNodeRaw current, List<FolderNodeRaw> all, Dictionary<string, List<FolderNode>> lookup)
    {
        var children = new List<FolderNode>();
        var childRaws = all.FindAll(f => f.ParentId == current.Id);
        foreach (var childRaw in childRaws)
        {
            children.Add(BuildFolderNodeRecursive(childRaw, all, lookup));
        }

        return new FolderNode(
            Id: new FolderId(current.Id),
            ParentId: current.ParentId != null ? new FolderId(current.ParentId) : null,
            DisplayName: current.DisplayName,
            FullPath: current.FullPath,
            MessageCount: current.MessageCount,
            Children: children
        );
    }

    public async IAsyncEnumerable<MailItem> GetMessagesInFolderAsync(FolderId folderId, int limit, int offset, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureForeignKeysAsync(ct);
        const string query = @"
            SELECT message_id, internet_message_id, folder_id, subject, sender, 
                   recipients_to, recipients_cc, recipients_bcc, sent_at, received_at, 
                   has_text_body, has_html_body, body_preview, attachment_count
            FROM messages
            WHERE folder_id = $folderId
            ORDER BY received_at DESC, sent_at DESC
            LIMIT $limit OFFSET $offset;";

        using var cmd = new SqliteCommand(query, _connection);
        cmd.Parameters.AddWithValue("$folderId", folderId.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return await MapReaderToMailItemAsync(reader, ct);
        }
    }

    public async Task<MailItem?> GetMessageByIdAsync(MessageId messageId, CancellationToken ct)
    {
        await EnsureForeignKeysAsync(ct);
        const string query = @"
            SELECT message_id, internet_message_id, folder_id, subject, sender, 
                   recipients_to, recipients_cc, recipients_bcc, sent_at, received_at, 
                   has_text_body, has_html_body, body_preview, attachment_count
            FROM messages
            WHERE message_id = $messageId LIMIT 1;";

        using var cmd = new SqliteCommand(query, _connection);
        cmd.Parameters.AddWithValue("$messageId", messageId.Value);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return await MapReaderToMailItemAsync(reader, ct);
        }

        return null;
    }

    public async Task<CaseInfoRef?> GetCaseInfoAsync(CancellationToken ct)
    {
        await EnsureForeignKeysAsync(ct);
        const string query = @"
            SELECT case_id, source_file, source_size, source_sha256, operator_name, started_at, adapter_name, adapter_version
            FROM case_info LIMIT 1;";

        using var cmd = new SqliteCommand(query, _connection);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            string caseId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            string sourceFile = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            long sourceSize = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            string sourceSha256 = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            string operatorName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            string startedAtStr = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
            string adapterName = reader.IsDBNull(6) ? "Unknown" : reader.GetString(6);
            string adapterVersion = reader.IsDBNull(7) ? "1.0.0.0" : reader.GetString(7);
            DateTimeOffset startedAt = DateTimeOffset.TryParse(startedAtStr, out var parsedStartedAt)
                ? parsedStartedAt
                : DateTimeOffset.MinValue;

            return new CaseInfoRef(
                CaseId: caseId,
                SourceFile: sourceFile,
                SourceSizeBytes: sourceSize,
                SourceSha256: sourceSha256,
                OperatorName: operatorName,
                StartedAt: startedAt,
                AdapterName: adapterName,
                AdapterVersion: adapterVersion
            );
        }

        return null;
    }

    private async Task<MailItem> MapReaderToMailItemAsync(SqliteDataReader reader, CancellationToken ct)
    {
        string id = reader.GetString(0);
        string? internetMessageId = reader.IsDBNull(1) ? null : reader.GetString(1);
        string? subject = reader.IsDBNull(3) ? null : reader.GetString(3);
        string? sender = reader.IsDBNull(4) ? null : reader.GetString(4);
        string recipientsTo = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
        string recipientsCc = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
        string recipientsBcc = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
        string? sentAtStr = reader.IsDBNull(8) ? null : reader.GetString(8);
        string? receivedAtStr = reader.IsDBNull(9) ? null : reader.GetString(9);
        string? bodyPreview = reader.IsDBNull(12) ? null : reader.GetString(12);

        var fromRef = ParseAddress(sender);
        var toList = ParseAddressList(recipientsTo);
        var ccList = ParseAddressList(recipientsCc);
        var bccList = ParseAddressList(recipientsBcc);

        DateTimeOffset? sentAt = sentAtStr != null ? DateTimeOffset.Parse(sentAtStr) : null;
        DateTimeOffset? receivedAt = receivedAtStr != null ? DateTimeOffset.Parse(receivedAtStr) : null;

        // Load attachments associated
        var attachList = new List<AttachmentRef>();
        const string attQuery = "SELECT attachment_id, file_name, content_type, size_bytes, content_id, is_inline FROM attachments WHERE message_id = $messageId;";
        using (var cmd = new SqliteCommand(attQuery, _connection))
        {
            cmd.Parameters.AddWithValue("$messageId", id);
            using var attReader = await cmd.ExecuteReaderAsync(ct);
            while (await attReader.ReadAsync(ct))
            {
                attachList.Add(new AttachmentRef(
                    InternalId: attReader.GetString(0),
                    FileName: attReader.IsDBNull(1) ? null : attReader.GetString(1),
                    ContentType: attReader.IsDBNull(2) ? null : attReader.GetString(2),
                    SizeBytes: attReader.IsDBNull(3) ? null : attReader.GetInt64(3),
                    ContentId: attReader.IsDBNull(4) ? null : attReader.GetString(4),
                    IsInline: attReader.GetInt32(5) == 1
                ));
            }
        }

        // Load issues associated
        var issuesList = new List<ExtractionIssue>();
        const string issueQuery = "SELECT issue_code, severity, message, technical_details FROM issues WHERE object_id = $objectId;";
        using (var cmd = new SqliteCommand(issueQuery, _connection))
        {
            cmd.Parameters.AddWithValue("$objectId", id);
            using var issueReader = await cmd.ExecuteReaderAsync(ct);
            while (await issueReader.ReadAsync(ct))
            {
                issuesList.Add(new ExtractionIssue(
                    Code: issueReader.GetString(0),
                    Severity: issueReader.GetString(1),
                    Message: issueReader.GetString(2),
                    ObjectId: id,
                    TechnicalDetails: issueReader.IsDBNull(3) ? null : issueReader.GetString(3)
                ));
            }
        }

        return new MailItem(
            InternalId: id,
            InternetMessageId: internetMessageId,
            Subject: subject,
            From: fromRef,
            To: toList,
            Cc: ccList,
            Bcc: bccList,
            SentAt: sentAt,
            ReceivedAt: receivedAt,
            PlainTextBody: bodyPreview, // Preview body is stored in PlainTextBody
            HtmlBody: null,
            Attachments: attachList,
            RawProperties: new Dictionary<string, string>(), // Do not cache MAPI raw properties completely
            Issues: issuesList
        );
    }

    private static MailAddressRef? ParseAddress(string? str)
    {
        if (string.IsNullOrEmpty(str)) return null;
        if (str.Contains("<") && str.Contains(">"))
        {
            int idx = str.IndexOf("<");
            string name = str.Substring(0, idx).Trim();
            string email = str.Substring(idx + 1, str.Length - idx - 2).Trim();
            return new MailAddressRef(name, email);
        }
        return new MailAddressRef(str, null);
    }

    private static List<MailAddressRef> ParseAddressList(string str)
    {
        var list = new List<MailAddressRef>();
        if (string.IsNullOrEmpty(str)) return list;
        var parts = str.Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            var addr = ParseAddress(p);
            if (addr != null)
            {
                list.Add(addr);
            }
        }
        return list;
    }

    public void Dispose()
    {
        // Connection is owned and managed by the Store, so Reader does not close it here
    }

    private record FolderNodeRaw(string Id, string? ParentId, string DisplayName, string FullPath, int MessageCount);
}

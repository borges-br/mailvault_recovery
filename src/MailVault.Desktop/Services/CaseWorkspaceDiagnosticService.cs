using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MailVault.Desktop.Services;

/// <summary>
/// Result of a case workspace diagnostic check.
/// </summary>
public sealed record CaseValidationResult
{
    public string CaseFolderPath { get; init; } = "";
    public string CaseDbPath { get; init; } = "";
    public bool DirectoryExists { get; init; }
    public bool CaseDbExists { get; init; }
    public bool ManifestExists { get; init; }
    public bool AuditLogExists { get; init; }
    public bool JournalFileExists { get; init; }
    public bool CaseDbReadable { get; init; }
    public int SchemaVersion { get; init; }
    public bool CaseInfoExists => CaseInfoRowCount > 0;
    public bool TablesExist { get; init; }
    public bool SchemaValid => CaseDbReadable && TablesExist && MissingSchemaObjects.Count == 0;
    public int CaseInfoRowCount { get; init; }
    public int FolderRowCount { get; init; }
    public int MessageRowCount { get; init; }
    public int AttachmentRowCount { get; init; }
    public int IssueRowCount { get; init; }
    public IReadOnlyList<string> MissingSchemaObjects { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? ErrorMessage { get; init; }
    public string? SuggestedAction { get; init; }
    public bool HasWarnings => Warnings.Count > 0;
    public bool IsHealthy => DirectoryExists && CaseDbExists && SchemaValid && !HasWarnings && MessageRowCount > 0;
    public bool CanOpenLimited => DirectoryExists && CaseDbExists && SchemaValid;
}

/// <summary>
/// Inspects a case folder path and returns a diagnostic report.
/// Does not delete files and does not treat manifest or journal warnings as fatal.
/// </summary>
public sealed class CaseWorkspaceDiagnosticService
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredSchema = new Dictionary<string, string[]>
    {
        ["schema_version"] = new[] { "version" },
        ["case_info"] = new[]
        {
            "case_id", "source_file", "source_size", "source_sha256", "operator_name",
            "started_at", "completed_at", "adapter_name", "adapter_version"
        },
        ["folders"] = new[] { "folder_id", "parent_id", "display_name", "full_path", "message_count" },
        ["messages"] = new[]
        {
            "message_id", "internet_message_id", "folder_id", "subject", "sender",
            "recipients_to", "recipients_cc", "recipients_bcc", "sent_at", "received_at",
            "has_text_body", "has_html_body", "body_preview", "attachment_count", "mapi_properties_count"
        },
        ["attachments"] = new[] { "attachment_id", "message_id", "file_name", "content_type", "size_bytes", "content_id", "is_inline" },
        ["issues"] = new[] { "issue_code", "severity", "message", "object_id", "technical_details" }
    };

    public async Task<CaseValidationResult> DiagnoseAsync(string caseFolderPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseFolderPath))
        {
            return new CaseValidationResult
            {
                ErrorMessage = "Caminho de pasta não pode ser vazio.",
                SuggestedAction = "Especifique um caminho de pasta de caso válido."
            };
        }

        bool dirExists = Directory.Exists(caseFolderPath);
        string dbPath = Path.Combine(caseFolderPath, "case.db");
        string manifestPath = Path.Combine(caseFolderPath, "manifest.json");
        string auditPath = Path.Combine(caseFolderPath, "audit.log");
        string journalPath = Path.Combine(caseFolderPath, "case.db-journal");
        string walPath = Path.Combine(caseFolderPath, "case.db-wal");

        if (!dirExists)
        {
            return new CaseValidationResult
            {
                CaseFolderPath = caseFolderPath,
                CaseDbPath = dbPath,
                DirectoryExists = false,
                ErrorMessage = $"Diretório não encontrado: {caseFolderPath}",
                SuggestedAction = "Verifique se o caminho está correto e se o dispositivo está conectado."
            };
        }

        bool dbExists = File.Exists(dbPath);
        bool manifestExists = File.Exists(manifestPath);
        bool auditExists = File.Exists(auditPath);
        bool journalExists = File.Exists(journalPath) || File.Exists(walPath);

        var warnings = new List<string>();
        if (!manifestExists)
        {
            warnings.Add("manifest.json ausente. Case aberto em modo limitado.");
        }

        if (journalExists)
        {
            warnings.Add("case.db-journal detectado.");
        }

        if (!dbExists)
        {
            return new CaseValidationResult
            {
                CaseFolderPath = caseFolderPath,
                CaseDbPath = dbPath,
                DirectoryExists = true,
                CaseDbExists = false,
                ManifestExists = manifestExists,
                AuditLogExists = auditExists,
                JournalFileExists = journalExists,
                Warnings = warnings,
                ErrorMessage = "case.db não encontrado na pasta especificada.",
                SuggestedAction = "Execute 'mailvault index' para criar o banco de dados do caso."
            };
        }

        bool readable = false;
        int schemaVersion = 0;
        int caseInfoRows = 0;
        int folderRows = 0;
        int messageRows = 0;
        int attachmentRows = 0;
        int issueRows = 0;
        string? errorMessage = null;
        string? suggestedAction = null;
        var missingSchemaObjects = new List<string>();

        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            await using var conn = new SqliteConnection(connStr);
            await conn.OpenAsync(ct);
            readable = true;

            await ExecuteNonQueryAsync(conn, "PRAGMA foreign_keys = ON;", ct);

            missingSchemaObjects.AddRange(await FindMissingSchemaObjectsAsync(conn, ct));
            bool schemaValid = missingSchemaObjects.Count == 0;

            if (await TableExistsAsync(conn, "schema_version", ct))
            {
                schemaVersion = await ExecuteIntAsync(conn, "SELECT version FROM schema_version LIMIT 1;", ct);
            }

            if (schemaValid)
            {
                caseInfoRows = await CountRowsAsync(conn, "case_info", ct);
                folderRows = await CountRowsAsync(conn, "folders", ct);
                messageRows = await CountRowsAsync(conn, "messages", ct);
                attachmentRows = await CountRowsAsync(conn, "attachments", ct);
                issueRows = await CountRowsAsync(conn, "issues", ct);
            }
            else
            {
                errorMessage = "Schema do case.db incompleto ou incompatível.";
                suggestedAction = "Recrie o índice com 'mailvault index --force' para gerar um case.db compatível.";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            readable = false;
            errorMessage = $"Erro ao ler case.db: {ex.Message}";
            suggestedAction = "O banco pode estar corrompido. Tente recriar com 'mailvault index --force'.";
        }

        return new CaseValidationResult
        {
            CaseFolderPath = caseFolderPath,
            CaseDbPath = dbPath,
            DirectoryExists = true,
            CaseDbExists = true,
            ManifestExists = manifestExists,
            AuditLogExists = auditExists,
            JournalFileExists = journalExists,
            CaseDbReadable = readable,
            SchemaVersion = schemaVersion,
            TablesExist = missingSchemaObjects.All(item => !item.StartsWith("table:", StringComparison.Ordinal)),
            CaseInfoRowCount = caseInfoRows,
            FolderRowCount = folderRows,
            MessageRowCount = messageRows,
            AttachmentRowCount = attachmentRows,
            IssueRowCount = issueRows,
            MissingSchemaObjects = missingSchemaObjects,
            Warnings = warnings,
            ErrorMessage = errorMessage,
            SuggestedAction = suggestedAction
        };
    }

    private static async Task<IReadOnlyList<string>> FindMissingSchemaObjectsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var missing = new List<string>();

        foreach (var (tableName, requiredColumns) in RequiredSchema)
        {
            if (!await TableExistsAsync(conn, tableName, ct))
            {
                missing.Add($"table:{tableName}");
                continue;
            }

            var existingColumns = await GetColumnsAsync(conn, tableName, ct);
            foreach (string column in requiredColumns)
            {
                if (!existingColumns.Contains(column))
                {
                    missing.Add($"column:{tableName}.{column}");
                }
            }
        }

        return missing;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string tableName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result ?? 0L) > 0;
    }

    private static async Task<HashSet<string>> GetColumnsAsync(SqliteConnection conn, string tableName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{EscapeIdentifier(tableName)}\");";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<int> CountRowsAsync(SqliteConnection conn, string tableName, CancellationToken ct)
    {
        return await ExecuteIntAsync(conn, $"SELECT COUNT(*) FROM \"{EscapeIdentifier(tableName)}\";", ct);
    }

    private static async Task<int> ExecuteIntAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result ?? 0);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string EscapeIdentifier(string identifier)
    {
        return identifier.Replace("\"", "\"\"");
    }
}

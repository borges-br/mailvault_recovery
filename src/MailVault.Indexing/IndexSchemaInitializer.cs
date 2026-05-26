using System;
using Microsoft.Data.Sqlite;

namespace MailVault.Indexing;

public static class IndexSchemaInitializer
{
    public static void Initialize(SqliteConnection connection)
    {
        // 1. Enable foreign keys explicitly on connection
        using (var pragmaCmd = new SqliteCommand("PRAGMA foreign_keys = ON;", connection))
        {
            pragmaCmd.ExecuteNonQuery();
        }

        // 2. Create schema versioning table
        using (var schemaVerTableCmd = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY);", connection))
        {
            schemaVerTableCmd.ExecuteNonQuery();
        }

        // Check if schema version already exists
        long currentVersion = 0;
        using (var selectVerCmd = new SqliteCommand("SELECT version FROM schema_version LIMIT 1;", connection))
        {
            var res = selectVerCmd.ExecuteScalar();
            if (res != null)
            {
                currentVersion = Convert.ToInt64(res);
            }
        }

        if (currentVersion == 0)
        {
            // Apply Schema v2
            ApplySchemaV2(connection);

            using (var insertVerCmd = new SqliteCommand("INSERT INTO schema_version (version) VALUES (2);", connection))
            {
                insertVerCmd.ExecuteNonQuery();
            }
        }
        else if (currentVersion != 2)
        {
            throw new InvalidOperationException($"Incompatibilidade de schema detectada. Versão atual do case.db é {currentVersion}, mas a aplicação suporta apenas a versão 2. Por favor, utilize a opção --force para recriar o banco do caso.");
        }
    }

    private static void ApplySchemaV2(SqliteConnection connection)
    {
        // Table: case_info
        ExecuteDDL(@"
            CREATE TABLE case_info (
                case_id TEXT PRIMARY KEY,
                source_file TEXT,
                source_size INTEGER,
                source_sha256 TEXT,
                operator_name TEXT,
                started_at TEXT,
                completed_at TEXT,
                adapter_name TEXT,
                adapter_version TEXT
            );", connection);

        // Table: folders
        ExecuteDDL(@"
            CREATE TABLE folders (
                folder_id TEXT PRIMARY KEY,
                parent_id TEXT,
                display_name TEXT,
                full_path TEXT,
                message_count INTEGER,
                FOREIGN KEY(parent_id) REFERENCES folders(folder_id)
            );", connection);

        // Table: messages
        ExecuteDDL(@"
            CREATE TABLE messages (
                message_id TEXT PRIMARY KEY,
                internet_message_id TEXT,
                folder_id TEXT,
                subject TEXT,
                sender TEXT,
                recipients_to TEXT,
                recipients_cc TEXT,
                recipients_bcc TEXT,
                sent_at TEXT,
                received_at TEXT,
                has_text_body INTEGER,
                has_html_body INTEGER,
                body_preview TEXT,
                attachment_count INTEGER,
                mapi_properties_count INTEGER,
                FOREIGN KEY(folder_id) REFERENCES folders(folder_id)
            );", connection);

        // Table: attachments
        ExecuteDDL(@"
            CREATE TABLE attachments (
                attachment_id TEXT PRIMARY KEY,
                message_id TEXT,
                file_name TEXT,
                content_type TEXT,
                size_bytes INTEGER,
                content_id TEXT,
                is_inline INTEGER,
                FOREIGN KEY(message_id) REFERENCES messages(message_id)
            );", connection);

        // Table: issues
        ExecuteDDL(@"
            CREATE TABLE issues (
                issue_code TEXT,
                severity TEXT,
                message TEXT,
                object_id TEXT,
                technical_details TEXT
            );", connection);

        // Table: index_runs
        ExecuteDDL(@"
            CREATE TABLE index_runs (
                run_id TEXT PRIMARY KEY,
                case_id TEXT,
                timestamp TEXT,
                status TEXT,
                duration_ms INTEGER,
                folders_indexed INTEGER,
                messages_indexed INTEGER,
                attachments_indexed INTEGER,
                issues_detected INTEGER,
                FOREIGN KEY(case_id) REFERENCES case_info(case_id)
            );", connection);

        // Required indexes
        ExecuteDDL("CREATE INDEX idx_messages_folder_id ON messages(folder_id);", connection);
        ExecuteDDL("CREATE INDEX idx_messages_subject ON messages(subject);", connection);
        ExecuteDDL("CREATE INDEX idx_messages_sender ON messages(sender);", connection);
        ExecuteDDL("CREATE INDEX idx_attachments_message_id ON attachments(message_id);", connection);
        ExecuteDDL("CREATE INDEX idx_issues_object_id ON issues(object_id);", connection);
    }

    private static void ExecuteDDL(string ddl, SqliteConnection connection)
    {
        using (var cmd = new SqliteCommand(ddl, connection))
        {
            cmd.ExecuteNonQuery();
        }
    }
}

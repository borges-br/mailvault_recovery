using System;
using Microsoft.Data.Sqlite;

namespace MailVault.Indexing;

public static class IndexSchemaInitializer
{
    public static void Initialize(SqliteConnection connection)
    {
        // 1. Enable foreign keys explicitly on connection
        using (var pragmaCmd = new SqliteCommand("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;", connection))
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
            // Apply Schema v3
            ApplySchemaV3(connection);

            using (var insertVerCmd = new SqliteCommand("INSERT INTO schema_version (version) VALUES (3);", connection))
            {
                insertVerCmd.ExecuteNonQuery();
            }
        }
        else if (currentVersion == 2)
        {
            // Migrate from V2 to V3!
            MigrateV2ToV3(connection);

            using (var updateVerCmd = new SqliteCommand("DELETE FROM schema_version; INSERT INTO schema_version (version) VALUES (3);", connection))
            {
                updateVerCmd.ExecuteNonQuery();
            }
        }
        else if (currentVersion != 3)
        {
            throw new InvalidOperationException($"Incompatibilidade de schema detectada. Versão atual do case.db é {currentVersion}, mas a aplicação suporta apenas a versão 3. Por favor, utilize a opção --force para recriar o banco do caso.");
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

    private static void ApplySchemaV3(SqliteConnection connection)
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
                FOREIGN KEY(case_id) REFERENCES case_info(case_id) ON DELETE CASCADE
            );", connection);

        // Table: folders
        ExecuteDDL(@"
            CREATE TABLE folders (
                folder_id TEXT PRIMARY KEY,
                parent_id TEXT,
                display_name TEXT,
                full_path TEXT,
                message_count INTEGER,
                run_id TEXT,
                FOREIGN KEY(parent_id) REFERENCES folders(folder_id) ON DELETE CASCADE,
                FOREIGN KEY(run_id) REFERENCES index_runs(run_id) ON DELETE CASCADE
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
                run_id TEXT,
                FOREIGN KEY(folder_id) REFERENCES folders(folder_id) ON DELETE CASCADE,
                FOREIGN KEY(run_id) REFERENCES index_runs(run_id) ON DELETE CASCADE
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
                run_id TEXT,
                FOREIGN KEY(message_id) REFERENCES messages(message_id) ON DELETE CASCADE,
                FOREIGN KEY(run_id) REFERENCES index_runs(run_id) ON DELETE CASCADE
            );", connection);

        // Table: issues
        ExecuteDDL(@"
            CREATE TABLE issues (
                issue_code TEXT,
                severity TEXT,
                message TEXT,
                object_id TEXT,
                technical_details TEXT,
                run_id TEXT,
                folder_id TEXT NULL,
                message_id TEXT NULL,
                phase TEXT NULL,
                folder_path TEXT NULL,
                FOREIGN KEY(run_id) REFERENCES index_runs(run_id) ON DELETE CASCADE,
                FOREIGN KEY(folder_id) REFERENCES folders(folder_id) ON DELETE SET NULL,
                FOREIGN KEY(message_id) REFERENCES messages(message_id) ON DELETE SET NULL
            );", connection);

        // Required indexes
        ExecuteDDL("CREATE INDEX idx_messages_folder_id ON messages(folder_id);", connection);
        ExecuteDDL("CREATE INDEX idx_messages_subject ON messages(subject);", connection);
        ExecuteDDL("CREATE INDEX idx_messages_sender ON messages(sender);", connection);
        ExecuteDDL("CREATE INDEX idx_attachments_message_id ON attachments(message_id);", connection);
        ExecuteDDL("CREATE INDEX idx_issues_object_id ON issues(object_id);", connection);
    }

    private static bool TableExists(string tableName, SqliteConnection connection)
    {
        using (var cmd = new SqliteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name=@name;", connection))
        {
            cmd.Parameters.AddWithValue("@name", tableName);
            var res = cmd.ExecuteScalar();
            return res != null;
        }
    }

    private static void MigrateV2ToV3(SqliteConnection connection)
    {
        using (var cmd = new SqliteCommand("PRAGMA foreign_keys = OFF;", connection))
        {
            cmd.ExecuteNonQuery();
        }

        try
        {
            // Rename existing V2 tables to _old if they exist
            bool hasIndexRuns = TableExists("index_runs", connection);
            bool hasFolders = TableExists("folders", connection);
            bool hasMessages = TableExists("messages", connection);
            bool hasAttachments = TableExists("attachments", connection);
            bool hasIssues = TableExists("issues", connection);
            bool hasCaseInfo = TableExists("case_info", connection);

            if (hasIndexRuns) ExecuteDDL("ALTER TABLE index_runs RENAME TO index_runs_old;", connection);
            if (hasFolders) ExecuteDDL("ALTER TABLE folders RENAME TO folders_old;", connection);
            if (hasMessages) ExecuteDDL("ALTER TABLE messages RENAME TO messages_old;", connection);
            if (hasAttachments) ExecuteDDL("ALTER TABLE attachments RENAME TO attachments_old;", connection);
            if (hasIssues) ExecuteDDL("ALTER TABLE issues RENAME TO issues_old;", connection);
            if (hasCaseInfo) ExecuteDDL("ALTER TABLE case_info RENAME TO case_info_old;", connection);

            // Drop old indexes first, since we will recreate them in ApplySchemaV3
            ExecuteDDL("DROP INDEX IF EXISTS idx_messages_folder_id;", connection);
            ExecuteDDL("DROP INDEX IF EXISTS idx_messages_subject;", connection);
            ExecuteDDL("DROP INDEX IF EXISTS idx_messages_sender;", connection);
            ExecuteDDL("DROP INDEX IF EXISTS idx_attachments_message_id;", connection);
            ExecuteDDL("DROP INDEX IF EXISTS idx_issues_object_id;", connection);

            // Re-create the tables with schema V3
            ApplySchemaV3(connection);

            // Copy data from old tables to V3 tables
            if (hasCaseInfo)
            {
                ExecuteDDL(@"
                    INSERT INTO case_info (case_id, source_file, source_size, source_sha256, operator_name, started_at, completed_at, adapter_name, adapter_version)
                    SELECT case_id, source_file, source_size, source_sha256, operator_name, started_at, completed_at, adapter_name, adapter_version
                    FROM case_info_old;", connection);
            }

            if (hasIndexRuns)
            {
                ExecuteDDL(@"
                    INSERT INTO index_runs (run_id, case_id, timestamp, status, duration_ms, folders_indexed, messages_indexed, attachments_indexed, issues_detected)
                    SELECT run_id, case_id, timestamp, status, duration_ms, folders_indexed, messages_indexed, attachments_indexed, issues_detected
                    FROM index_runs_old;", connection);
            }

            if (hasFolders)
            {
                ExecuteDDL(@"
                    INSERT INTO folders (folder_id, parent_id, display_name, full_path, message_count, run_id)
                    SELECT folder_id, parent_id, display_name, full_path, message_count, NULL
                    FROM folders_old;", connection);
            }

            if (hasMessages)
            {
                ExecuteDDL(@"
                    INSERT INTO messages (message_id, internet_message_id, folder_id, subject, sender, recipients_to, recipients_cc, recipients_bcc, sent_at, received_at, has_text_body, has_html_body, body_preview, attachment_count, mapi_properties_count, run_id)
                    SELECT message_id, internet_message_id, folder_id, subject, sender, recipients_to, recipients_cc, recipients_bcc, sent_at, received_at, has_text_body, has_html_body, body_preview, attachment_count, mapi_properties_count, NULL
                    FROM messages_old;", connection);
            }

            if (hasAttachments)
            {
                ExecuteDDL(@"
                    INSERT INTO attachments (attachment_id, message_id, file_name, content_type, size_bytes, content_id, is_inline, run_id)
                    SELECT attachment_id, message_id, file_name, content_type, size_bytes, content_id, is_inline, NULL
                    FROM attachments_old;", connection);
            }

            if (hasIssues)
            {
                ExecuteDDL(@"
                    INSERT INTO issues (issue_code, severity, message, object_id, technical_details, run_id, folder_id, message_id, phase, folder_path)
                    SELECT issue_code, severity, message, object_id, technical_details, NULL, NULL, NULL, NULL, NULL
                    FROM issues_old;", connection);
            }

            // Drop temporary _old tables
            if (hasIndexRuns) ExecuteDDL("DROP TABLE index_runs_old;", connection);
            if (hasFolders) ExecuteDDL("DROP TABLE folders_old;", connection);
            if (hasMessages) ExecuteDDL("DROP TABLE messages_old;", connection);
            if (hasAttachments) ExecuteDDL("DROP TABLE attachments_old;", connection);
            if (hasIssues) ExecuteDDL("DROP TABLE issues_old;", connection);
            if (hasCaseInfo) ExecuteDDL("DROP TABLE case_info_old;", connection);
        }
        finally
        {
            using (var cmd = new SqliteCommand("PRAGMA foreign_keys = ON;", connection))
            {
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static void ExecuteDDL(string ddl, SqliteConnection connection)
    {
        using (var cmd = new SqliteCommand(ddl, connection))
        {
            cmd.ExecuteNonQuery();
        }
    }
}

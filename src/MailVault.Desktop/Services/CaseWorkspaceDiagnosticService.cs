using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MailVault.Desktop.Services;

/// <summary>
/// Result of a case workspace diagnostic check.
/// </summary>
public sealed record CaseValidationResult
{
    public bool DirectoryExists { get; init; }
    public bool CaseDbExists { get; init; }
    public bool ManifestExists { get; init; }
    public bool AuditLogExists { get; init; }
    public bool JournalFileExists { get; init; }
    public bool CaseDbReadable { get; init; }
    public int SchemaVersion { get; init; }
    public bool CaseInfoExists { get; init; }
    public bool TablesExist { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SuggestedAction { get; init; }
    public bool IsHealthy => DirectoryExists && CaseDbExists && CaseDbReadable && TablesExist;
    public bool CanOpenLimited => DirectoryExists && CaseDbExists && CaseDbReadable;
}

/// <summary>
/// Inspects a case folder path and returns a diagnostic report.
/// Does NOT delete files, does NOT throw fatal exceptions.
/// Journal presence is treated as a warning, not a fatal error.
/// </summary>
public sealed class CaseWorkspaceDiagnosticService
{
    private static readonly string[] RequiredTables =
    {
        "case_info", "folders", "messages", "attachments", "issues"
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
        if (!dirExists)
        {
            return new CaseValidationResult
            {
                DirectoryExists = false,
                ErrorMessage = $"Diretório não encontrado: {caseFolderPath}",
                SuggestedAction = "Verifique se o caminho está correto e o dispositivo está conectado."
            };
        }

        string dbPath = Path.Combine(caseFolderPath, "case.db");
        string manifestPath = Path.Combine(caseFolderPath, "manifest.json");
        string auditPath = Path.Combine(caseFolderPath, "audit.log");
        string journalPath = Path.Combine(caseFolderPath, "case.db-journal");
        string walPath = Path.Combine(caseFolderPath, "case.db-wal");

        bool dbExists = File.Exists(dbPath);
        bool manifestExists = File.Exists(manifestPath);
        bool auditExists = File.Exists(auditPath);
        bool journalExists = File.Exists(journalPath) || File.Exists(walPath);

        if (!dbExists)
        {
            return new CaseValidationResult
            {
                DirectoryExists = true,
                CaseDbExists = false,
                ManifestExists = manifestExists,
                AuditLogExists = auditExists,
                JournalFileExists = journalExists,
                ErrorMessage = "case.db não encontrado na pasta especificada.",
                SuggestedAction = "Execute 'mailvault index' para criar o banco de dados do caso."
            };
        }

        // Try to open and query the SQLite database
        bool readable = false;
        bool caseInfoExists = false;
        bool tablesExist = false;
        int schemaVersion = 0;
        string? errorMessage = null;
        string? suggestedAction = null;

        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            await using var conn = new SqliteConnection(connStr);
            await conn.OpenAsync(ct);

            // PRAGMA foreign_keys ON
            await using (var fkCmd = conn.CreateCommand())
            {
                fkCmd.CommandText = "PRAGMA foreign_keys = ON;";
                await fkCmd.ExecuteNonQueryAsync(ct);
            }

            // Read schema version
            await using (var svCmd = conn.CreateCommand())
            {
                svCmd.CommandText = "PRAGMA user_version;";
                var result = await svCmd.ExecuteScalarAsync(ct);
                schemaVersion = result is long lv ? (int)lv : 0;
            }

            // Check if required tables exist
            int tableCount = 0;
            await using (var tCmd = conn.CreateCommand())
            {
                tCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('case_info','folders','messages','attachments','issues');";
                var result = await tCmd.ExecuteScalarAsync(ct);
                tableCount = result is long lt ? (int)lt : 0;
            }

            tablesExist = tableCount == RequiredTables.Length;

            // Check if case_info has at least one row
            if (tablesExist)
            {
                await using var ciCmd = conn.CreateCommand();
                ciCmd.CommandText = "SELECT COUNT(*) FROM case_info LIMIT 1;";
                var result = await ciCmd.ExecuteScalarAsync(ct);
                caseInfoExists = result is long lc && lc > 0;
            }

            readable = true;

            if (journalExists && !tablesExist)
            {
                suggestedAction = "Arquivo de journal detectado e schema incompleto. O índice pode estar corrompido. Recrie com 'mailvault index --force'.";
                errorMessage = "Journal SQLite detectado e schema incompleto.";
            }
            else if (journalExists)
            {
                suggestedAction = "Arquivo de journal SQLite detectado. O banco será aberto normalmente; o journal será recuperado automaticamente pelo SQLite.";
            }
            else if (!tablesExist)
            {
                errorMessage = "Banco de dados case.db encontrado mas schema incompleto. Recrie com 'mailvault index'.";
                suggestedAction = "Execute 'mailvault index' para recriar o índice.";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errorMessage = $"Erro ao ler case.db: {ex.Message}";
            suggestedAction = "O banco pode estar corrompido. Tente recriar com 'mailvault index --force'.";
        }

        return new CaseValidationResult
        {
            DirectoryExists = true,
            CaseDbExists = true,
            ManifestExists = manifestExists,
            AuditLogExists = auditExists,
            JournalFileExists = journalExists,
            CaseDbReadable = readable,
            SchemaVersion = schemaVersion,
            CaseInfoExists = caseInfoExists,
            TablesExist = tablesExist,
            ErrorMessage = errorMessage,
            SuggestedAction = suggestedAction
        };
    }
}

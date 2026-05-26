using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using Microsoft.Data.Sqlite;

namespace MailVault.Indexing;

public sealed class SqliteCaseIndexStore : ICaseIndexStore
{
    private SqliteConnection? _connection;
    private string? _dbPath;
    private string? _connString;

    public string ConnectionString => _connString ?? throw new InvalidOperationException("Store não inicializado.");
    public string DatabasePath => _dbPath ?? throw new InvalidOperationException("Store não inicializado.");

    public async Task InitializeAsync(string caseFolderPath, CancellationToken ct)
    {
        if (!Directory.Exists(caseFolderPath))
        {
            Directory.CreateDirectory(caseFolderPath);
        }

        _dbPath = Path.Combine(caseFolderPath, "case.db");
        _connString = $"Data Source={_dbPath};";

        _connection = new SqliteConnection(_connString);
        await _connection.OpenAsync(ct);

        // Run pragma for foreign keys on open
        using (var cmd = new SqliteCommand("PRAGMA foreign_keys = ON;", _connection))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Initialize schema v1
        IndexSchemaInitializer.Initialize(_connection);
    }

    public ICaseIndexWriter CreateWriter()
    {
        if (_connection == null) throw new InvalidOperationException("Store não inicializado.");
        return new SqliteCaseIndexWriter(_connection);
    }

    public ICaseIndexReader CreateReader()
    {
        if (_connection == null) throw new InvalidOperationException("Store não inicializado.");
        return new SqliteCaseIndexReader(_connection);
    }

    public void Dispose()
    {
        if (_connection != null)
        {
            if (_connection.State == System.Data.ConnectionState.Open)
            {
                _connection.Close();
            }
            _connection.Dispose();
            _connection = null;
        }
    }
}

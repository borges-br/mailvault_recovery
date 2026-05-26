using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailVault.Desktop.Services;

/// <summary>
/// A de-identified record of a recently opened case.
/// Does NOT store email bodies, attachments, hashes, or sensitive paths.
/// </summary>
public sealed record RecentCaseEntry
{
    [JsonPropertyName("caseId")]
    public string CaseId { get; init; } = "";

    [JsonPropertyName("caseFolderPath")]
    public string CaseFolderPath { get; init; } = "";

    [JsonPropertyName("openMode")]
    public string OpenMode { get; init; } = "";

    [JsonPropertyName("lastOpenedAt")]
    public DateTimeOffset LastOpenedAt { get; init; }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }
}

/// <summary>
/// Persists and loads a list of recently opened cases to/from ApplicationData.
/// Security: Only case folder paths, case IDs, schema version, and open mode are saved.
/// Email content, hashes, attachment data, and headers are never persisted here.
/// </summary>
public sealed class RecentCasesService
{
    private const int MaxRecentCases = 10;

    private readonly string _storageFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public RecentCasesService() : this(GetDefaultStoragePath()) { }

    public RecentCasesService(string storageFilePath)
    {
        _storageFilePath = storageFilePath;
    }

    private static string GetDefaultStoragePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "MailVault");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "recent-cases.json");
    }

    /// <summary>
    /// Loads the list of recent cases. Returns empty list on error.
    /// </summary>
    public List<RecentCaseEntry> Load()
    {
        try
        {
            if (!File.Exists(_storageFilePath))
                return new List<RecentCaseEntry>();

            string json = File.ReadAllText(_storageFilePath);
            var list = JsonSerializer.Deserialize<List<RecentCaseEntry>>(json, JsonOptions);
            return list ?? new List<RecentCaseEntry>();
        }
        catch
        {
            // Graceful degradation — do not crash if history file is corrupted
            return new List<RecentCaseEntry>();
        }
    }

    /// <summary>
    /// Adds or updates an entry for the given case and saves to disk.
    /// Keeps only the most recent MaxRecentCases entries.
    /// </summary>
    public void AddOrUpdate(RecentCaseEntry entry)
    {
        var list = Load();

        // Remove any existing entry for the same path
        list.RemoveAll(e => string.Equals(e.CaseFolderPath, entry.CaseFolderPath, StringComparison.OrdinalIgnoreCase));

        // Insert at the front (most recent first)
        list.Insert(0, entry);

        // Trim to max size
        if (list.Count > MaxRecentCases)
            list = list.Take(MaxRecentCases).ToList();

        Save(list);
    }

    /// <summary>
    /// Removes a specific case entry by folder path.
    /// </summary>
    public void Remove(string caseFolderPath)
    {
        var list = Load();
        list.RemoveAll(e => string.Equals(e.CaseFolderPath, caseFolderPath, StringComparison.OrdinalIgnoreCase));
        Save(list);
    }

    /// <summary>
    /// Clears all recent cases.
    /// </summary>
    public void Clear()
    {
        Save(new List<RecentCaseEntry>());
    }

    private void Save(List<RecentCaseEntry> list)
    {
        try
        {
            string dir = Path.GetDirectoryName(_storageFilePath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(list, JsonOptions);
            File.WriteAllText(_storageFilePath, json);
        }
        catch
        {
            // Graceful degradation — history is not mission-critical
        }
    }
}

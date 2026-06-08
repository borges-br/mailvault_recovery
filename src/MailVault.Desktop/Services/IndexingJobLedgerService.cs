using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailVault.Desktop.Services;

/// <summary>
/// Registro durável de um job de indexação. NÃO guarda corpo de e-mail, anexos,
/// hashes nem o caminho da evidência original — apenas o suficiente para retomar
/// a visibilidade de um caso caso o app seja fechado/trave durante a indexação.
/// O progresso real é sempre relido do case.db; estes contadores são só para exibição.
/// </summary>
public sealed record IndexingJobRecord
{
    [JsonPropertyName("jobId")] public string JobId { get; init; } = "";
    [JsonPropertyName("caseId")] public string CaseId { get; init; } = "";
    [JsonPropertyName("caseFolderPath")] public string CaseFolderPath { get; init; } = "";
    [JsonPropertyName("engine")] public string Engine { get; init; } = "";

    /// <summary>Running | Completed | Failed | Cancelled | Interrupted (e demais status do worker).</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = "Running";

    [JsonPropertyName("foldersIndexed")] public int FoldersIndexed { get; init; }
    [JsonPropertyName("messagesIndexed")] public int MessagesIndexed { get; init; }
    [JsonPropertyName("progressPercent")] public double ProgressPercent { get; init; }
    [JsonPropertyName("startedAt")] public DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }

    [JsonIgnore]
    public string DisplaySummary => $"{MessagesIndexed:N0} e-mails • {FoldersIndexed} pastas • motor {Engine}";
}

/// <summary>
/// Persiste e carrega um histórico de jobs de indexação em ApplicationData.
/// Segurança: mesmo posicionamento de RecentCasesService — só metadados do caso,
/// nunca conteúdo de e-mail, anexos ou hashes.
/// </summary>
public sealed class IndexingJobLedgerService
{
    private const int MaxJobs = 25;

    private readonly string _storageFilePath;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public IndexingJobLedgerService() : this(GetDefaultStoragePath()) { }

    public IndexingJobLedgerService(string storageFilePath)
    {
        _storageFilePath = storageFilePath;
    }

    private static string GetDefaultStoragePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "MailVault");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "indexing-jobs.json");
    }

    public List<IndexingJobRecord> Load()
    {
        lock (_gate) return LoadNoLock();
    }

    public void Upsert(IndexingJobRecord record)
    {
        lock (_gate)
        {
            var list = LoadNoLock();
            list.RemoveAll(j => string.Equals(j.JobId, record.JobId, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, record);
            SaveNoLock(list);
        }
    }

    public void Remove(string jobId)
    {
        lock (_gate)
        {
            var list = LoadNoLock();
            list.RemoveAll(j => string.Equals(j.JobId, jobId, StringComparison.OrdinalIgnoreCase));
            SaveNoLock(list);
        }
    }

    public void RemoveByCaseFolder(string caseFolderPath)
    {
        if (string.IsNullOrWhiteSpace(caseFolderPath)) return;
        lock (_gate)
        {
            var list = LoadNoLock();
            int removed = list.RemoveAll(j => string.Equals(j.CaseFolderPath, caseFolderPath, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) SaveNoLock(list);
        }
    }

    public void Clear()
    {
        lock (_gate) SaveNoLock(new List<IndexingJobRecord>());
    }

    private List<IndexingJobRecord> LoadNoLock()
    {
        try
        {
            if (!File.Exists(_storageFilePath)) return new List<IndexingJobRecord>();
            string json = File.ReadAllText(_storageFilePath);
            return JsonSerializer.Deserialize<List<IndexingJobRecord>>(json, JsonOptions) ?? new List<IndexingJobRecord>();
        }
        catch
        {
            return new List<IndexingJobRecord>();
        }
    }

    private void SaveNoLock(List<IndexingJobRecord> list)
    {
        try
        {
            var trimmed = list
                .OrderByDescending(j => j.UpdatedAt)
                .Take(MaxJobs)
                .ToList();
            string dir = Path.GetDirectoryName(_storageFilePath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(trimmed, JsonOptions);
            File.WriteAllText(_storageFilePath, json);
        }
        catch
        {
            // Graceful degradation — o ledger não é mission-critical.
        }
    }
}

using System;
using System.IO;
using System.Linq;
using MailVault.Desktop.Services;
using Xunit;

namespace MailVault.Desktop.Tests;

public class IndexingJobLedgerServiceTests : IDisposable
{
    private readonly string _tempFile;

    public IndexingJobLedgerServiceTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"mv-ledger-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { /* best-effort */ }
    }

    private static IndexingJobRecord Job(string jobId, string caseId, string folder, string status = "Running")
        => new()
        {
            JobId = jobId,
            CaseId = caseId,
            CaseFolderPath = folder,
            Engine = "XstReader",
            Status = status,
            MessagesIndexed = 100,
            FoldersIndexed = 5,
            StartedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };

    [Fact]
    public void Load_OnMissingFile_ReturnsEmpty()
    {
        var ledger = new IndexingJobLedgerService(_tempFile);
        Assert.Empty(ledger.Load());
    }

    [Fact]
    public void Upsert_ThenLoad_RoundTrips()
    {
        var ledger = new IndexingJobLedgerService(_tempFile);
        ledger.Upsert(Job("j1", "CASE-1", @"C:\cases\CASE-1"));

        var loaded = ledger.Load();
        Assert.Single(loaded);
        Assert.Equal("CASE-1", loaded[0].CaseId);
        Assert.Equal("Running", loaded[0].Status);
    }

    [Fact]
    public void Upsert_WithSameJobId_ReplacesInsteadOfDuplicating()
    {
        var ledger = new IndexingJobLedgerService(_tempFile);
        ledger.Upsert(Job("j1", "CASE-1", @"C:\cases\CASE-1", "Running"));
        ledger.Upsert(Job("j1", "CASE-1", @"C:\cases\CASE-1", "Completed"));

        var loaded = ledger.Load();
        Assert.Single(loaded);
        Assert.Equal("Completed", loaded[0].Status);
    }

    [Fact]
    public void Remove_ByJobId_DropsOnlyThatRecord()
    {
        var ledger = new IndexingJobLedgerService(_tempFile);
        ledger.Upsert(Job("j1", "CASE-1", @"C:\cases\CASE-1"));
        ledger.Upsert(Job("j2", "CASE-2", @"C:\cases\CASE-2"));

        ledger.Remove("j1");

        var loaded = ledger.Load();
        Assert.Single(loaded);
        Assert.Equal("j2", loaded[0].JobId);
    }

    [Fact]
    public void RemoveByCaseFolder_IsCaseInsensitive()
    {
        var ledger = new IndexingJobLedgerService(_tempFile);
        ledger.Upsert(Job("j1", "CASE-1", @"C:\cases\CASE-1"));

        ledger.RemoveByCaseFolder(@"c:\CASES\case-1");

        Assert.Empty(ledger.Load());
    }

    [Fact]
    public void Save_TrimsToMaxJobs_KeepingMostRecent()
    {
        var ledger = new IndexingJobLedgerService(_tempFile);
        for (int i = 0; i < 30; i++)
        {
            ledger.Upsert(new IndexingJobRecord
            {
                JobId = $"j{i}",
                CaseId = $"CASE-{i}",
                CaseFolderPath = $@"C:\cases\CASE-{i}",
                Status = "Completed",
                UpdatedAt = DateTimeOffset.Now.AddMinutes(i)
            });
        }

        var loaded = ledger.Load();
        Assert.Equal(25, loaded.Count);
        // O mais recente (maior UpdatedAt) deve sobreviver; o mais antigo, não.
        Assert.Contains(loaded, j => j.JobId == "j29");
        Assert.DoesNotContain(loaded, j => j.JobId == "j0");
    }

    [Fact]
    public void Load_OnCorruptedFile_ReturnsEmpty()
    {
        File.WriteAllText(_tempFile, "{ this is not valid json ][");
        var ledger = new IndexingJobLedgerService(_tempFile);
        Assert.Empty(ledger.Load());
    }
}

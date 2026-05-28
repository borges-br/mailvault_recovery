using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Indexing;
using Xunit;

namespace MailVault.Indexing.Tests;

public class RecoveryEnginesResilienceTests : IDisposable
{
    private readonly string _tempDir;

    public RecoveryEnginesResilienceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mailvault-resilience-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    // -------------------------------------------------------------------------
    // ThunderbirdMboxEngine — malformed entry skips and continues
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ThunderbirdMboxEngine_MalformedEntry_SkipsAndContinues()
    {
        // Arrange: write a .mbox file with one valid message followed by garbage
        var mboxPath = Path.Combine(_tempDir, "test.mbox");
        var mboxContent =
            "From test@example.com Mon Jan  1 00:00:00 2024\r\n" +
            "Subject: Valid\r\n" +
            "From: test@example.com\r\n" +
            "\r\n" +
            "Body text\r\n" +
            "\r\n" +
            "XXXXXXXXXGARBAGEXXXXXXXXX\n\nNOT A MESSAGE\n";

        await File.WriteAllTextAsync(mboxPath, mboxContent);

        var caseDir = Path.Combine(_tempDir, "case-tb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(caseDir);

        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDir, CancellationToken.None);

        var engine = new ThunderbirdMboxEngine();

        // Act — must not throw
        var result = await engine.IndexAsync(
            mboxPath,
            store,
            caseId: "CASE-TB-RESILIENCE",
            operatorName: "Tester",
            cachePreview: false,
            limit: null,
            progress: null,
            ct: CancellationToken.None);

        // Assert
        Assert.True(result.MessagesIndexed >= 1, $"Expected at least 1 indexed message, got {result.MessagesIndexed}");
    }

    // -------------------------------------------------------------------------
    // EmlFolderEngine — malformed EML skips and continues
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EmlFolderEngine_MalformedEml_SkipsAndContinues()
    {
        // Arrange: create a dir with one valid EML and one garbage file
        var emlDir = Path.Combine(_tempDir, "emls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emlDir);

        await File.WriteAllTextAsync(
            Path.Combine(emlDir, "valid.eml"),
            "From: a@b.com\r\nTo: c@d.com\r\nSubject: Hi\r\n\r\nBody\r\n");

        await File.WriteAllTextAsync(
            Path.Combine(emlDir, "bad.eml"),
            "GARBAGE CONTENT NOT AN EMAIL");

        var caseDir = Path.Combine(_tempDir, "case-eml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(caseDir);

        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDir, CancellationToken.None);

        var engine = new EmlFolderEngine();

        // Act — must not throw
        var result = await engine.IndexAsync(
            emlDir,
            store,
            caseId: "CASE-EML-RESILIENCE",
            operatorName: "Tester",
            cachePreview: false,
            limit: null,
            progress: null,
            ct: CancellationToken.None);

        // Assert
        Assert.True(result.MessagesIndexed >= 1, $"Expected at least 1 indexed message, got {result.MessagesIndexed}");
    }
}

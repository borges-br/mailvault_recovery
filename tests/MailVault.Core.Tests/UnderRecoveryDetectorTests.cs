using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using Xunit;

namespace MailVault.Core.Tests;

// Valida o detector de sub-recuperação silenciosa sem depender de MailVault.Exporters.Eml
// (usa um exporter no-op), para rodar mesmo quando o Smart App Control bloqueia outras DLLs.
public class UnderRecoveryDetectorTests : IDisposable
{
    private readonly string _tempDir;

    public UnderRecoveryDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mv-underrec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task UnderRecovery_FlaggedWhenCoverageLow_OnUnboundedRun()
    {
        var runner = new RecoveryExportRunner();
        var result = await runner.ExportToEmlAsync(
            new UnderReportingReader(claimed: 10, yielded: 2), new NoopExporter(), "fake.ost",
            Path.Combine(_tempDir, "low"), targetFolderPath: null, messageIds: null,
            progress: null, ct: CancellationToken.None);

        Assert.Equal(2, result.ExportedMessages);
        Assert.Equal(RecoveryExportStatus.PartialCompleted, result.Status);
        Assert.Contains(result.Errors, e => e.ErrorCode == "MV-WARN-REC-UNDER-RECOVERY");
        Assert.Equal(10, result.Metrics!.ExpectedMessages);
        Assert.True(result.Metrics!.CoveragePercent < 90.0);
    }

    [Fact]
    public async Task UnderRecovery_NotFlagged_WhenCoverageFull()
    {
        var runner = new RecoveryExportRunner();
        var result = await runner.ExportToEmlAsync(
            new UnderReportingReader(claimed: 3, yielded: 3), new NoopExporter(), "fake.ost",
            Path.Combine(_tempDir, "full"), targetFolderPath: null, messageIds: null,
            progress: null, ct: CancellationToken.None);

        Assert.Equal(3, result.ExportedMessages);
        Assert.Equal(RecoveryExportStatus.Completed, result.Status);
        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "MV-WARN-REC-UNDER-RECOVERY");
        Assert.Equal(100.0, result.Metrics!.CoveragePercent);
    }

    [Fact]
    public async Task UnderRecovery_NotFlagged_WhenBoundedByMaxMessages()
    {
        var runner = new RecoveryExportRunner();
        var result = await runner.ExportToEmlAsync(
            new UnderReportingReader(claimed: 10, yielded: 5), new NoopExporter(), "fake.ost",
            Path.Combine(_tempDir, "bounded"), targetFolderPath: null, messageIds: null,
            progress: null, ct: CancellationToken.None,
            options: new RecoveryExportOptions(MaxMessages: 2));

        // Limite de escopo NÃO deve disparar falso-positivo de sub-recuperação.
        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "MV-WARN-REC-UNDER-RECOVERY");
    }

    private sealed class NoopExporter : IMessageExporter
    {
        public string FormatName => "NOOP";
        public Task ExportMessageAsync(MailItem message, IAttachmentContentProvider attachmentProvider, Stream outputStream, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class UnderReportingReader : IMailStoreReader
    {
        private readonly int _claimed;
        private readonly int _yielded;
        public UnderReportingReader(int claimed, int yielded) { _claimed = claimed; _yielded = yielded; }

        public string ReaderName => "UnderReporting";

        private FolderNode Folder => new FolderNode(
            new FolderId("Box"), null, "Box", "Box", _claimed, new List<FolderNode>());

        private static MailItem Msg(int i) => new MailItem(
            $"m-{i}", $"<m{i}@x>", $"M{i}", new MailAddressRef("S", "s@x"),
            new List<MailAddressRef>(), new List<MailAddressRef>(), new List<MailAddressRef>(),
            DateTimeOffset.UtcNow, null, "body", null, new List<AttachmentRef>(),
            new Dictionary<string, string>(), new List<ExtractionIssue>());

        public Task<StoreMetadata> InspectAsync(string filePath, CancellationToken ct) =>
            Task.FromResult(new StoreMetadata(filePath, 0, "", "Fake", ReaderName, new List<ExtractionIssue>()));

        public async IAsyncEnumerable<FolderNode> EnumerateFoldersAsync([EnumeratorCancellation] CancellationToken ct)
        { yield return Folder; await Task.CompletedTask; }

        public async IAsyncEnumerable<MailItem> EnumerateMessagesAsync(FolderId folderId, [EnumeratorCancellation] CancellationToken ct)
        {
            for (int i = 1; i <= _yielded; i++) yield return Msg(i);
            await Task.CompletedTask;
        }

        public Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct) =>
            Task.FromResult(OperationResult<MailItem>.Ok(Msg(0)));

        public Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct) => Task.FromResult<Stream>(Stream.Null);
        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct) => Task.FromResult<Stream>(Stream.Null);
    }
}

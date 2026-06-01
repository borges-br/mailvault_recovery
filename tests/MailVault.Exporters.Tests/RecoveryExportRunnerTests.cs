using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Exporters.Eml;
using MailVault.Exporters.Mbox;
using Xunit;

namespace MailVault.Exporters.Tests;

public class RecoveryExportRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public RecoveryExportRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mailvault-runner-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task RecoveryExportRunner_ExportAllToEml_CreatesFilesAndReport()
    {
        var runner = new RecoveryExportRunner();
        var outputDir = Path.Combine(_tempDir, "eml-all");

        var result = await runner.ExportToEmlAsync(
            new FakeMailStoreReader(), new EmlExporter(), "fake.pst", outputDir,
            targetFolderPath: null, messageIds: null,
            progress: null, ct: CancellationToken.None);

        Assert.Equal(6, result.ExportedMessages);
        Assert.Equal(0, result.FailedMessages);
        Assert.Equal(3, result.TotalFolders);
        Assert.True(File.Exists(Path.Combine(outputDir, "_mailvault-export-report.json")));
        Assert.True(File.Exists(Path.Combine(outputDir, "_mailvault-export-errors.csv")));

        var mdPath = Path.Combine(outputDir, "_mailvault-export-report.md");
        Assert.True(File.Exists(mdPath));
        var md = File.ReadAllText(mdPath);
        Assert.Contains("# Relatório de Recuperação", md);
        Assert.Contains("Completo", md); // 6 exportadas, 0 falhas

        var emlFiles = Directory.GetFiles(outputDir, "*.eml", SearchOption.AllDirectories);
        Assert.Equal(6, emlFiles.Length);
    }

    [Fact]
    public async Task RecoveryExportRunner_AttachmentFail_DoesNotAbortMessage()
    {
        var runner = new RecoveryExportRunner();
        var outputDir = Path.Combine(_tempDir, "eml-att-fail");

        var result = await runner.ExportToEmlAsync(
            new FailingAttachmentReader(new FakeMailStoreReader()), new EmlExporter(), "fake.pst", outputDir,
            targetFolderPath: null, messageIds: null,
            progress: null, ct: CancellationToken.None);

        // All 6 messages must be exported even though 3 inbox attachments fail
        Assert.Equal(6, result.ExportedMessages);
        Assert.Equal(0, result.FailedMessages);
        Assert.Equal(3, result.FailedAttachments);
        Assert.Contains(result.Errors, e => e.ErrorCode == "MV-WARN-REC-ATTACH");

        var emlFiles = Directory.GetFiles(outputDir, "*.eml", SearchOption.AllDirectories);
        Assert.Equal(6, emlFiles.Length);
    }

    [Fact]
    public async Task RecoveryExportRunner_FolderExport_OnlyExportsTargetFolder()
    {
        var runner = new RecoveryExportRunner();
        var outputDir = Path.Combine(_tempDir, "eml-folder");

        var result = await runner.ExportToEmlAsync(
            new FakeMailStoreReader(), new EmlExporter(), "fake.pst", outputDir,
            targetFolderPath: "Sent Items", messageIds: null,
            progress: null, ct: CancellationToken.None);

        Assert.Equal(1, result.TotalFolders);
        Assert.Equal(1, result.ExportedMessages);
        Assert.Equal(0, result.FailedMessages);

        var emlFiles = Directory.GetFiles(outputDir, "*.eml", SearchOption.AllDirectories);
        Assert.Single(emlFiles);
    }

    [Fact]
    public async Task RecoveryExportRunner_ExportMbox_EscapesFromLines()
    {
        var runner = new RecoveryExportRunner();
        var outputDir = Path.Combine(_tempDir, "mbox-escape");

        await runner.ExportToMboxAsync(
            new FromLineFakeReader(), new MboxExporter(new EmlExporter()), "fake.pst", outputDir,
            targetFolderPath: null, progress: null, ct: CancellationToken.None);

        var mboxFiles = Directory.GetFiles(outputDir, "*.mbox", SearchOption.AllDirectories);
        Assert.True(mboxFiles.Length >= 1);

        bool hasEnvelope = false;
        bool hasEscapedFromLine = false;

        foreach (var f in mboxFiles)
        {
            string content = await File.ReadAllTextAsync(f);
            if (content.Contains("\nFrom ") || content.StartsWith("From "))
                hasEnvelope = true;
            if (content.Contains("\n>From ") || content.Contains("\n>>From "))
                hasEscapedFromLine = true;
        }

        Assert.True(hasEnvelope, "MBOX file must contain 'From ' envelope lines");
        Assert.True(hasEscapedFromLine, "MBOX file must escape body lines starting with 'From '");
    }

    [Fact]
    public async Task RecoveryExportRunner_CancelExport_ReturnsCancelledStatus_AndReport()
    {
        // Comportamento do Milestone 1.5: cancelamento NÃO lança — finaliza com status CancelledByUser
        // e gera relatório (não perde o que foi feito).
        var runner = new RecoveryExportRunner();
        var outputDir = Path.Combine(_tempDir, "eml-cancel");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await runner.ExportToEmlAsync(
            new FakeMailStoreReader(), new EmlExporter(), "fake.pst", outputDir,
            targetFolderPath: null, messageIds: null,
            progress: null, ct: cts.Token);

        Assert.Equal(RecoveryExportStatus.CancelledByUser, result.Status);
        Assert.True(File.Exists(Path.Combine(outputDir, "_mailvault-export-report.json")));
    }

    [Fact]
    public async Task RecoveryExportRunner_MaxMessages_LimitsAndReportsPartial()
    {
        var runner = new RecoveryExportRunner();
        var outputDir = Path.Combine(_tempDir, "eml-maxmsg");

        var result = await runner.ExportToEmlAsync(
            new FakeMailStoreReader(), new EmlExporter(), "fake.pst", outputDir,
            targetFolderPath: null, messageIds: null,
            progress: null, ct: CancellationToken.None,
            options: new RecoveryExportOptions(MaxMessages: 2));

        Assert.Equal(2, result.ExportedMessages);
        Assert.Equal(RecoveryExportStatus.PartialCompleted, result.Status);
        Assert.NotNull(result.Metrics);
    }

    [Fact]
    public async Task RecoveryExportRunner_ProgressReportsExportCounts()
    {
        var runner = new RecoveryExportRunner();
        var outputDir = Path.Combine(_tempDir, "eml-progress");

        var reports = new List<RecoveryExportProgress>();
        var progress = new Progress<RecoveryExportProgress>(p =>
        {
            lock (reports) reports.Add(p);
        });

        var result = await runner.ExportToEmlAsync(
            new FakeMailStoreReader(), new EmlExporter(), "fake.pst", outputDir,
            targetFolderPath: null, messageIds: null,
            progress: progress, ct: CancellationToken.None);

        // Give the Progress<T> callbacks a chance to fire (they post to SynchronizationContext)
        await Task.Delay(50);

        Assert.True(reports.Count > 0, "Expected at least one progress report");
        Assert.Equal(6, result.ExportedMessages);
    }

    // Wraps FakeMailStoreReader and throws on every attachment stream request
    private sealed class FailingAttachmentReader : IMailStoreReader
    {
        private readonly FakeMailStoreReader _inner;

        public FailingAttachmentReader(FakeMailStoreReader inner) => _inner = inner;

        public string ReaderName => _inner.ReaderName;

        public Task<StoreMetadata> InspectAsync(string filePath, CancellationToken ct) =>
            _inner.InspectAsync(filePath, ct);

        public IAsyncEnumerable<FolderNode> EnumerateFoldersAsync(CancellationToken ct) =>
            _inner.EnumerateFoldersAsync(ct);

        public IAsyncEnumerable<MailItem> EnumerateMessagesAsync(FolderId folderId, CancellationToken ct) =>
            _inner.EnumerateMessagesAsync(folderId, ct);

        public Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct) =>
            _inner.GetMessageAsync(messageId, ct);

        public Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct) =>
            _inner.OpenAttachmentAsync(attachment, ct);

        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct) =>
            throw new InvalidOperationException("Simulated attachment failure.");
    }

    // Single-folder reader with one message whose body contains "From " at a line start
    private sealed class FromLineFakeReader : IMailStoreReader
    {
        public string ReaderName => "FromLine Fake Reader";

        private readonly FolderNode _folder = new FolderNode(
            Id: new FolderId("TestFolder"),
            ParentId: null,
            DisplayName: "TestFolder",
            FullPath: "TestFolder",
            MessageCount: 1,
            Children: new List<FolderNode>());

        private readonly MailItem _message = new MailItem(
            InternalId: "msg-fromline-1",
            InternetMessageId: "<fromline@test.local>",
            Subject: "Message with From line in body",
            From: new MailAddressRef("Sender", "sender@test.local"),
            To: new List<MailAddressRef> { new MailAddressRef("Receiver", "recv@test.local") },
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.Parse("2026-05-27T10:00:00Z"),
            ReceivedAt: DateTimeOffset.Parse("2026-05-27T10:01:00Z"),
            PlainTextBody: "First line of message.\r\nFrom boss@example.com: here is the report\r\nLast line.",
            HtmlBody: null,
            Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>());

        public Task<StoreMetadata> InspectAsync(string filePath, CancellationToken ct) =>
            Task.FromResult(new StoreMetadata(filePath, 0, string.Empty, "Fake", ReaderName, new List<ExtractionIssue>()));

        public async IAsyncEnumerable<FolderNode> EnumerateFoldersAsync([EnumeratorCancellation] CancellationToken ct)
        {
            yield return _folder;
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<MailItem> EnumerateMessagesAsync(FolderId folderId, [EnumeratorCancellation] CancellationToken ct)
        {
            if (folderId.Value == _folder.Id.Value)
                yield return _message;
            await Task.CompletedTask;
        }

        public Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct) =>
            Task.FromResult(OperationResult<MailItem>.Ok(_message));

        public Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct) =>
            Task.FromResult<Stream>(Stream.Null);

        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct) =>
            Task.FromResult<Stream>(Stream.Null);
    }

    // Pasta reporta ContentCount=10 mas só enumera 2 mensagens (simula perda silenciosa por corrupção).
    private sealed class UnderReportingReader : IMailStoreReader
    {
        public string ReaderName => "UnderReporting Fake Reader";

        private readonly FolderNode _folder = new FolderNode(
            Id: new FolderId("Box"), ParentId: null, DisplayName: "Box", FullPath: "Box",
            MessageCount: 10, Children: new List<FolderNode>());

        private static MailItem Msg(int i) => new MailItem(
            InternalId: $"u-{i}", InternetMessageId: $"<u{i}@x>", Subject: $"U{i}",
            From: new MailAddressRef("S", "s@x"), To: new List<MailAddressRef>(), Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(), SentAt: DateTimeOffset.UtcNow, ReceivedAt: null,
            PlainTextBody: "body", HtmlBody: null, Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string>(), Issues: new List<ExtractionIssue>());

        public Task<StoreMetadata> InspectAsync(string filePath, CancellationToken ct) =>
            Task.FromResult(new StoreMetadata(filePath, 0, string.Empty, "Fake", ReaderName, new List<ExtractionIssue>()));

        public async IAsyncEnumerable<FolderNode> EnumerateFoldersAsync([EnumeratorCancellation] CancellationToken ct)
        { yield return _folder; await Task.CompletedTask; }

        public async IAsyncEnumerable<MailItem> EnumerateMessagesAsync(FolderId folderId, [EnumeratorCancellation] CancellationToken ct)
        { yield return Msg(1); yield return Msg(2); await Task.CompletedTask; }

        public Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct) =>
            Task.FromResult(OperationResult<MailItem>.Ok(Msg(0)));

        public Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct) => Task.FromResult<Stream>(Stream.Null);
        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct) => Task.FromResult<Stream>(Stream.Null);
    }

    [Fact]
    public async Task RecoveryExportRunner_UnderRecovery_FlaggedOnUnboundedRun()
    {
        var runner = new RecoveryExportRunner();
        var outputDir = Path.Combine(_tempDir, "eml-underrecovery");

        var result = await runner.ExportToEmlAsync(
            new UnderReportingReader(), new EmlExporter(), "fake.pst", outputDir,
            targetFolderPath: null, messageIds: null, progress: null, ct: CancellationToken.None);

        Assert.Equal(2, result.ExportedMessages);
        Assert.Equal(RecoveryExportStatus.PartialCompleted, result.Status);
        Assert.Contains(result.Errors, e => e.ErrorCode == "MV-WARN-REC-UNDER-RECOVERY");
        Assert.NotNull(result.Metrics);
        Assert.Equal(10, result.Metrics!.ExpectedMessages);
        Assert.True(result.Metrics!.CoveragePercent < 90.0);
    }
}

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
using MimeKit;
using Xunit;

namespace MailVault.Exporters.Tests;

public class WriterTests : IDisposable
{
    private readonly string _tempDir;

    public WriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mailvault-writer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static MailItem MakeSimpleMailItem(string id, string subject, string body,
        string fromAddr = "sender@test.local")
    {
        return new MailItem(
            InternalId: id,
            InternetMessageId: $"<{id}@test.local>",
            Subject: subject,
            From: new MailAddressRef("Test Sender", fromAddr),
            To: new List<MailAddressRef> { new MailAddressRef("Receiver", "recv@test.local") },
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.Parse("2026-05-27T10:00:00Z"),
            ReceivedAt: DateTimeOffset.Parse("2026-05-27T10:01:00Z"),
            PlainTextBody: body,
            HtmlBody: null,
            Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        );
    }

    private static IAttachmentContentProvider NullProvider() => new NullAttachmentContentProvider();

    // -------------------------------------------------------------------------
    // MboxWriter_AppendsMultipleMessages
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MboxWriter_AppendsMultipleMessages()
    {
        var exporter = new MboxExporter(new EmlExporter());
        var provider = NullProvider();
        var ct = CancellationToken.None;

        var msg1 = MakeSimpleMailItem("m1", "Message One", "Hello World");
        var msg2 = MakeSimpleMailItem("m2", "Message Two", "Hello World");

        using var ms = new MemoryStream();

        await exporter.ExportMessageAsync(msg1, provider, ms, ct);
        await exporter.ExportMessageAsync(msg2, provider, ms, ct);

        ms.Position = 0;
        string content = new StreamReader(ms, Encoding.UTF8).ReadToEnd();

        // Count lines starting with "From " — should be exactly 2 (one per message)
        var fromLines = content
            .Split('\n')
            .Where(l => l.StartsWith("From ", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, fromLines.Count);
    }

    // -------------------------------------------------------------------------
    // MboxWriter_EscapesFromLines
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MboxWriter_EscapesFromLines()
    {
        var exporter = new MboxExporter(new EmlExporter());
        var provider = NullProvider();
        var ct = CancellationToken.None;

        var msg = MakeSimpleMailItem(
            "m-escape",
            "Escape test",
            "First line.\r\nFrom boss@example.com: report\r\nLast line.");

        using var ms = new MemoryStream();
        await exporter.ExportMessageAsync(msg, provider, ms, ct);

        ms.Position = 0;
        string content = new StreamReader(ms, Encoding.UTF8).ReadToEnd();

        Assert.DoesNotContain("\nFrom boss", content);
        Assert.Contains("\n>From boss", content);
    }

    // -------------------------------------------------------------------------
    // EmlExporter_CreatesValidMimeHeaders
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EmlExporter_CreatesValidMimeHeaders()
    {
        var exporter = new EmlExporter();
        var provider = NullProvider();
        var ct = CancellationToken.None;

        var msg = new MailItem(
            InternalId: "m-mime",
            InternetMessageId: "<m-mime@test.local>",
            Subject: "Test Mail",
            From: new MailAddressRef("Sender", "sender@test.com"),
            To: new List<MailAddressRef> { new MailAddressRef("Receiver", "recv@test.com") },
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.Parse("2026-05-27T10:00:00Z"),
            ReceivedAt: DateTimeOffset.Parse("2026-05-27T10:01:00Z"),
            PlainTextBody: "Hello",
            HtmlBody: null,
            Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        );

        using var ms = new MemoryStream();
        await exporter.ExportMessageAsync(msg, provider, ms, ct);
        ms.Position = 0;

        var mimeMsg = await MimeMessage.LoadAsync(ms, ct);

        Assert.Equal("Test Mail", mimeMsg.Subject);
        Assert.True(mimeMsg.From.Count > 0);
    }

    // -------------------------------------------------------------------------
    // EmlExporter_AtomicWrite_NoTmpLeftOnSuccess
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EmlExporter_AtomicWrite_NoTmpLeftOnSuccess()
    {
        var outputDir = Path.Combine(_tempDir, "eml-atomic");
        var runner = new RecoveryExportRunner();

        var result = await runner.ExportToEmlAsync(
            new WriterTestsFakeMailStoreReader(),
            new EmlExporter(),
            "fake.pst",
            outputDir,
            targetFolderPath: null,
            messageIds: null,
            progress: null,
            ct: CancellationToken.None);

        Assert.True(result.ExportedMessages >= 1);

        var tmpFiles = Directory.GetFiles(outputDir, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(tmpFiles);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private sealed class NullAttachmentContentProvider : IAttachmentContentProvider
    {
        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct)
            => Task.FromResult<Stream>(Stream.Null);
    }

    /// <summary>
    /// Minimal fake reader returning 2 messages with body "Hello World".
    /// Kept private here to avoid polluting the shared FakeMailStoreReader.
    /// </summary>
    private sealed class WriterTestsFakeMailStoreReader : IMailStoreReader
    {
        public string ReaderName => "WriterTests Fake Reader";

        private readonly FolderNode _folder = new FolderNode(
            Id: new FolderId("Inbox"),
            ParentId: null,
            DisplayName: "Inbox",
            FullPath: "Inbox",
            MessageCount: 2,
            Children: new List<FolderNode>());

        private readonly List<MailItem> _messages = new List<MailItem>
        {
            new MailItem(
                InternalId: "wt-msg-1",
                InternetMessageId: "<wt-1@fake.local>",
                Subject: "WriterTest Message 1",
                From: new MailAddressRef("Sender", "sender@fake.local"),
                To: new List<MailAddressRef> { new MailAddressRef("Recv", "recv@fake.local") },
                Cc: new List<MailAddressRef>(),
                Bcc: new List<MailAddressRef>(),
                SentAt: DateTimeOffset.Parse("2026-05-27T10:00:00Z"),
                ReceivedAt: DateTimeOffset.Parse("2026-05-27T10:01:00Z"),
                PlainTextBody: "Hello World",
                HtmlBody: null,
                Attachments: new List<AttachmentRef>(),
                RawProperties: new Dictionary<string, string>(),
                Issues: new List<ExtractionIssue>()),
            new MailItem(
                InternalId: "wt-msg-2",
                InternetMessageId: "<wt-2@fake.local>",
                Subject: "WriterTest Message 2",
                From: new MailAddressRef("Sender", "sender@fake.local"),
                To: new List<MailAddressRef> { new MailAddressRef("Recv", "recv@fake.local") },
                Cc: new List<MailAddressRef>(),
                Bcc: new List<MailAddressRef>(),
                SentAt: DateTimeOffset.Parse("2026-05-27T11:00:00Z"),
                ReceivedAt: DateTimeOffset.Parse("2026-05-27T11:01:00Z"),
                PlainTextBody: "Hello World",
                HtmlBody: null,
                Attachments: new List<AttachmentRef>(),
                RawProperties: new Dictionary<string, string>(),
                Issues: new List<ExtractionIssue>())
        };

        public Task<StoreMetadata> InspectAsync(string filePath, CancellationToken ct) =>
            Task.FromResult(new StoreMetadata(filePath, 0, string.Empty, "Fake", ReaderName, new List<ExtractionIssue>()));

        public async IAsyncEnumerable<FolderNode> EnumerateFoldersAsync([EnumeratorCancellation] CancellationToken ct)
        {
            yield return _folder;
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<MailItem> EnumerateMessagesAsync(FolderId folderId, [EnumeratorCancellation] CancellationToken ct)
        {
            if (folderId.Value == "Inbox")
            {
                foreach (var m in _messages)
                    yield return m;
            }
            await Task.CompletedTask;
        }

        public Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct) =>
            Task.FromResult(OperationResult<MailItem>.Ok(_messages[0]));

        public Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct) =>
            Task.FromResult<Stream>(Stream.Null);

        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct) =>
            Task.FromResult<Stream>(Stream.Null);
    }
}

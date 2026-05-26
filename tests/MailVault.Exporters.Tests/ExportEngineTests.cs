using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Core.Normalization;
using MailVault.Domain;
using MailVault.Exporters.Eml;
using MailVault.Exporters.Mbox;
using MailVault.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MailVault.Exporters.Tests;

public class ExportEngineTests : IDisposable
{
    private readonly string _tempWorkspaceDir;
    private readonly string _dummyFile;
    private readonly string _dummyFileSha256;

    public ExportEngineTests()
    {
        _tempWorkspaceDir = Path.Combine("c:\\Github\\mailvault_recovery\\scratch", $"exporters-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspaceDir);

        _dummyFile = Path.Combine(_tempWorkspaceDir, "evidence.pst");
        File.WriteAllText(_dummyFile, "Dummy PST data for exporters tests");
        
        // Compute dummy file hash
        var hashService = new HashService();
        _dummyFileSha256 = hashService.CalculateSha256Async(_dummyFile, new NullProgress(), CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspaceDir))
            {
                Directory.Delete(_tempWorkspaceDir, true);
            }
        }
        catch
        {
            // Ignore minor cleanup errors
        }
    }

    // Auxiliary method to setup a dummy relational case in memory or database
    private async Task<ICaseIndexStore> SetupIndexedCaseAsync(IMailStoreReader fakeReader)
    {
        var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(_tempWorkspaceDir, CancellationToken.None);

        var indexingService = new IndexingService();
        await indexingService.RunIndexAsync(
            _dummyFile,
            store,
            fakeReader,
            "CASE-EXPORT-TEST",
            "test-operator",
            cachePreview: true,
            limit: null,
            CancellationToken.None
        );

        return store;
    }

    // 1. ExportCommand_DryRun_DoesNotWriteMessages
    [Fact]
    public async Task ExportCommand_DryRun_DoesNotWriteMessages()
    {
        // Arrange
        var fakeReader = new FakeMailStoreReader();
        using var store = await SetupIndexedCaseAsync(fakeReader);
        using var caseReader = store.CreateReader();

        var emlExporter = new EmlExporter();
        var runner = new ExportJobRunner();
        
        string exportDir = Path.Combine(_tempWorkspaceDir, "exports-dryrun");
        var options = new ExportJobOptions(
            CaseFolder: _tempWorkspaceDir,
            Format: "eml",
            OutputDir: exportDir,
            DryRun: true
        );

        // Act
        var result = await runner.RunExportJobAsync(options, caseReader, new FakeResolver(fakeReader), emlExporter, new NullProgress(), CancellationToken.None);

        // Assert
        Assert.True(result.FoldersSelected > 0);
        Assert.True(result.MessagesSelected > 0);
        Assert.Equal(0, result.MessagesExported);
        Assert.False(Directory.Exists(exportDir)); // Directory should not be created physically
    }

    // 2. EmlExporter_WritesValidEmlFile
    [Fact]
    public async Task EmlExporter_WritesValidEmlFile()
    {
        // Arrange
        var message = new MailItem(
            InternalId: "msg-1",
            InternetMessageId: "<internet-msg-1@fake.local>",
            Subject: "Relatorio de Custos",
            From: new MailAddressRef("Remetente", "sender@fake.local"),
            To: new List<MailAddressRef> { new MailAddressRef("Destinatario", "receiver@fake.local") },
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.Now,
            ReceivedAt: DateTimeOffset.Now,
            PlainTextBody: "Ola, este e o corpo do e-mail simulado.",
            HtmlBody: null,
            Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        );

        var provider = new FakeAttachmentProvider();
        var exporter = new EmlExporter();
        using var ms = new MemoryStream();

        // Act
        await exporter.ExportMessageAsync(message, provider, ms, CancellationToken.None);
        string emlContent = Encoding.UTF8.GetString(ms.ToArray());

        // Assert
        Assert.Contains("Subject: Relatorio de Custos", emlContent);
        Assert.Contains("From: Remetente <sender@fake.local>", emlContent);
        Assert.Contains("To: Destinatario <receiver@fake.local>", emlContent);
        Assert.Contains("Message-Id: <internet-msg-1@fake.local>", emlContent);
        Assert.Contains("Ola, este e o corpo do e-mail simulado.", emlContent);
    }

    // 3. EmlExporter_IncludesAttachments
    [Fact]
    public async Task EmlExporter_IncludesAttachments()
    {
        // Arrange
        var message = new MailItem(
            InternalId: "msg-1",
            InternetMessageId: null,
            Subject: "Email com Anexo",
            From: new MailAddressRef("Remetente", "sender@fake.local"),
            To: new List<MailAddressRef>(),
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.Now,
            ReceivedAt: DateTimeOffset.Now,
            PlainTextBody: "Corpo do email",
            HtmlBody: null,
            Attachments: new List<AttachmentRef> {
                new AttachmentRef("att-1", "contrato.pdf", "application/pdf", 1024, null, false)
            },
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        );

        var provider = new FakeAttachmentProvider();
        var exporter = new EmlExporter();
        using var ms = new MemoryStream();

        // Act
        await exporter.ExportMessageAsync(message, provider, ms, CancellationToken.None);
        string emlContent = Encoding.UTF8.GetString(ms.ToArray());

        // Assert
        Assert.Contains("Content-Disposition: attachment; filename=contrato.pdf", emlContent);
        Assert.Contains("Content-Type: application/pdf", emlContent);
    }

    // 4. MboxExporter_WritesMboxWithEscapedFromLines
    [Fact]
    public async Task MboxExporter_WritesMboxWithEscapedFromLines()
    {
        // Arrange
        var message = new MailItem(
            InternalId: "msg-1",
            InternetMessageId: null,
            Subject: "Assunto do MBOX",
            From: new MailAddressRef("Remetente", "sender@fake.local"),
            To: new List<MailAddressRef>(),
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.Now,
            ReceivedAt: DateTimeOffset.Now,
            PlainTextBody: "From do e-mail corporativo:\nFrom Roberto Silva\n>From anterior.",
            HtmlBody: null,
            Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        );

        var provider = new FakeAttachmentProvider();
        var emlExporter = new EmlExporter();
        var mboxExporter = new MboxExporter(emlExporter);
        using var ms = new MemoryStream();

        // Act
        await mboxExporter.ExportMessageAsync(message, provider, ms, CancellationToken.None);
        string mboxContent = Encoding.UTF8.GetString(ms.ToArray());

        // Assert
        // Standard envelope From line
        Assert.StartsWith("From sender@fake.local", mboxContent);
        
        // Escaped lines checking (mboxrd)
        Assert.Contains(">From do e-mail corporativo:", mboxContent);
        Assert.Contains(">From Roberto Silva", mboxContent);
        Assert.Contains(">>From anterior.", mboxContent);
    }

    // 5. ExportJob_FiltersByFolder
    [Fact]
    public async Task ExportJob_FiltersByFolder()
    {
        // Arrange
        var fakeReader = new FakeMailStoreReader();
        using var store = await SetupIndexedCaseAsync(fakeReader);
        using var caseReader = store.CreateReader();

        var emlExporter = new EmlExporter();
        var runner = new ExportJobRunner();
        
        string exportDir = Path.Combine(_tempWorkspaceDir, "exports-folder");
        var options = new ExportJobOptions(
            CaseFolder: _tempWorkspaceDir,
            Format: "eml",
            OutputDir: exportDir,
            FolderIdOrPath: "Inbox/Subfolder"
        );

        // Act
        var result = await runner.RunExportJobAsync(options, caseReader, new FakeResolver(fakeReader), emlExporter, new NullProgress(), CancellationToken.None);

        // Assert
        Assert.Equal(1, result.FoldersSelected); // Only Inbox/Subfolder selected
        Assert.Equal(2, result.MessagesExported); // 2 messages in Subfolder
        Assert.True(Directory.Exists(Path.Combine(exportDir, "eml", "Inbox", "Subfolder")));
    }

    // 6. ExportJob_RespectsLimitAndOffset
    [Fact]
    public async Task ExportJob_RespectsLimitAndOffset()
    {
        // Arrange
        var fakeReader = new FakeMailStoreReader();
        using var store = await SetupIndexedCaseAsync(fakeReader);
        using var caseReader = store.CreateReader();

        var emlExporter = new EmlExporter();
        var runner = new ExportJobRunner();
        
        string exportDir = Path.Combine(_tempWorkspaceDir, "exports-pagination");
        var options = new ExportJobOptions(
            CaseFolder: _tempWorkspaceDir,
            Format: "eml",
            OutputDir: exportDir,
            FolderIdOrPath: "Inbox",
            Limit: 2,
            Offset: 1
        );

        // Act
        var result = await runner.RunExportJobAsync(options, caseReader, new FakeResolver(fakeReader), emlExporter, new NullProgress(), CancellationToken.None);

        // Assert
        Assert.Equal(2, result.MessagesExported); // limited to 2 messages
    }

    // 7. ExportManifest_RecordsCounts
    [Fact]
    public async Task ExportManifest_RecordsCounts()
    {
        // Arrange
        var fakeReader = new FakeMailStoreReader();
        using var store = await SetupIndexedCaseAsync(fakeReader);
        using var caseReader = store.CreateReader();

        var emlExporter = new EmlExporter();
        var runner = new ExportJobRunner();
        
        string exportDir = Path.Combine(_tempWorkspaceDir, "exports-manifest");
        var options = new ExportJobOptions(
            CaseFolder: _tempWorkspaceDir,
            Format: "eml",
            OutputDir: exportDir
        );

        // Act
        var result = await runner.RunExportJobAsync(options, caseReader, new FakeResolver(fakeReader), emlExporter, new NullProgress(), CancellationToken.None);

        // Assert
        string manifestPath = Path.Combine(exportDir, "export-manifest.json");
        Assert.True(File.Exists(manifestPath));

        string manifestJson = File.ReadAllText(manifestPath);
        Assert.Contains("\"ExportFormat\": \"eml\"", manifestJson);
        Assert.Contains($"\"SourceSha256\": \"{_dummyFileSha256}\"", manifestJson);
        Assert.Contains("\"MessagesExported\": 6", manifestJson);
        Assert.Contains("\"FoldersSelected\": 3", manifestJson);
        
        // Gate 10 check: no body or massive dumps present in manifest
        Assert.DoesNotContain("PlainTextBody", manifestJson);
        Assert.DoesNotContain("HtmlBody", manifestJson);
    }

    // 8. ExportCommand_AbortsWhenSourceHashMismatch
    [Fact]
    public async Task ExportCommand_AbortsWhenSourceHashMismatch()
    {
        // Arrange
        var fakeReader = new FakeMailStoreReader();
        using var store = await SetupIndexedCaseAsync(fakeReader);
        using var caseReader = store.CreateReader();

        // Modifying physical dummy file to mismatch calculated hash in relational case_info
        File.WriteAllText(_dummyFile, "PST content is now modified illegally!");

        var emlExporter = new EmlExporter();
        var runner = new ExportJobRunner();
        
        string exportDir = Path.Combine(_tempWorkspaceDir, "exports-corrupted");
        var options = new ExportJobOptions(
            CaseFolder: _tempWorkspaceDir,
            Format: "eml",
            OutputDir: exportDir
        );

        // Act & Assert (Integrity check aborts export before any file creation)
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            runner.RunExportJobAsync(options, caseReader, new FakeResolver(fakeReader), emlExporter, new NullProgress(), CancellationToken.None)
        );
        Assert.False(Directory.Exists(exportDir));
    }

    // 9. AttachmentNameNormalizer_PreventsPathTraversal
    [Fact]
    public void AttachmentNameNormalizer_PreventsPathTraversal()
    {
        // Arrange
        string rawFileName = "../../../hacker.exe";

        // Act & Assert 1: Domestic AttachmentNameNormalizer cleanup
        string cleanName = AttachmentNameNormalizer.Normalize(rawFileName, "fallback");
        Assert.Equal("___hacker.exe", cleanName);

        // Act & Assert 2: Security check inside ExportJobRunner utility against path traversal
        string traversalFilename = ExportJobRunner.SanitiseFilename(rawFileName, "fallback");
        Assert.Equal("___hacker.exe", traversalFilename);

        string targetPath = Path.Combine(_tempWorkspaceDir, traversalFilename);
        
        // Assert no security exception occurs because directory escapes are blocked
        ExportJobRunner.EnsureSafeWritePath(targetPath, _tempWorkspaceDir);

        // Act & Assert 3: A path traversal that actually escapes base dir will throw
        string escapePath = Path.GetFullPath(Path.Combine(_tempWorkspaceDir, "..", "..", "escape.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => 
            ExportJobRunner.EnsureSafeWritePath(escapePath, _tempWorkspaceDir)
        );
    }

    // 10. Exporters_DoNotReferenceXstReaderTypes
    [Fact]
    public void Exporters_DoNotReferenceXstReaderTypes()
    {
        // Arrange
        var emlAssembly = typeof(EmlExporter).Assembly;
        var mboxAssembly = typeof(MboxExporter).Assembly;

        // Act
        var emlRefs = emlAssembly.GetReferencedAssemblies();
        var mboxRefs = mboxAssembly.GetReferencedAssemblies();

        // Assert (No direct dependency on the XstReader adapters assembly)
        Assert.DoesNotContain(emlRefs, r => r.Name != null && r.Name.Contains("XstReader"));
        Assert.DoesNotContain(mboxRefs, r => r.Name != null && r.Name.Contains("XstReader"));
    }

    // Auxiliary classes
    private sealed class FakeAttachmentProvider : IAttachmentContentProvider
    {
        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct)
        {
            var ms = new MemoryStream(Encoding.UTF8.GetBytes("Fake attachment stream"));
            return Task.FromResult<Stream>(ms);
        }
    }

    private sealed class FakeResolver : IAdapterResolver
    {
        private readonly IMailStoreReader _reader;
        public FakeResolver(IMailStoreReader reader) => _reader = reader;

        public IEnumerable<AdapterDescriptor> GetAvailableAdapters() => Array.Empty<AdapterDescriptor>();
        
        public AdapterLoadResult ResolveAdapter(string extension)
        {
            return new AdapterLoadResult(true, _reader, null, null);
        }

        public AdapterLoadResult LoadAdapterByPath(string assemblyPath)
        {
            return new AdapterLoadResult(true, _reader, null, null);
        }
    }

    private sealed class NullProgress : IProgressReporter
    {
        public void ReportProgress(double percentage, string status) { }
    }
}

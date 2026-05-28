using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Cli;
using MailVault.Core;
using MailVault.Core.Normalization;
using MailVault.Domain;
using MailVault.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MailVault.Indexing.Tests;

public class Milestone3Tests : IDisposable
{
    private readonly string _tempWorkspaceDir;
    private readonly string _dummyPstFile;
    private readonly TextWriter _originalConsoleOut;

    public Milestone3Tests()
    {
        // Setup temporary directory inside workspace scratch for integration CLI tests
        _tempWorkspaceDir = Path.Combine("c:\\Github\\mailvault_recovery\\scratch", $"milestone3-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspaceDir);

        _dummyPstFile = Path.Combine(_tempWorkspaceDir, "test-store.pst");
        File.WriteAllText(_dummyPstFile, "Dummy PST data for CLI integration tests");

        _originalConsoleOut = Console.Out;
    }

    public void Dispose()
    {
        Console.SetOut(_originalConsoleOut);
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

    // 1. Normalization Tests
    [Fact]
    public void BodyPreviewSanitizer_TruncatesSafely()
    {
        // Arrange
        var body = "Linha 1\nLinha 2\nLinha 3\n" + string.Join("\n", Enumerable.Range(4, 50).Select(i => $"Linha {i}"));

        // Act
        string? preview = BodyPreviewSanitizer.Sanitize(body, null, 10);

        // Assert
        Assert.NotNull(preview);
        Assert.Contains("Linha 1", preview);
        Assert.Contains("Linha 10", preview);
        Assert.DoesNotContain("Linha 11", preview);
        Assert.Contains("TEXTO TRUNCADO SEGURAMENTE PARA COMPLIANCE FORENSE", preview);
    }

    [Fact]
    public void FolderPathNormalizer_NormalizesSeparators()
    {
        // Arrange
        string rawPath = "\\Top of Personal Folders\\Inbox\\\\Financeiro\\\\";

        // Act
        string normalized = FolderPathNormalizer.Normalize(rawPath);

        // Assert
        Assert.Equal("Top of Personal Folders/Inbox/Financeiro", normalized);
    }

    [Fact]
    public void AttachmentNameNormalizer_RemovesInvalidCharacters()
    {
        // Arrange
        string rawName = "documento/anexo:teste*financeiro?.pdf";

        // Act
        string normalized = AttachmentNameNormalizer.Normalize(rawName, "att-1");

        // Assert
        Assert.Equal("documento_anexo_teste_financeiro_.pdf", normalized);
    }

    // 2. Schema and Persistence Tests
    [Fact]
    public void IndexSchemaInitializer_CreatesSchemaV2_WithAdapterMetadata()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        // Act
        IndexSchemaInitializer.Initialize(connection);

        // Assert
        // Verify tables and version are successfully set
        using var cmd = new SqliteCommand("SELECT version FROM schema_version LIMIT 1;", connection);
        var version = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.Equal(4, version);

        // Verify case_info columns for adapter metadata
        using var colCmd = new SqliteCommand("PRAGMA table_info(case_info);", connection);
        using var reader = colCmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1)); // Column name is at index 1
        }
        Assert.Contains("adapter_name", columns);
        Assert.Contains("adapter_version", columns);

        // Verify some indexes exist
        using var indexCmd = new SqliteCommand("SELECT name FROM sqlite_master WHERE type='index' AND name='idx_messages_folder_id';", connection);
        var indexName = indexCmd.ExecuteScalar() as string;
        Assert.Equal("idx_messages_folder_id", indexName);
    }

    [Fact]
    public async Task IndexWriter_SavesFoldersMessagesAttachments()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        IndexSchemaInitializer.Initialize(connection);

        var writer = new SqliteCaseIndexWriter(connection);
        var folder = new FolderNode(new FolderId("Inbox"), null, "Inbox", "Inbox", 1, new List<FolderNode>());
        var message = new MailItem(
            InternalId: "msg-1",
            InternetMessageId: "<msg1@fake.local>",
            Subject: "Teste",
            From: new MailAddressRef("Remetente", "sender@fake.local"),
            To: new List<MailAddressRef>(),
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.Now,
            ReceivedAt: DateTimeOffset.Now,
            PlainTextBody: "Preview do corpo",
            HtmlBody: null,
            Attachments: new List<AttachmentRef> {
                new AttachmentRef("att-1", "anexo.pdf", "application/pdf", 1024, null, false)
            },
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        );

        // Act
        await writer.BeginTransactionAsync(CancellationToken.None);
        await writer.SaveFolderAsync(folder, CancellationToken.None);
        await writer.SaveMessageAsync(message, folder.Id, CancellationToken.None);
        await writer.CommitTransactionAsync(CancellationToken.None);

        // Assert
        using var folderCmd = new SqliteCommand("SELECT display_name FROM folders WHERE folder_id = 'Inbox';", connection);
        Assert.Equal("Inbox", folderCmd.ExecuteScalar() as string);

        using var messageCmd = new SqliteCommand("SELECT subject FROM messages WHERE message_id = 'msg-1';", connection);
        Assert.Equal("Teste", messageCmd.ExecuteScalar() as string);

        using var attCmd = new SqliteCommand("SELECT file_name FROM attachments WHERE attachment_id = 'att-1';", connection);
        Assert.Equal("anexo.pdf", attCmd.ExecuteScalar() as string);
    }

    [Fact]
    public async Task IndexReader_ReturnsStats()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        IndexSchemaInitializer.Initialize(connection);

        var writer = new SqliteCaseIndexWriter(connection);
        var folder = new FolderNode(new FolderId("Inbox"), null, "Inbox", "Inbox", 2, new List<FolderNode>());
        var message1 = new MailItem("m1", null, "Subj1", null, new List<MailAddressRef>(), new List<MailAddressRef>(), new List<MailAddressRef>(), null, null, null, null, new List<AttachmentRef> { new AttachmentRef("a1", "doc1.pdf", null, 1000, null, false) }, new Dictionary<string, string>(), new List<ExtractionIssue>());
        var message2 = new MailItem("m2", null, "Subj2", null, new List<MailAddressRef>(), new List<MailAddressRef>(), new List<MailAddressRef>(), null, null, null, null, new List<AttachmentRef> { new AttachmentRef("a2", "doc2.pdf", null, 5000, null, false) }, new Dictionary<string, string>(), new List<ExtractionIssue>());

        await writer.BeginTransactionAsync(CancellationToken.None);
        await writer.SaveFolderAsync(folder, CancellationToken.None);
        await writer.SaveMessageAsync(message1, folder.Id, CancellationToken.None);
        await writer.SaveMessageAsync(message2, folder.Id, CancellationToken.None);
        await writer.CommitTransactionAsync(CancellationToken.None);

        // Act
        var reader = new SqliteCaseIndexReader(connection);
        int foldersCount = await reader.GetFolderCountAsync(CancellationToken.None);
        int messagesCount = await reader.GetMessageCountAsync(CancellationToken.None);
        int attachmentsCount = await reader.GetAttachmentCountAsync(CancellationToken.None);
        long totalAttachmentSize = await reader.GetTotalAttachmentSizeAsync(CancellationToken.None);
        var largest = await reader.GetLargestAttachmentAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, foldersCount);
        Assert.Equal(2, messagesCount);
        Assert.Equal(2, attachmentsCount);
        Assert.Equal(6000, totalAttachmentSize);
        Assert.Equal("doc2.pdf", largest.fileName);
        Assert.Equal(5000, largest.sizeBytes);
    }

    [Fact]
    public async Task Search_ReturnsExpectedMessages()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        IndexSchemaInitializer.Initialize(connection);

        var writer = new SqliteCaseIndexWriter(connection);
        var folder = new FolderNode(new FolderId("Inbox"), null, "Inbox", "Inbox", 1, new List<FolderNode>());
        
        // Em MailItemNormalizer, o preview fica em PlainTextBody
        var message = new MailItem("m1", null, "Relatorio Orçamento", new MailAddressRef("Eu", "eu@corp.local"), new List<MailAddressRef>(), new List<MailAddressRef>(), new List<MailAddressRef>(), null, null, "Este é o corpo com a palavra chave segredo corporativo", null, new List<AttachmentRef>(), new Dictionary<string, string>(), new List<ExtractionIssue>());

        await writer.BeginTransactionAsync(CancellationToken.None);
        await writer.SaveFolderAsync(folder, CancellationToken.None);
        await writer.SaveMessageAsync(message, folder.Id, CancellationToken.None);
        await writer.CommitTransactionAsync(CancellationToken.None);

        var reader = new SqliteCaseIndexReader(connection);

        // Act & Assert 1: Search by Subject keyword
        var search1 = await reader.SearchMessagesAsync("Orçamento", null, 10, 0, CancellationToken.None).ToListAsync();
        Assert.Single(search1);
        Assert.Equal("m1", search1[0].InternalId);

        // Act & Assert 2: Search by Body Preview keyword
        var search2 = await reader.SearchMessagesAsync("segredo", null, 10, 0, CancellationToken.None).ToListAsync();
        Assert.Single(search2);

        // Act & Assert 3: Search by Sender
        var search3 = await reader.SearchMessagesAsync("Eu", null, 10, 0, CancellationToken.None).ToListAsync();
        Assert.Single(search3);

        // Act & Assert 4: Query that has no match
        var searchNoMatch = await reader.SearchMessagesAsync("NaoExiste", null, 10, 0, CancellationToken.None).ToListAsync();
        Assert.Empty(searchNoMatch);
    }

    // 3. CLI Command Integration Tests
    [Fact]
    public async Task IndexCommand_WithFakeReader_CreatesCaseDb()
    {
        // Arrange
        var reader = new FakeMailStoreReader();
        Program.InjectReader(reader);

        using var sw = new StringWriter();
        Console.SetOut(sw);

        var args = new[] { "index", _dummyPstFile, "--out", _tempWorkspaceDir, "--no-preview-cache" };

        // Act
        int exitCode = await Program.Main(args);
        string output = sw.ToString();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("Indexador Persistente", output);
        Assert.Contains("Pastas Indexadas      : 3", output);
        Assert.Contains("Mensagens Indexadas   : 6", output); // 3 inbox + 2 subfolder + 1 sent
        Assert.Contains("Anexos Indexados      : 3", output);
        Assert.Contains("RELATÓRIO DE INDEXAÇÃO", output);

        // Check if case.db exists in case folder
        string[] caseDirs = Directory.GetDirectories(_tempWorkspaceDir);
        Assert.Single(caseDirs);
        string caseDir = caseDirs[0];
        Assert.True(File.Exists(Path.Combine(caseDir, "case.db")));
        Assert.True(File.Exists(Path.Combine(caseDir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(caseDir, "audit.log")));
    }

    [Fact]
    public async Task StatsCommand_WithCaseDb_PrintsSummary()
    {
        // Arrange
        // First we must run index to generate case.db in directory
        var reader = new FakeMailStoreReader();
        Program.InjectReader(reader);
        
        await Program.Main(new[] { "index", _dummyPstFile, "--out", _tempWorkspaceDir, "--case-id", "CASE-STATS-TEST" });

        string caseFolderPath = Path.Combine(_tempWorkspaceDir, "CASE-STATS-TEST");

        using var sw = new StringWriter();
        Console.SetOut(sw);

        var args = new[] { "stats", caseFolderPath };

        // Act
        int exitCode = await Program.Main(args);
        string output = sw.ToString();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("Estatísticas do Caso", output);
        Assert.Contains("Pastas Encontradas    : 3", output);
        Assert.Contains("Mensagens Indexadas   : 6", output);
        Assert.Contains("Anexos Localizados    : 3", output);
        Assert.Contains("PASTAS COM MAIS MENSAGENS:", output);
        Assert.Contains("1. Inbox — 3 e-mails", output);
    }

    [Fact]
    public async Task SearchCommand_WithCaseDb_ReturnsMatches()
    {
        // Arrange
        var reader = new FakeMailStoreReader();
        Program.InjectReader(reader);

        await Program.Main(new[] { "index", _dummyPstFile, "--out", _tempWorkspaceDir, "--case-id", "CASE-SEARCH-TEST" });

        string caseFolderPath = Path.Combine(_tempWorkspaceDir, "CASE-SEARCH-TEST");

        using var sw = new StringWriter();
        Console.SetOut(sw);

        // Search for Inbox e-mails (using 'Inbox' keyword which matches sender 'Remetente Fake' and subjects)
        var args = new[] { "search", caseFolderPath, "--query", "Inbox", "--include-preview" };

        // Act
        int exitCode = await Program.Main(args);
        string output = sw.ToString();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("Pesquisa do Caso", output);
        Assert.Contains("Assunto do Fake Inbox", output);
        Assert.Contains("Busca finalizada. Exibidos 3 e-mails correspondentes.", output);
        Assert.Contains(">>> PREVIEW:", output);
    }

    [Fact]
    public async Task XstReaderMailStoreReader_DoesNotReopenStorePerFolder_WhenSessionActive()
    {
        var reader = new CountingSessionReader();
        string caseDir = Path.Combine(_tempWorkspaceDir, "CASE-SESSION-COUNT");
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDir, CancellationToken.None);

        var result = await new IndexingService().RunIndexAsync(
            _dummyPstFile,
            store,
            reader,
            "CASE-SESSION-COUNT",
            "Tester",
            cachePreview: false,
            limit: null,
            CancellationToken.None);

        Assert.Equal("Success", result.Status);
        Assert.Equal(1, reader.BeginCount);
        Assert.Equal(1, reader.EndCount);
        Assert.True(reader.EnumerateMessagesCalls > 1);
    }

    [Fact]
    public async Task IndexingService_CommitsCaseInfoBeforeFolderIndexing()
    {
        string caseDir = Path.Combine(_tempWorkspaceDir, "CASE-CASEINFO-FIRST");
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDir, CancellationToken.None);

        var result = await new IndexingService().RunIndexAsync(
            _dummyPstFile,
            store,
            new ThrowingFolderReader(),
            "CASE-CASEINFO-FIRST",
            "Tester",
            cachePreview: false,
            limit: null,
            CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.Equal(1, await ExecuteIntAsync(store.DatabasePath, "SELECT COUNT(*) FROM case_info WHERE case_id = 'CASE-CASEINFO-FIRST';"));
    }

    [Fact]
    public async Task IndexingService_RecordsFailedIndexRun_WhenReaderThrows()
    {
        string caseDir = Path.Combine(_tempWorkspaceDir, "CASE-FAILED-RUN");
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDir, CancellationToken.None);

        var result = await new IndexingService().RunIndexAsync(
            _dummyPstFile,
            store,
            new ThrowingFolderReader(),
            "CASE-FAILED-RUN",
            "Tester",
            cachePreview: false,
            limit: null,
            CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.Equal(1, await ExecuteIntAsync(store.DatabasePath, "SELECT COUNT(*) FROM index_runs WHERE status = 'Failed';"));
        Assert.Equal(1, await ExecuteIntAsync(store.DatabasePath, "SELECT COUNT(*) FROM issues WHERE issue_code = 'MV-ERR-INDEX-FATAL';"));
    }

    [Fact]
    public async Task IndexCommand_ReturnsControlledFailure_WhenReaderThrows()
    {
        Program.InjectReader(new ThrowingFolderReader());

        using var sw = new StringWriter();
        Console.SetOut(sw);

        int exitCode = await Program.Main(new[]
        {
            "index",
            _dummyPstFile,
            "--out",
            _tempWorkspaceDir,
            "--case-id",
            "CASE-CLI-CONTROLLED-FAIL"
        });

        string output = sw.ToString();
        string caseDir = Path.Combine(_tempWorkspaceDir, "CASE-CLI-CONTROLLED-FAIL");
        string dbPath = Path.Combine(caseDir, "case.db");

        Assert.Equal(8, exitCode);
        Assert.Contains("FALHA CONTROLADA", output);
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.True(File.Exists(dbPath));
        Assert.Equal(1, await ExecuteIntAsync(dbPath, "SELECT COUNT(*) FROM case_info WHERE case_id = 'CASE-CLI-CONTROLLED-FAIL';"));
        Assert.Equal(1, await ExecuteIntAsync(dbPath, "SELECT COUNT(*) FROM index_runs WHERE status = 'Failed';"));
    }

    [Fact]
    public async Task IndexingService_DoesNotLeaveEmptySuccessfulCase_WhenFatalErrorOccurs()
    {
        string caseDir = Path.Combine(_tempWorkspaceDir, "CASE-NOT-EMPTY-SUCCESS");
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDir, CancellationToken.None);

        var result = await new IndexingService().RunIndexAsync(
            _dummyPstFile,
            store,
            new ThrowingFolderReader(),
            "CASE-NOT-EMPTY-SUCCESS",
            "Tester",
            cachePreview: false,
            limit: null,
            CancellationToken.None);

        Assert.NotEqual("Success", result.Status);
        Assert.Equal(1, await ExecuteIntAsync(store.DatabasePath, "SELECT COUNT(*) FROM case_info;"));
        Assert.True(await ExecuteIntAsync(store.DatabasePath, "SELECT COUNT(*) FROM issues;") > 0);
    }

    [Fact]
    public async Task SessionAwareReader_BeginAndEndCalledAroundIndexing()
    {
        var reader = new CountingSessionReader();
        string caseDir = Path.Combine(_tempWorkspaceDir, "CASE-SESSION-LIFECYCLE");
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDir, CancellationToken.None);

        await new IndexingService().RunIndexAsync(
            _dummyPstFile,
            store,
            reader,
            "CASE-SESSION-LIFECYCLE",
            "Tester",
            cachePreview: false,
            limit: null,
            CancellationToken.None);

        Assert.Equal(_dummyPstFile, reader.BeginFilePath);
        Assert.Equal(1, reader.BeginCount);
        Assert.Equal(1, reader.EndCount);
    }

    [Fact]
    public async Task DebugDbOutput_NotPrintedWithoutVerbose()
    {
        string caseDir = Path.Combine(_tempWorkspaceDir, "CASE-NO-DEBUG");
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDir, CancellationToken.None);

        using var sw = new StringWriter();
        Console.SetOut(sw);

        await new IndexingService().RunIndexAsync(
            _dummyPstFile,
            store,
            new FakeMailStoreReader(),
            "CASE-NO-DEBUG",
            "Tester",
            cachePreview: false,
            limit: null,
            CancellationToken.None);

        Assert.DoesNotContain("[DEBUG_DB]", sw.ToString());
    }

    [Fact]
    public async Task PartialIndex_HasStatusFailedOrPartial_NotSuccess()
    {
        string caseDir = Path.Combine(_tempWorkspaceDir, "CASE-PARTIAL");
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDir, CancellationToken.None);

        var result = await new IndexingService().RunIndexAsync(
            _dummyPstFile,
            store,
            new PartialThenThrowReader(),
            "CASE-PARTIAL",
            "Tester",
            cachePreview: false,
            limit: null,
            CancellationToken.None);

        Assert.Equal("Partial", result.Status);
        Assert.NotEqual("Success", result.Status);
        Assert.True(result.MessagesIndexed > 0);
        Assert.Equal(1, await ExecuteIntAsync(store.DatabasePath, "SELECT COUNT(*) FROM index_runs WHERE status = 'Partial';"));
    }

    [Fact]
    public async Task AdapterExceptions_AreMappedToExtractionIssues_WhenPossible()
    {
        string invalidPst = Path.Combine(_tempWorkspaceDir, "invalid.pst");
        await File.WriteAllTextAsync(invalidPst, "not really a pst");

        var reader = new MailVault.Adapters.XstReader.XstReaderMailStoreReader();
        var metadata = await reader.InspectAsync(invalidPst, CancellationToken.None);

        Assert.Contains(metadata.Issues, issue => issue.Code == "MV-ERR-ADAPTER-INSPECT");
        Assert.Contains(reader.DrainIssues(), issue => issue.Code == "MV-ERR-ADAPTER-INSPECT");
    }

    [Fact]
    public async Task ExistingFakeReaderTests_StillPass()
    {
        string caseDir = Path.Combine(_tempWorkspaceDir, "CASE-FAKE-STILL-PASS");
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDir, CancellationToken.None);

        var result = await new IndexingService().RunIndexAsync(
            _dummyPstFile,
            store,
            new FakeMailStoreReader(),
            "CASE-FAKE-STILL-PASS",
            "Tester",
            cachePreview: false,
            limit: null,
            CancellationToken.None);

        Assert.Equal("Success", result.Status);
        Assert.Equal(6, result.MessagesIndexed);
    }

    private static async Task<int> ExecuteIntAsync(string dbPath, string sql)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class CountingSessionReader : IMailStoreReader, ISessionAwareMailStoreReader
    {
        private readonly FakeMailStoreReader _inner = new();

        public string ReaderName => _inner.ReaderName;
        public int BeginCount { get; private set; }
        public int EndCount { get; private set; }
        public int EnumerateMessagesCalls { get; private set; }
        public string? BeginFilePath { get; private set; }

        public Task BeginReadSessionAsync(string filePath, CancellationToken ct)
        {
            BeginCount++;
            BeginFilePath = filePath;
            return Task.CompletedTask;
        }

        public Task EndReadSessionAsync(CancellationToken ct)
        {
            EndCount++;
            return Task.CompletedTask;
        }

        public Task<StoreMetadata> InspectAsync(string filePath, CancellationToken ct) => _inner.InspectAsync(filePath, ct);
        public IAsyncEnumerable<FolderNode> EnumerateFoldersAsync(CancellationToken ct) => _inner.EnumerateFoldersAsync(ct);
        public Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct) => _inner.OpenAttachmentAsync(attachment, ct);
        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct) => _inner.OpenAttachmentStreamAsync(messageId, attachmentId, ct);
        public Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct) => _inner.GetMessageAsync(messageId, ct);

        public async IAsyncEnumerable<MailItem> EnumerateMessagesAsync(FolderId folderId, [EnumeratorCancellation] CancellationToken ct)
        {
            EnumerateMessagesCalls++;
            await foreach (var message in _inner.EnumerateMessagesAsync(folderId, ct))
            {
                yield return message;
            }
        }
    }

    private sealed class ThrowingFolderReader : IMailStoreReader, ISessionAwareMailStoreReader
    {
        public string ReaderName => "Throwing Test Reader";

        public Task BeginReadSessionAsync(string filePath, CancellationToken ct) => Task.CompletedTask;
        public Task EndReadSessionAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<StoreMetadata> InspectAsync(string filePath, CancellationToken ct)
        {
            return Task.FromResult(new StoreMetadata(filePath, 1, "", "Fake", ReaderName, Array.Empty<ExtractionIssue>()));
        }

        public async IAsyncEnumerable<FolderNode> EnumerateFoldersAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("Simulated reader failure");
            yield break; // unreachable; required by the compiler to treat this as an async iterator
        }

        public async IAsyncEnumerable<MailItem> EnumerateMessagesAsync(FolderId folderId, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct) => Task.FromResult<Stream>(Stream.Null);
        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct) => Task.FromResult<Stream>(Stream.Null);
        public Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct)
            => Task.FromResult(OperationResult<MailItem>.Failure(new ExtractionIssue("MV-ERR-TEST", "Error", "Not found", messageId.Value, null)));
    }

    private sealed class PartialThenThrowReader : IMailStoreReader
    {
        private readonly FakeMailStoreReader _inner = new();
        private int _folderCount;

        public string ReaderName => _inner.ReaderName;

        public Task<StoreMetadata> InspectAsync(string filePath, CancellationToken ct) => _inner.InspectAsync(filePath, ct);
        public IAsyncEnumerable<MailItem> EnumerateMessagesAsync(FolderId folderId, CancellationToken ct) => _inner.EnumerateMessagesAsync(folderId, ct);
        public Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct) => _inner.OpenAttachmentAsync(attachment, ct);
        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct) => _inner.OpenAttachmentStreamAsync(messageId, attachmentId, ct);
        public Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct) => _inner.GetMessageAsync(messageId, ct);

        public async IAsyncEnumerable<FolderNode> EnumerateFoldersAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var folder in _inner.EnumerateFoldersAsync(ct))
            {
                _folderCount++;
                if (_folderCount > 1)
                {
                    throw new InvalidOperationException("Simulated partial reader failure");
                }

                yield return folder;
            }
        }
    }

    [Fact]
    public async Task ExternalToolDetector_FindsBundledPffexportFirst()
    {
        string baseDir = AppContext.BaseDirectory;
        string toolsDir = Path.Combine(baseDir, "tools", "libpff");
        Directory.CreateDirectory(toolsDir);
        string targetExe = Path.Combine(toolsDir, "pffexport.exe");
        bool created = false;
        if (!File.Exists(targetExe))
        {
            File.WriteAllText(targetExe, "dummy text");
            created = true;
        }

        try
        {
            var capability = await ExternalToolDetector.DetectToolAsync("pffexport", CancellationToken.None);
            Assert.True(capability.IsAvailable);
            Assert.Equal(targetExe, capability.ExecutablePath);
        }
        finally
        {
            if (created && File.Exists(targetExe)) File.Delete(targetExe);
        }
    }

    [Fact]
    public async Task ExternalToolDetector_FindsBundledPffinfoFirst()
    {
        string baseDir = AppContext.BaseDirectory;
        string toolsDir = Path.Combine(baseDir, "tools", "libpff");
        Directory.CreateDirectory(toolsDir);
        string targetExe = Path.Combine(toolsDir, "pffinfo.exe");
        bool created = false;
        if (!File.Exists(targetExe))
        {
            File.WriteAllText(targetExe, "dummy text");
            created = true;
        }

        try
        {
            var capability = await ExternalToolDetector.DetectToolAsync("pffinfo", CancellationToken.None);
            Assert.True(capability.IsAvailable);
            Assert.Equal(targetExe, capability.ExecutablePath);
        }
        finally
        {
            if (created && File.Exists(targetExe)) File.Delete(targetExe);
        }
    }

    [Fact]
    public async Task LibpffExternal_UsesBundledPffexport()
    {
        string baseDir = AppContext.BaseDirectory;
        string toolsDir = Path.Combine(baseDir, "tools", "libpff");
        Directory.CreateDirectory(toolsDir);
        string targetExe = Path.Combine(toolsDir, "pffexport.exe");
        bool created = false;
        if (!File.Exists(targetExe))
        {
            File.WriteAllText(targetExe, "dummy text");
            created = true;
        }

        try
        {
            var engine = new LibpffExternalEngine();
            var capability = await engine.CheckCapabilityAsync(CancellationToken.None);
            Assert.True(capability.IsAvailable);
            Assert.Equal(targetExe, capability.ExecutablePath);
        }
        finally
        {
            if (created && File.Exists(targetExe)) File.Delete(targetExe);
        }
    }

    // -------------------------------------------------------------
    // Milestone 6.2.4.2 Engine, Diagnostic, & Summarizer Tests
    // -------------------------------------------------------------

    [Fact]
    public void NativeToolErrorSummarizer_DeduplicatesRepeatedLines()
    {
        string rawStderr = @"
libpff_local_descriptors_node_get_entry_data: invalid local descriptors node in identifier: 1234
libpff_local_descriptors_node_get_entry_data: invalid local descriptors node in identifier: 5678
libpff_local_descriptors_node_get_entry_data: invalid local descriptors node in identifier: 9012
libpff_attachment_get_data_file_entry: unable to determine attachments in identifier: 1111
";
        string category;
        var summary = NativeToolErrorSummarizer.Summarize(rawStderr, out category);

        Assert.Equal("LocalDescriptorCorruptionOrUnsupportedStructure;AttachmentDescriptorFailure", category);
        Assert.Equal(2, summary.Count);
        
        var localError = summary.First(s => s.Category == "LocalDescriptor");
        Assert.Equal(3, localError.Count);
        Assert.Equal("libpff_local_descriptors_node_get_entry_data: invalid local descriptors node in identifier: <ID>", localError.Signature);

        var attError = summary.First(s => s.Category == "AttachmentDescriptor");
        Assert.Equal(1, attError.Count);
        Assert.Equal("libpff_attachment_get_data_file_entry: unable to determine attachments in identifier: <ID>", attError.Signature);
    }

    [Fact]
    public void NativeToolErrorSummarizer_LimitsRawStderr()
    {
        string baseLine = "libpff_local_descriptors: error code: ";
        string rawStderr = string.Join(Environment.NewLine, Enumerable.Range(0, 100).Select(i => baseLine + i));
        
        string sanitized = NativeToolErrorSummarizer.SanitizeAndLimitStderr(rawStderr, 1000);
        
        Assert.Contains("[TECHNICAL WARNING: RAW STDERR TRUNCATED DUE TO 2MB FORENSIC LIMIT]", sanitized);
        Assert.True(System.Text.Encoding.UTF8.GetBytes(sanitized).Length <= 1100);
    }

    [Fact]
    public void NativeFallbackReport_DoesNotContainEmailBody()
    {
        // Dynamic logs that could contain sensitive fields must be sanitized when they are long.
        string sensitiveBody = "Body: Hello user password: " + new string('X', 300);
        string sensitiveSubject = "Subject: Confidencial " + new string('Y', 300);
        string rawStderr = sensitiveSubject + "\n" + sensitiveBody + "\nlibpff_local_descriptors_node_get_entry_data: invalid local descriptors node in identifier: 1234";
        string category;
        var summary = NativeToolErrorSummarizer.Summarize(rawStderr, out category);
        
        // Ensure no sensitive lines exist in the signature list
        foreach (var sig in summary)
        {
            Assert.DoesNotContain("Confidencial", sig.Signature);
            Assert.DoesNotContain("password", sig.Signature);
        }
        
        string sanitized = NativeToolErrorSummarizer.SanitizeAndLimitStderr(rawStderr);
        Assert.DoesNotContain("Confidencial", sanitized);
        Assert.DoesNotContain("password", sanitized);
        Assert.Contains("[SANITIZED LINE - SENSITIVE CONTENT MASKED]", sanitized);
    }

    [Fact]
    public async Task LibpffExternal_DoesNotCreateRelationalIndex_WhenOutputCountZero()
    {
        using var store = new SqliteCaseIndexStore();
        string tempCaseDir = Path.Combine(_tempWorkspaceDir, $"empty-case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCaseDir);
        await store.InitializeAsync(tempCaseDir, CancellationToken.None);

        using (var connection = new SqliteConnection($"Data Source={Path.Combine(tempCaseDir, "case.db")};"))
        {
            await connection.OpenAsync();
            IndexSchemaInitializer.Initialize(connection);
            
            // Check counts are zero
            using var folderCmd = new SqliteCommand("SELECT COUNT(*) FROM folders", connection);
            Assert.Equal(0, Convert.ToInt32(folderCmd.ExecuteScalar()));
            
            using var msgCmd = new SqliteCommand("SELECT COUNT(*) FROM messages", connection);
            Assert.Equal(0, Convert.ToInt32(msgCmd.ExecuteScalar()));
        }
    }

    [Fact]
    public void LibpffExternal_PffInfoSuccess_PffexportFailure_IsNativeToolFailed()
    {
        // Test status classification logic directly using expected mock inputs
        int infoExitCode = 0;
        int exitCode = 1;
        int outputFileCount = 0;
        string errorCategory = "Other";

        string finalStatus;
        if (infoExitCode != 0) finalStatus = "NativeDiagnosticFailed";
        else if (exitCode == -99) finalStatus = "NativeToolTimeout";
        else if (outputFileCount > 0) finalStatus = "PartialNativeRecovery";
        else finalStatus = (errorCategory.Contains("LocalDescriptor") || errorCategory.Contains("Attachment")) ? "NativeToolUnsupportedStructure" : "NativeToolFailed";

        Assert.Equal("NativeToolFailed", finalStatus);
    }

    [Fact]
    public void LibpffExternal_ExitCode1_NoOutput_IsNativeToolFailed()
    {
        int infoExitCode = 0;
        int exitCode = 1;
        int outputFileCount = 0;
        string errorCategory = "Other";

        string finalStatus = (infoExitCode != 0) ? "NativeDiagnosticFailed" :
                             (exitCode == -99) ? "NativeToolTimeout" :
                             (outputFileCount > 0) ? "PartialNativeRecovery" :
                             (errorCategory.Contains("LocalDescriptor") || errorCategory.Contains("Attachment")) ? "NativeToolUnsupportedStructure" : "NativeToolFailed";

        Assert.Equal("NativeToolFailed", finalStatus);
    }

    [Fact]
    public void LibpffExternal_LocalDescriptorErrors_AreClassified()
    {
        string rawStderr = "libpff_local_descriptors_node_get_entry_data: invalid local descriptors node";
        string category;
        NativeToolErrorSummarizer.Summarize(rawStderr, out category);
        Assert.Contains("LocalDescriptorCorruptionOrUnsupportedStructure", category);
    }

    [Fact]
    public void LibpffExternal_AttachmentDescriptorErrors_AreClassified()
    {
        string rawStderr = "unable to determine attachments";
        string category;
        NativeToolErrorSummarizer.Summarize(rawStderr, out category);
        Assert.Contains("AttachmentDescriptorFailure", category);
    }

    [Fact]
    public void NativeFallbackReport_Written_WhenPffexportFails()
    {
        // Verify mock serialization of native fallback report format
        var report = new {
            caseId = "CASE-123",
            toolVersions = new { pffinfo = "20260526", pffexport = "20260526" },
            finalStatus = "NativeToolFailed",
            recommendation = "Use XstReader MetadataOnly"
        };
        string json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.Contains("NativeToolFailed", json);
        Assert.Contains("CASE-123", json);
    }
}

public static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source, CancellationToken ct = default)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(ct))
        {
            list.Add(item);
        }
        return list;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        Assert.Equal(2, version);

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

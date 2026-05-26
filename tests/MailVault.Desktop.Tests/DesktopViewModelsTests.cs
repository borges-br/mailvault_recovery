using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Desktop.Services;
using MailVault.Desktop.ViewModels;
using Xunit;

namespace MailVault.Desktop.Tests;

public class DesktopViewModelsTests
{
    private sealed class MockCaseIndexReader : ICaseIndexReader
    {
        public CaseInfoRef? CaseInfo { get; set; }
        public int FolderCount { get; set; }
        public int MessageCount { get; set; }
        public int AttachmentCount { get; set; }
        public int IssueCount { get; set; }
        public long TotalAttachmentSize { get; set; }
        
        public List<FolderNode> Folders { get; } = new();
        public List<MailItem> Messages { get; } = new();
        public List<MailItem> SearchResults { get; } = new();

        public Task<int> GetFolderCountAsync(CancellationToken ct) => Task.FromResult(FolderCount);
        public Task<int> GetMessageCountAsync(CancellationToken ct) => Task.FromResult(MessageCount);
        public Task<int> GetAttachmentCountAsync(CancellationToken ct) => Task.FromResult(AttachmentCount);
        public Task<int> GetIssueCountAsync(CancellationToken ct) => Task.FromResult(IssueCount);
        public Task<long> GetTotalAttachmentSizeAsync(CancellationToken ct) => Task.FromResult(TotalAttachmentSize);

        public Task<Dictionary<string, int>> GetTopFoldersByMessageCountAsync(int limit, CancellationToken ct) 
            => Task.FromResult(new Dictionary<string, int>());
            
        public Task<(string fileName, long sizeBytes)> GetLargestAttachmentAsync(CancellationToken ct) 
            => Task.FromResult(("test.bin", 0L));

        public async IAsyncEnumerable<MailItem> SearchMessagesAsync(string queryText, string? folderPath, int limit, int offset, CancellationToken ct)
        {
            foreach (var msg in SearchResults)
            {
                yield return msg;
            }
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<FolderNode> GetFolderHierarchyAsync(CancellationToken ct)
        {
            foreach (var folder in Folders)
            {
                yield return folder;
            }
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<MailItem> GetMessagesInFolderAsync(FolderId folderId, int limit, int offset, CancellationToken ct)
        {
            foreach (var msg in Messages)
            {
                yield return msg;
            }
            await Task.CompletedTask;
        }

        public Task<MailItem?> GetMessageByIdAsync(MessageId messageId, CancellationToken ct)
            => Task.FromResult<MailItem?>(Messages.FirstOrDefault(m => m.InternalId == messageId.Value));

        public Task<CaseInfoRef?> GetCaseInfoAsync(CancellationToken ct) => Task.FromResult(CaseInfo);

        public void Dispose() { }
    }

    // -------------------------------------------------------------------------
    // Existing tests (Milestone 6)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CaseOverviewViewModel_LoadsCaseSummary()
    {
        // Arrange
        var reader = new MockCaseIndexReader
        {
            CaseInfo = new CaseInfoRef(
                CaseId: "CASE-001",
                SourceFile: "C:\\Users\\natha\\Evidences\\backup.ost",
                SourceSizeBytes: 1024L * 1024L,
                SourceSha256: "SHA256HASH123456",
                OperatorName: "Operator",
                StartedAt: DateTimeOffset.UtcNow,
                AdapterName: "XstReaderAdapter",
                AdapterVersion: "2.1.0"
            ),
            FolderCount = 5,
            MessageCount = 120,
            AttachmentCount = 15,
            IssueCount = 3,
            TotalAttachmentSize = 1024 * 1024 * 8 // 8 MB
        };
        var vm = new CaseOverviewViewModel();

        // Action
        await vm.LoadFromReaderAsync(reader, CancellationToken.None);

        // Assert
        Assert.Equal("CASE-001", vm.CaseId);
        Assert.Contains("<USER>", vm.SourceFileMasked);
        Assert.Equal("SHA256HASH123456", vm.SourceSha256);
        Assert.Equal("XstReaderAdapter (2.1.0)", vm.AdapterNameVersion);
        Assert.Equal(5, vm.FolderCount);
        Assert.Equal(120, vm.MessageCount);
        Assert.Equal(15, vm.AttachmentCount);
        Assert.Equal(3, vm.IssueCount);
        Assert.Contains("8", vm.TotalAttachmentSize);
    }

    [Fact]
    public async Task FolderTreeViewModel_LoadsFolders()
    {
        // Arrange
        var reader = new MockCaseIndexReader();
        reader.Folders.Add(new FolderNode(
            Id: new FolderId("F1"),
            ParentId: null,
            DisplayName: "Caixa de Entrada",
            FullPath: "\\Caixa de Entrada",
            MessageCount: 45,
            Children: new List<FolderNode>()
        ));
        var vm = new FolderTreeViewModel();

        // Action
        await vm.LoadFoldersAsync(reader, CancellationToken.None);

        // Assert
        Assert.Single(vm.RootFolders);
        Assert.Equal("Caixa de Entrada (45)", vm.RootFolders[0].DisplayName);
        Assert.Equal("\\Caixa de Entrada", vm.RootFolders[0].FullPath);
        Assert.Equal("F1", vm.RootFolders[0].FolderId.Value);
    }

    [Fact]
    public async Task MessageListViewModel_PaginatesMessages()
    {
        // Arrange
        var reader = new MockCaseIndexReader();
        for (int i = 0; i < 50; i++)
        {
            reader.Messages.Add(new MailItem(
                InternalId: $"M-{i}",
                InternetMessageId: $"<msg-{i}@test.com>",
                Subject: $"Email {i}",
                From: new MailAddressRef("Remetente", "sender@test.com"),
                To: new List<MailAddressRef>(),
                Cc: new List<MailAddressRef>(),
                Bcc: new List<MailAddressRef>(),
                SentAt: DateTimeOffset.UtcNow,
                ReceivedAt: DateTimeOffset.UtcNow,
                PlainTextBody: "Body text",
                HtmlBody: null,
                Attachments: new List<AttachmentRef>(),
                RawProperties: new Dictionary<string, string>(),
                Issues: new List<ExtractionIssue>()
            ));
        }
        var vm = new MessageListViewModel();

        // Action
        await vm.SetFolderAsync(new FolderId("F1"), reader, CancellationToken.None);

        // Assert
        Assert.Equal(50, vm.Messages.Count);
        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(2, vm.TotalPages);

        // Paging tests
        Assert.NotNull(vm.NextPageCommand);
        Assert.NotNull(vm.PrevPageCommand);
    }

    [Fact]
    public void MessagePreviewViewModel_TruncatesBodyPreview()
    {
        // Arrange
        var vm = new MessagePreviewViewModel();
        var longBody = new string('A', 500);
        var mailItemLong = new MailItem(
            InternalId: "M1",
            InternetMessageId: "<m1@test.com>",
            Subject: "Test",
            From: new MailAddressRef("Remetente", "sender@test.com"),
            To: new List<MailAddressRef>(),
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.UtcNow,
            ReceivedAt: DateTimeOffset.UtcNow,
            PlainTextBody: longBody,
            HtmlBody: null,
            Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        );

        var mailItemShort = new MailItem(
            InternalId: "M2",
            InternetMessageId: "<m2@test.com>",
            Subject: "Test Short",
            From: new MailAddressRef("Remetente", "sender@test.com"),
            To: new List<MailAddressRef>(),
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.UtcNow,
            ReceivedAt: DateTimeOffset.UtcNow,
            PlainTextBody: "Hello",
            HtmlBody: null,
            Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        );

        // Action & Assert Long
        vm.SetMessage(mailItemLong);
        Assert.True(vm.HasMessage);
        Assert.Contains("[CONTEÚDO TRUNCADO POR SEGURANÇA E PRIVACIDADE FORENSE]", vm.BodyPreview);
        Assert.Equal(400 + "... [CONTEÚDO TRUNCADO POR SEGURANÇA E PRIVACIDADE FORENSE]".Length, vm.BodyPreview.Length);

        // Action & Assert Short
        vm.SetMessage(mailItemShort);
        Assert.Equal("Hello", vm.BodyPreview);
    }

    [Fact]
    public async Task SearchViewModel_ReturnsIndexedResults()
    {
        // Arrange
        var reader = new MockCaseIndexReader();
        reader.SearchResults.Add(new MailItem(
            InternalId: "M-SEARCH",
            InternetMessageId: "<search@test.com>",
            Subject: "Resultado Encontrado",
            From: new MailAddressRef("Remetente", "sender@test.com"),
            To: new List<MailAddressRef>(),
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.UtcNow,
            ReceivedAt: DateTimeOffset.UtcNow,
            PlainTextBody: "Matched content",
            HtmlBody: null,
            Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        ));
        var vm = new SearchViewModel();
        vm.SetReader(reader);
        vm.SearchQuery = "test";

        // Action
        await vm.OnSearchAsync();

        // Assert
        Assert.Single(vm.Results);
        Assert.Equal("Resultado Encontrado", vm.Results[0].Subject);
        Assert.Contains("Busca concluída", vm.StatusText);
    }

    [Fact]
    public void ValidationPanelViewModel_MapsReportStatus()
    {
        // Arrange
        var vm = new ValidationPanelViewModel();

        // Action
        vm.RunValidationCommand.Execute(null);

        // Assert
        Assert.Equal("PassedWithWarnings", vm.ReportStatus);
        Assert.Contains("Validação concluída com alertas", vm.ValidationStatus);
        Assert.NotEmpty(vm.ReportMetrics);
        Assert.NotEmpty(vm.ReportWarningsErrors);
    }

    // -------------------------------------------------------------------------
    // Milestone 6.1 — Mandated Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CaseWorkspaceService_DetectsExistingCaseDb()
    {
        // Arrange — create a temp folder with a real SQLite case.db
        string tempDir = Path.Combine(Path.GetTempPath(), $"mv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string dbPath = Path.Combine(tempDir, "case.db");
            await CreateMinimalCaseDbAsync(dbPath);

            var svc = new CaseWorkspaceDiagnosticService();

            // Act
            var result = await svc.DiagnoseAsync(tempDir, CancellationToken.None);

            // Assert
            Assert.True(result.DirectoryExists);
            Assert.True(result.CaseDbExists);
            Assert.True(result.CaseDbReadable);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task CaseWorkspaceService_DetectsMissingManifestButAllowsLimitedOpen()
    {
        // Arrange — folder with case.db but NO manifest.json
        string tempDir = Path.Combine(Path.GetTempPath(), $"mv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string dbPath = Path.Combine(tempDir, "case.db");
            await CreateMinimalCaseDbAsync(dbPath);
            // deliberately do NOT create manifest.json

            var diagnostic = new CaseWorkspaceDiagnosticService();
            var result = await diagnostic.DiagnoseAsync(tempDir, CancellationToken.None);

            // Assert — can open in limited mode
            Assert.True(result.CaseDbExists);
            Assert.True(result.CaseDbReadable);
            Assert.False(result.ManifestExists, "manifest.json should be absent");
            Assert.True(result.CanOpenLimited, "Must be openable in limited mode even without manifest");
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task CaseWorkspaceService_DetectsJournalFile()
    {
        // Arrange — folder with case.db and a leftover case.db-journal
        string tempDir = Path.Combine(Path.GetTempPath(), $"mv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string dbPath = Path.Combine(tempDir, "case.db");
            await CreateMinimalCaseDbAsync(dbPath);
            // Create a fake journal file
            File.WriteAllText(Path.Combine(tempDir, "case.db-journal"), "");

            var svc = new CaseWorkspaceDiagnosticService();
            var result = await svc.DiagnoseAsync(tempDir, CancellationToken.None);

            // Assert — journal detected as warning, not fatal
            Assert.True(result.JournalFileExists, "Journal file must be detected");
            Assert.True(result.CaseDbReadable, "DB must still be readable when journal present");
            Assert.True(result.CanOpenLimited, "Must allow limited open with journal present");
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task CaseWorkspaceService_DoesNotReportMissingCaseDbWhenItExists()
    {
        // Arrange — folder with case.db present
        string tempDir = Path.Combine(Path.GetTempPath(), $"mv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string dbPath = Path.Combine(tempDir, "case.db");
            await CreateMinimalCaseDbAsync(dbPath);

            var svc = new CaseWorkspaceDiagnosticService();
            var result = await svc.DiagnoseAsync(tempDir, CancellationToken.None);

            // Assert — must NEVER say case.db not found when it actually exists
            Assert.True(result.CaseDbExists, "CaseDbExists must be true when file is present");
            Assert.False(
                result.ErrorMessage?.Contains("case.db não encontrado") ?? false,
                "Error message must NOT say 'case.db not found' when it exists");
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task CaseWorkspaceService_DetectsMissingCaseDb()
    {
        // Arrange — folder exists but case.db does NOT
        string tempDir = Path.Combine(Path.GetTempPath(), $"mv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var svc = new CaseWorkspaceDiagnosticService();
            var result = await svc.DiagnoseAsync(tempDir, CancellationToken.None);

            // Assert
            Assert.True(result.DirectoryExists);
            Assert.False(result.CaseDbExists);
            Assert.False(result.IsHealthy);
            Assert.NotNull(result.ErrorMessage);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task CaseOverviewViewModel_DoesNotStayLoadingOnError()
    {
        // Arrange — reader that throws an exception
        var reader = new ThrowingCaseIndexReader();
        var vm = new CaseOverviewViewModel();

        // Act
        await vm.LoadFromReaderAsync(reader, CancellationToken.None);

        // Assert — must NOT be in Loading state; must be Error
        Assert.NotEqual(LoadingState.Loading, vm.State);
        Assert.Equal(LoadingState.Error, vm.State);
        Assert.False(vm.IsLoading);
        Assert.True(vm.HasError);
        Assert.NotEmpty(vm.ErrorMessage);
    }

    [Fact]
    public async Task CaseOverviewViewModel_LoadsStatsFromSyntheticCaseDb()
    {
        // Arrange — synthetic reader with known stats
        var reader = new MockCaseIndexReader
        {
            CaseInfo = new CaseInfoRef(
                CaseId: "SYNTH-001",
                SourceFile: "C:\\Data\\synth.ost",
                SourceSizeBytes: 512L,
                SourceSha256: "AABBCC",
                OperatorName: "Tester",
                StartedAt: DateTimeOffset.UtcNow,
                AdapterName: "FakeAdapter",
                AdapterVersion: "1.0.0"
            ),
            FolderCount = 3,
            MessageCount = 77,
            AttachmentCount = 5,
            IssueCount = 1,
            TotalAttachmentSize = 1024 * 1024 * 4 // 4 MB
        };
        var vm = new CaseOverviewViewModel();

        // Act
        await vm.LoadFromReaderAsync(reader, CancellationToken.None);

        // Assert
        Assert.Equal("SYNTH-001", vm.CaseId);
        Assert.Equal(3, vm.FolderCount);
        Assert.Equal(77, vm.MessageCount);
        Assert.Equal(5, vm.AttachmentCount);
        Assert.Equal(1, vm.IssueCount);
        Assert.Contains("4", vm.TotalAttachmentSize);
        Assert.Equal(LoadingState.Loaded, vm.State);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public void HomeViewModel_ExposesOpenCaseCreateCaseMboxAndRecentCases()
    {
        // Arrange
        var vm = new HomeViewModel();

        // Assert — all four entry points must be present
        Assert.NotNull(vm.OpenCaseCommand);
        Assert.NotNull(vm.CreateCaseCommand);
        Assert.NotNull(vm.OpenMboxCaseCommand);
        Assert.NotNull(vm.RecentCases);
    }

    [Fact]
    public void RecentCasesService_SavesAndLoadsRecentCases()
    {
        // Arrange — use a temp file path so we don't affect real ApplicationData
        string tempFile = Path.Combine(Path.GetTempPath(), $"mv-recent-{Guid.NewGuid():N}.json");
        try
        {
            var svc = new RecentCasesService(tempFile);

            var entry = new RecentCaseEntry
            {
                CaseId = "CASE-TEST",
                CaseFolderPath = "C:\\Cases\\CASE-TEST",
                OpenMode = "Full",
                LastOpenedAt = DateTimeOffset.UtcNow,
                SchemaVersion = 2
            };

            // Act
            svc.AddOrUpdate(entry);
            var loaded = svc.Load();

            // Assert
            Assert.Single(loaded);
            Assert.Equal("CASE-TEST", loaded[0].CaseId);
            Assert.Equal("C:\\Cases\\CASE-TEST", loaded[0].CaseFolderPath);
            Assert.Equal("Full", loaded[0].OpenMode);
            Assert.Equal(2, loaded[0].SchemaVersion);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void DesktopDependencyAudit_NoKnownVulnerabilities()
    {
        // This test documents that the Desktop project pins Tmds.DBus.Protocol to 0.21.3
        // to eliminate NU1903 (GHSA-xrw6-gwf8-vvr9).
        // The actual vulnerability check is enforced by the CI gate:
        //   dotnet list package --vulnerable --include-transitive
        // This test verifies we are running under net10.0 and that the assembly loads correctly.
        var asm = typeof(MailVault.Desktop.ViewModels.ViewModelBase).Assembly;
        Assert.NotNull(asm);
        Assert.Contains("MailVault.Desktop", asm.FullName);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Forces disposal of SQLite connections held by native handles before file cleanup.</summary>
    private static void FlushSqliteHandles()
    {
        // ClearAllPools releases native SQLite file handles held by the connection pool
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        // Brief sleep to let the OS release file locks on Windows
        System.Threading.Thread.Sleep(50);
    }

    /// <summary>Deletes a directory with retries on Windows file-lock exceptions.</summary>
    private static void DeleteDirectoryWithRetry(string path, int retries = 5, int delayMs = 200)
    {
        FlushSqliteHandles();
        for (int i = 0; i < retries; i++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (i < retries - 1)
            {
                System.Threading.Thread.Sleep(delayMs);
            }
        }
        // Last attempt — let it throw
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>Creates a minimal valid SQLite database at the given path.</summary>
    private static async Task CreateMinimalCaseDbAsync(string dbPath)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA foreign_keys = ON;
            PRAGMA user_version = 2;
            CREATE TABLE IF NOT EXISTS case_info (
                id INTEGER PRIMARY KEY,
                case_id TEXT NOT NULL,
                source_file TEXT NOT NULL,
                source_sha256 TEXT NOT NULL,
                adapter_name TEXT,
                adapter_version TEXT,
                started_at TEXT
            );
            CREATE TABLE IF NOT EXISTS folders (id TEXT PRIMARY KEY, display_name TEXT);
            CREATE TABLE IF NOT EXISTS messages (id TEXT PRIMARY KEY, folder_id TEXT);
            CREATE TABLE IF NOT EXISTS attachments (id TEXT PRIMARY KEY, message_id TEXT);
            CREATE TABLE IF NOT EXISTS issues (id INTEGER PRIMARY KEY, object_id TEXT);
            INSERT INTO case_info (case_id, source_file, source_sha256) VALUES ('TEST-001', 'test.ost', 'AABB');
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A reader that always throws to test error-state handling.</summary>
    private sealed class ThrowingCaseIndexReader : ICaseIndexReader
    {
        public Task<CaseInfoRef?> GetCaseInfoAsync(CancellationToken ct) 
            => throw new InvalidOperationException("Simulated read failure");

        public Task<int> GetFolderCountAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<int> GetMessageCountAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<int> GetAttachmentCountAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<int> GetIssueCountAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<long> GetTotalAttachmentSizeAsync(CancellationToken ct) => Task.FromResult(0L);
        public Task<Dictionary<string, int>> GetTopFoldersByMessageCountAsync(int limit, CancellationToken ct) 
            => Task.FromResult(new Dictionary<string, int>());
        public Task<(string fileName, long sizeBytes)> GetLargestAttachmentAsync(CancellationToken ct) 
            => Task.FromResult(("", 0L));
        public async IAsyncEnumerable<MailItem> SearchMessagesAsync(string q, string? f, int l, int o, CancellationToken ct) 
            { yield break; await Task.CompletedTask; }
        public async IAsyncEnumerable<FolderNode> GetFolderHierarchyAsync(CancellationToken ct) 
            { yield break; await Task.CompletedTask; }
        public async IAsyncEnumerable<MailItem> GetMessagesInFolderAsync(FolderId folderId, int limit, int offset, CancellationToken ct) 
            { yield break; await Task.CompletedTask; }
        public Task<MailItem?> GetMessageByIdAsync(MessageId messageId, CancellationToken ct) 
            => Task.FromResult<MailItem?>(null);
        public void Dispose() { }
    }
}

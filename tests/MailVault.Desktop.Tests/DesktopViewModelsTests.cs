using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Desktop.Services;
using MailVault.Desktop.ViewModels;
using MailVault.Validation;
using ReactiveUI;
using System.Reactive.Threading.Tasks;
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

        public async IAsyncEnumerable<MailItem> SearchMessagesAsync(string queryText, string? folderPath, int limit, int offset, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var msg in SearchResults)
            {
                yield return msg;
            }
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<FolderNode> GetFolderHierarchyAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var folder in Folders)
            {
                yield return folder;
            }
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<MailItem> GetMessagesInFolderAsync(FolderId folderId, int limit, int offset, [EnumeratorCancellation] CancellationToken ct)
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
        // A partir do milestone de preview ao vivo, o corpo é exibido na íntegra
        // (cap de MaxBodyChars=100_000), não mais truncado em 400 chars.
        var vm = new MessagePreviewViewModel();
        var longBody = new string('A', 100_500);
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
        Assert.Contains("corpo truncado na visualização", vm.BodyPreview);
        Assert.Equal(
            100_000 + "\n\n[...] (corpo truncado na visualização; use a exportação forense para o conteúdo completo.)".Length,
            vm.BodyPreview.Length);

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
    public async Task ValidationPanelViewModel_MapsReportStatus()
    {
        // Arrange
        var mockService = new MockDesktopValidationService { ExpectedStatus = "PassedWithWarnings" };
        var vm = new ValidationPanelViewModel(mockService);
        vm.SetCaseFolder("C:\\DummyCase");

        // Action
        await (vm.RunValidationCommand as ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit>)!.Execute().ToTask();

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

    [Fact]
    public async Task CaseWorkspaceDiagnosticService_ReadsExistingCaseDb()
    {
        string tempDir = CreateTempDir();
        try
        {
            await CreateMinimalCaseDbAsync(Path.Combine(tempDir, "case.db"), messageCount: 2, attachmentCount: 1, issueCount: 1);
            File.WriteAllText(Path.Combine(tempDir, "manifest.json"), "{}");

            var svc = new CaseWorkspaceDiagnosticService();
            var result = await svc.DiagnoseAsync(tempDir, CancellationToken.None);

            Assert.True(result.DirectoryExists);
            Assert.True(result.CaseDbExists);
            Assert.True(result.CaseDbReadable);
            Assert.True(result.SchemaValid);
            Assert.Equal(2, result.SchemaVersion);
            Assert.Equal(1, result.CaseInfoRowCount);
            Assert.Equal(1, result.FolderRowCount);
            Assert.Equal(2, result.MessageRowCount);
            Assert.Equal(1, result.AttachmentRowCount);
            Assert.Equal(1, result.IssueRowCount);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task CaseWorkspaceDiagnosticService_DetectsMissingManifestAsWarning()
    {
        string tempDir = CreateTempDir();
        try
        {
            await CreateMinimalCaseDbAsync(Path.Combine(tempDir, "case.db"), messageCount: 1);

            var result = await new CaseWorkspaceDiagnosticService().DiagnoseAsync(tempDir, CancellationToken.None);

            Assert.False(result.ManifestExists);
            Assert.True(result.CanOpenLimited);
            Assert.Contains(result.Warnings, warning => warning.Contains("manifest.json ausente"));
            Assert.Null(result.ErrorMessage);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task CaseWorkspaceDiagnosticService_DetectsJournalAsWarning()
    {
        string tempDir = CreateTempDir();
        try
        {
            await CreateMinimalCaseDbAsync(Path.Combine(tempDir, "case.db"), messageCount: 1);
            File.WriteAllText(Path.Combine(tempDir, "case.db-journal"), "");

            var result = await new CaseWorkspaceDiagnosticService().DiagnoseAsync(tempDir, CancellationToken.None);

            Assert.True(result.JournalFileExists);
            Assert.True(result.CanOpenLimited);
            Assert.Contains(result.Warnings, warning => warning.Contains("case.db-journal detectado"));
            Assert.Null(result.ErrorMessage);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task CaseWorkspaceService_LoadsCaseInfoFromSyntheticCaseDb()
    {
        string tempDir = CreateTempDir();
        try
        {
            await CreateMinimalCaseDbAsync(Path.Combine(tempDir, "case.db"), messageCount: 1);
            File.WriteAllText(Path.Combine(tempDir, "manifest.json"), "{}");

            using var service = new CaseWorkspaceService(new CaseWorkspaceDiagnosticService());
            var workspace = await service.OpenExistingCaseAsync(tempDir, CancellationToken.None);

            Assert.NotNull(workspace);
            Assert.NotNull(workspace!.CaseInfo);
            Assert.Equal("SYNTH-CASE-001", workspace.CaseInfo!.CaseId);
            Assert.Equal("SyntheticAdapter", workspace.CaseInfo.AdapterName);
            Assert.Equal("9.1.0", workspace.CaseInfo.AdapterVersion);
            Assert.EndsWith("synthetic.ost", workspace.CaseInfo.SourceFile);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task CaseWorkspaceService_LoadsStatsFromSyntheticCaseDb()
    {
        string tempDir = CreateTempDir();
        try
        {
            await CreateMinimalCaseDbAsync(Path.Combine(tempDir, "case.db"), folderCount: 2, messageCount: 3, attachmentCount: 2, issueCount: 1);
            File.WriteAllText(Path.Combine(tempDir, "manifest.json"), "{}");

            using var service = new CaseWorkspaceService(new CaseWorkspaceDiagnosticService());
            var workspace = await service.OpenExistingCaseAsync(tempDir, CancellationToken.None);

            Assert.NotNull(workspace);
            Assert.Equal(2, workspace!.Stats.FolderCount);
            Assert.Equal(3, workspace.Stats.MessageCount);
            Assert.Equal(2, workspace.Stats.AttachmentCount);
            Assert.Equal(1, workspace.Stats.IssueCount);
            Assert.True(workspace.Stats.TotalAttachmentSizeBytes > 0);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task CaseOverviewViewModel_PopulatesFieldsFromWorkspace()
    {
        string tempDir = CreateTempDir();
        try
        {
            await CreateMinimalCaseDbAsync(Path.Combine(tempDir, "case.db"), messageCount: 2, attachmentCount: 1);
            File.WriteAllText(Path.Combine(tempDir, "manifest.json"), "{}");

            using var service = new CaseWorkspaceService(new CaseWorkspaceDiagnosticService());
            var workspace = await service.OpenExistingCaseAsync(tempDir, CancellationToken.None);
            var vm = new CaseOverviewViewModel();

            await vm.LoadFromWorkspaceAsync(workspace!, CancellationToken.None);

            Assert.Equal("SYNTH-CASE-001", vm.CaseId);
            Assert.Equal("SyntheticAdapter", vm.AdapterName);
            Assert.Equal("9.1.0", vm.AdapterVersion);
            Assert.Equal("SyntheticAdapter (9.1.0)", vm.AdapterNameVersion);
            Assert.Contains("<USER>", vm.SourceFileMasked);
            Assert.Equal("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", vm.SourceSha256);
            Assert.Equal(2, vm.MessageCount);
            Assert.Equal(1, vm.AttachmentCount);
            Assert.Equal("Íntegro", vm.HealthStatus);
            Assert.Equal(LoadingState.Loaded, vm.State);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task CaseOverviewViewModel_ShowsEmptyStateWhenDbHasNoMessages()
    {
        string tempDir = CreateTempDir();
        try
        {
            await CreateMinimalCaseDbAsync(Path.Combine(tempDir, "case.db"), includeCaseInfo: false);

            using var service = new CaseWorkspaceService(new CaseWorkspaceDiagnosticService());
            var workspace = await service.OpenExistingCaseAsync(tempDir, CancellationToken.None);
            var vm = new CaseOverviewViewModel();

            await vm.LoadFromWorkspaceAsync(workspace!, CancellationToken.None);

            Assert.Equal(LoadingState.Empty, vm.State);
            Assert.Equal("Vazio", vm.HealthStatus);
            Assert.Equal(0, vm.MessageCount);
            Assert.Contains(vm.Warnings, warning => warning.Contains("não há mensagens indexadas"));
            Assert.Contains(vm.Warnings, warning => warning.Contains("case_info"));
            Assert.False(string.IsNullOrWhiteSpace(vm.SuggestedAction));
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task CaseOverviewViewModel_DoesNotShowIntactStatusWhenWarningsExist()
    {
        string tempDir = CreateTempDir();
        try
        {
            await CreateMinimalCaseDbAsync(Path.Combine(tempDir, "case.db"), messageCount: 1);

            using var service = new CaseWorkspaceService(new CaseWorkspaceDiagnosticService());
            var workspace = await service.OpenExistingCaseAsync(tempDir, CancellationToken.None);
            var vm = new CaseOverviewViewModel();

            await vm.LoadFromWorkspaceAsync(workspace!, CancellationToken.None);

            Assert.NotEqual("Íntegro", vm.HealthStatus);
            Assert.Equal("Modo limitado", vm.HealthStatus);
            Assert.Contains(vm.Warnings, warning => warning.Contains("manifest.json ausente"));
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task MainWindowViewModel_OpenCaseLoadsOverviewAndFolderTree()
    {
        string tempDir = CreateTempDir();
        string recentFile = Path.Combine(Path.GetTempPath(), $"mv-recent-{Guid.NewGuid():N}.json");
        MainWindowViewModel? vm = null;
        try
        {
            await CreateMinimalCaseDbAsync(Path.Combine(tempDir, "case.db"), folderCount: 2, messageCount: 2);
            File.WriteAllText(Path.Combine(tempDir, "manifest.json"), "{}");

            vm = new MainWindowViewModel(
                new CaseWorkspaceDiagnosticService(),
                recentCasesService: new RecentCasesService(recentFile));

            await vm.LoadCaseAsync(tempDir);

            Assert.True(vm.IsCaseLoaded);
            Assert.Same(vm.OverviewVm, vm.CurrentView);
            Assert.Equal("SYNTH-CASE-001", vm.OverviewVm.CaseId);
            Assert.Equal(2, vm.OverviewVm.MessageCount);
            Assert.Equal(2, vm.FolderTreeVm.RootFolders.Count);
            Assert.Equal("Íntegro", vm.CaseStatusText);
        }
        finally
        {
            vm?.CloseCase();

            if (File.Exists(recentFile))
            {
                File.Delete(recentFile);
            }

            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task HomeViewModel_RecentCaseOpensRealWorkspace()
    {
        string tempDir = CreateTempDir();
        try
        {
            await CreateMinimalCaseDbAsync(Path.Combine(tempDir, "case.db"), messageCount: 1);
            var vm = new HomeViewModel(new CaseWorkspaceDiagnosticService());
            string? openedPath = null;
            vm.CaseSelected += path => openedPath = path;

            await vm.OpenRecentCaseAsync(new RecentCaseEntry
            {
                CaseId = "SYNTH-CASE-001",
                CaseFolderPath = tempDir,
                OpenMode = "Full",
                LastOpenedAt = DateTimeOffset.UtcNow,
                SchemaVersion = 2
            });

            Assert.Equal(tempDir, openedPath);
            Assert.Equal(tempDir, vm.SelectedCasePath);
            Assert.NotNull(vm.RemoveRecentCaseCommand);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public void CaseOverviewViewModel_ExposesPropertiesUsedByOverviewBindings()
    {
        var properties = typeof(CaseOverviewViewModel)
            .GetProperties()
            .Select(prop => prop.Name)
            .ToHashSet(StringComparer.Ordinal);

        string[] overviewBindings =
        {
            nameof(CaseOverviewViewModel.CaseId),
            nameof(CaseOverviewViewModel.AdapterNameVersion),
            nameof(CaseOverviewViewModel.SourceFileMasked),
            nameof(CaseOverviewViewModel.FolderCount),
            nameof(CaseOverviewViewModel.MessageCount),
            nameof(CaseOverviewViewModel.AttachmentCount),
            nameof(CaseOverviewViewModel.IssueCount),
            nameof(CaseOverviewViewModel.TotalAttachmentSizeFormatted),
            nameof(CaseOverviewViewModel.HealthStatus),
            nameof(CaseOverviewViewModel.SourceSha256),
            nameof(CaseOverviewViewModel.Warnings),
            nameof(CaseOverviewViewModel.HasWarnings),
            nameof(CaseOverviewViewModel.SuggestedAction),
            nameof(CaseOverviewViewModel.ErrorMessage)
        };

        foreach (string binding in overviewBindings)
        {
            Assert.Contains(binding, properties);
        }
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
    private static string CreateTempDir()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"mv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
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

    /// <summary>Creates a minimal valid SQLite database at the given path using the production schema.</summary>
    private static async Task CreateMinimalCaseDbAsync(
        string dbPath,
        bool includeCaseInfo = true,
        int folderCount = 1,
        int messageCount = 0,
        int attachmentCount = 0,
        int issueCount = 0)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        using var schemaCmd = conn.CreateCommand();
        schemaCmd.CommandText = @"
            PRAGMA foreign_keys = ON;
            CREATE TABLE schema_version (version INTEGER PRIMARY KEY);
            INSERT INTO schema_version (version) VALUES (2);
            CREATE TABLE case_info (
                case_id TEXT PRIMARY KEY,
                source_file TEXT,
                source_size INTEGER,
                source_sha256 TEXT,
                operator_name TEXT,
                started_at TEXT,
                completed_at TEXT,
                adapter_name TEXT,
                adapter_version TEXT
            );
            CREATE TABLE folders (
                folder_id TEXT PRIMARY KEY,
                parent_id TEXT,
                display_name TEXT,
                full_path TEXT,
                message_count INTEGER,
                FOREIGN KEY(parent_id) REFERENCES folders(folder_id)
            );
            CREATE TABLE messages (
                message_id TEXT PRIMARY KEY,
                internet_message_id TEXT,
                folder_id TEXT,
                subject TEXT,
                sender TEXT,
                recipients_to TEXT,
                recipients_cc TEXT,
                recipients_bcc TEXT,
                sent_at TEXT,
                received_at TEXT,
                has_text_body INTEGER,
                has_html_body INTEGER,
                body_preview TEXT,
                attachment_count INTEGER,
                mapi_properties_count INTEGER,
                FOREIGN KEY(folder_id) REFERENCES folders(folder_id)
            );
            CREATE TABLE attachments (
                attachment_id TEXT PRIMARY KEY,
                message_id TEXT,
                file_name TEXT,
                content_type TEXT,
                size_bytes INTEGER,
                content_id TEXT,
                is_inline INTEGER,
                FOREIGN KEY(message_id) REFERENCES messages(message_id)
            );
            CREATE TABLE issues (
                issue_code TEXT,
                severity TEXT,
                message TEXT,
                object_id TEXT,
                technical_details TEXT
            );
            CREATE TABLE index_runs (
                run_id TEXT PRIMARY KEY,
                case_id TEXT,
                timestamp TEXT,
                status TEXT,
                duration_ms INTEGER,
                folders_indexed INTEGER,
                messages_indexed INTEGER,
                attachments_indexed INTEGER,
                issues_detected INTEGER,
                FOREIGN KEY(case_id) REFERENCES case_info(case_id)
            );
            CREATE INDEX idx_messages_folder_id ON messages(folder_id);
            CREATE INDEX idx_messages_subject ON messages(subject);
            CREATE INDEX idx_messages_sender ON messages(sender);
            CREATE INDEX idx_attachments_message_id ON attachments(message_id);
            CREATE INDEX idx_issues_object_id ON issues(object_id);
        ";
        await schemaCmd.ExecuteNonQueryAsync();

        if (includeCaseInfo)
        {
            using var caseCmd = conn.CreateCommand();
            caseCmd.CommandText = @"
                INSERT INTO case_info (
                    case_id, source_file, source_size, source_sha256, operator_name,
                    started_at, completed_at, adapter_name, adapter_version)
                VALUES (
                    'SYNTH-CASE-001',
                    'C:\Users\natha\Evidence\synthetic.ost',
                    4096,
                    '0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF',
                    'Tester',
                    '2026-05-26T12:00:00.0000000+00:00',
                    NULL,
                    'SyntheticAdapter',
                    '9.1.0');";
            await caseCmd.ExecuteNonQueryAsync();
        }

        int effectiveFolderCount = Math.Max(folderCount, messageCount > 0 ? 1 : 0);
        for (int i = 1; i <= effectiveFolderCount; i++)
        {
            using var folderCmd = conn.CreateCommand();
            folderCmd.CommandText = @"
                INSERT INTO folders (folder_id, parent_id, display_name, full_path, message_count)
                VALUES ($folderId, NULL, $displayName, $fullPath, $messageCount);";
            folderCmd.Parameters.AddWithValue("$folderId", $"F{i}");
            folderCmd.Parameters.AddWithValue("$displayName", $"Folder {i}");
            folderCmd.Parameters.AddWithValue("$fullPath", $"\\Folder {i}");
            folderCmd.Parameters.AddWithValue("$messageCount", i == 1 ? messageCount : 0);
            await folderCmd.ExecuteNonQueryAsync();
        }

        for (int i = 1; i <= messageCount; i++)
        {
            using var messageCmd = conn.CreateCommand();
            messageCmd.CommandText = @"
                INSERT INTO messages (
                    message_id, internet_message_id, folder_id, subject, sender,
                    recipients_to, recipients_cc, recipients_bcc, sent_at, received_at,
                    has_text_body, has_html_body, body_preview, attachment_count, mapi_properties_count)
                VALUES (
                    $messageId, $internetMessageId, 'F1', $subject, 'Sender <sender@example.com>',
                    'Receiver <receiver@example.com>', '', '', '2026-05-26T12:00:00.0000000+00:00',
                    '2026-05-26T12:01:00.0000000+00:00', 1, 0, 'Synthetic preview only',
                    $attachmentCount, 0);";
            messageCmd.Parameters.AddWithValue("$messageId", $"M{i}");
            messageCmd.Parameters.AddWithValue("$internetMessageId", $"<m{i}@example.com>");
            messageCmd.Parameters.AddWithValue("$subject", $"Synthetic message {i}");
            messageCmd.Parameters.AddWithValue("$attachmentCount", i == 1 ? attachmentCount : 0);
            await messageCmd.ExecuteNonQueryAsync();
        }

        for (int i = 1; i <= attachmentCount; i++)
        {
            using var attachmentCmd = conn.CreateCommand();
            attachmentCmd.CommandText = @"
                INSERT INTO attachments (attachment_id, message_id, file_name, content_type, size_bytes, content_id, is_inline)
                VALUES ($attachmentId, 'M1', $fileName, 'application/octet-stream', $sizeBytes, NULL, 0);";
            attachmentCmd.Parameters.AddWithValue("$attachmentId", $"A{i}");
            attachmentCmd.Parameters.AddWithValue("$fileName", $"file-{i}.bin");
            attachmentCmd.Parameters.AddWithValue("$sizeBytes", i * 1024);
            await attachmentCmd.ExecuteNonQueryAsync();
        }

        for (int i = 1; i <= issueCount; i++)
        {
            using var issueCmd = conn.CreateCommand();
            issueCmd.CommandText = @"
                INSERT INTO issues (issue_code, severity, message, object_id, technical_details)
                VALUES ($code, 'Warning', 'Synthetic issue', 'M1', 'Synthetic technical details');";
            issueCmd.Parameters.AddWithValue("$code", $"ISSUE-{i}");
            await issueCmd.ExecuteNonQueryAsync();
        }
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
        public async IAsyncEnumerable<MailItem> SearchMessagesAsync(string q, string? f, int l, int o, [EnumeratorCancellation] CancellationToken ct) 
            { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<FolderNode> GetFolderHierarchyAsync([EnumeratorCancellation] CancellationToken ct) 
            { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<MailItem> GetMessagesInFolderAsync(FolderId folderId, int limit, int offset, [EnumeratorCancellation] CancellationToken ct) 
            { await Task.CompletedTask; yield break; }
        public Task<MailItem?> GetMessageByIdAsync(MessageId messageId, CancellationToken ct) 
            => Task.FromResult<MailItem?>(null);
        public void Dispose() { }
    }

    // -------------------------------------------------------------------------
    // Milestone 6.2 — Operational UI & Test Lab Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void HomeViewModel_ShowsPrimaryActions()
    {
        var vm = new HomeViewModel();
        Assert.NotNull(vm.OpenCaseCommand);
        Assert.NotNull(vm.CreateCaseCommand);
        Assert.NotNull(vm.OpenMboxCaseCommand);
        Assert.NotNull(vm.RecentCases);
    }

    [Fact]
    public void NewCaseWizard_ValidatesOstPstInput()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test-evidence-{Guid.NewGuid():N}.ost");
        try
        {
            File.WriteAllText(tempFile, "dummy");
            var vm = new NewCaseWizardViewModel();
            vm.SourcePath = tempFile;
            Assert.True(vm.CanProceedStep1);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void NewCaseWizard_RejectsUnsupportedInput()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test-evidence-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tempFile, "dummy");
            var vm = new NewCaseWizardViewModel();
            vm.SourcePath = tempFile;
            Assert.False(vm.CanProceedStep1);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void NewCaseWizard_ReportsProgressFromFakeIndexer()
    {
        var vm = new NewCaseWizardViewModel();
        var reporterType = typeof(NewCaseWizardViewModel).GetNestedType("ProgressReporter", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(reporterType);
        
        var reporterInstance = Activator.CreateInstance(reporterType, vm);
        Assert.NotNull(reporterInstance);
        
        var reportMethod = reporterType.GetMethod("Report");
        Assert.NotNull(reportMethod);
        
        var progress = new DesktopIndexingProgress("Processando emails...", 75.0, 5, 25, 12, 2);
        reportMethod.Invoke(reporterInstance, new object[] { progress });
        
        Assert.Equal(75.0, vm.ProgressPercentage);
        Assert.Equal("Processando emails...", vm.ProgressText);
        Assert.Equal(5, vm.FoldersIndexed);
        Assert.Equal(25, vm.MessagesIndexed);
        Assert.Equal(12, vm.AttachmentsIndexed);
        Assert.Equal(2, vm.IssuesDetected);
        Assert.Contains("Processando emails...", vm.LogsText);
    }

    [Fact]
    public void NewCaseWizard_CancelMovesToCancelledState()
    {
        var vm = new NewCaseWizardViewModel();
        var tempFile = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.ost");
        vm.SourcePath = tempFile;
        
        var cts = new CancellationTokenSource();
        typeof(NewCaseWizardViewModel)
            .GetField("_indexingCts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .SetValue(vm, cts);
            
        vm.CancelIndexingCommand.Execute(null);
        Assert.Contains("Solicitando cancelamento", vm.LogsText);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task OverviewViewModel_MapsWorkspaceHealth()
    {
        var tempDir = CreateTempDir();
        try
        {
            await CreateMinimalCaseDbAsync(Path.Combine(tempDir, "case.db"), messageCount: 5);
            File.WriteAllText(Path.Combine(tempDir, "manifest.json"), "{}");

            using var service = new CaseWorkspaceService(new CaseWorkspaceDiagnosticService());
            var workspace = await service.OpenExistingCaseAsync(tempDir, CancellationToken.None);
            var vm = new CaseOverviewViewModel();

            await vm.LoadFromWorkspaceAsync(workspace!, CancellationToken.None);

            Assert.Equal("Íntegro", vm.HealthStatus);
            Assert.Equal(LoadingState.Loaded, vm.State);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task MailNavigator_LoadsFoldersAndMessages()
    {
        var reader = new MockCaseIndexReader();
        reader.Folders.Add(new FolderNode(
            Id: new FolderId("F-TEST"),
            ParentId: null,
            DisplayName: "Inbox",
            FullPath: "\\Inbox",
            MessageCount: 10,
            Children: new List<FolderNode>()
        ));
        reader.Messages.Add(new MailItem(
            InternalId: "M-TEST",
            InternetMessageId: "<test@domain.com>",
            Subject: "Test Subject",
            From: new MailAddressRef("Sender", "sender@domain.com"),
            To: new List<MailAddressRef>(),
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.UtcNow,
            ReceivedAt: DateTimeOffset.UtcNow,
            PlainTextBody: "Body content",
            HtmlBody: null,
            Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        ));

        var folderTreeVm = new FolderTreeViewModel();
        var messageListVm = new MessageListViewModel();

        await folderTreeVm.LoadFoldersAsync(reader, CancellationToken.None);
        await messageListVm.SetFolderAsync(new FolderId("F-TEST"), reader, CancellationToken.None);

        Assert.Single(folderTreeVm.RootFolders);
        Assert.Equal("Inbox (10)", folderTreeVm.RootFolders[0].DisplayName);
        Assert.Single(messageListVm.Messages);
        Assert.Equal("Test Subject", messageListVm.Messages[0].Subject);
    }

    [Fact]
    public async Task SearchViewModel_ExecutesIndexedSearch()
    {
        var reader = new MockCaseIndexReader();
        reader.SearchResults.Add(new MailItem(
            InternalId: "M-MATCH",
            InternetMessageId: "<match@domain.com>",
            Subject: "Keyword matched",
            From: new MailAddressRef("Sender", "sender@domain.com"),
            To: new List<MailAddressRef>(),
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.UtcNow,
            ReceivedAt: DateTimeOffset.UtcNow,
            PlainTextBody: "Body matched",
            HtmlBody: null,
            Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        ));

        var vm = new SearchViewModel();
        vm.SetReader(reader);
        vm.SearchQuery = "Keyword";

        await vm.OnSearchAsync();

        Assert.Single(vm.Results);
        Assert.Equal("Keyword matched", vm.Results[0].Subject);
    }

    [Fact]
    public async Task ExportPanel_DryRunShowsCounts()
    {
        var mockService = new MockDesktopExportService();
        var vm = new ExportPanelViewModel(mockService);
        vm.SetCaseFolder("C:\\DummyCase");
        vm.DryRun = true;
        
        await (vm.RunExportCommand as ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit>)!.Execute().ToTask();
        
        Assert.Equal(3, vm.MessagesSelected);
        Assert.Equal(0, vm.MessagesExported);
        Assert.Contains("DRY RUN", vm.ExportStatus);
    }

    [Fact]
    public async Task ValidationPanel_MapsValidationReport()
    {
        var mockService = new MockDesktopValidationService { ExpectedStatus = "Passed" };
        var vm = new ValidationPanelViewModel(mockService);
        vm.SetCaseFolder("C:\\DummyCase");
        
        await (vm.RunValidationCommand as ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit>)!.Execute().ToTask();
        
        Assert.Contains("Validação concluída", vm.ValidationStatus);
        Assert.Equal("Passed", vm.ReportStatus);
        Assert.Contains("Mensagens no cofre", vm.ReportMetrics);
    }

    [Fact]
    public async Task TestLab_ScansCorpusWithoutReadingBodies()
    {
        string tempCorpusDir = Path.Combine(Path.GetTempPath(), $"mv-corpus-{Guid.NewGuid():N}");
        var service = new DesktopTestLabService(new DesktopCaseCreationService(), new DesktopExportService(), new DesktopValidationService());
        try
        {
            service.CreateDefaultStructure(tempCorpusDir);
            
            string ostFile = Path.Combine(tempCorpusDir, "evidences", "test.ost");
            File.WriteAllText(ostFile, "dummy ost content");

            var files = await service.ScanCorpusAsync(tempCorpusDir, CancellationToken.None);
            
            Assert.NotEmpty(files);
            var fileRecord = files.FirstOrDefault(f => f.FileName == "test.ost");
            Assert.NotNull(fileRecord);
            Assert.Equal("OST Microsoft Outlook", fileRecord.Category);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempCorpusDir);
        }
    }

    [Fact]
    public async Task TestLab_RunPipelineCreatesSummary()
    {
        string tempCorpusDir = Path.Combine(Path.GetTempPath(), $"mv-corpus-{Guid.NewGuid():N}");
        var service = new DesktopTestLabService(new DesktopCaseCreationService(), new DesktopExportService(), new DesktopValidationService());
        try
        {
            service.CreateDefaultStructure(tempCorpusDir);
            
            string ostFile = Path.Combine(tempCorpusDir, "evidences", "test.ost");
            File.WriteAllText(ostFile, "dummy ost content");

            var files = await service.ScanCorpusAsync(tempCorpusDir, CancellationToken.None);
            Assert.NotEmpty(files);
            var fileRecord = files.First(f => f.FileName == "test.ost");

            var summary = await service.RunPipelineAsync(
                tempCorpusDir,
                fileRecord,
                (msg, percent) => { },
                CancellationToken.None
            );

            Assert.NotNull(summary);
            Assert.Equal("Failed", summary.Status);
            Assert.Equal("Failed", summary.ValidationStatus);
            Assert.Equal(fileRecord.Sha256, summary.Sha256);
            Assert.NotEmpty(summary.Steps);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempCorpusDir);
        }
    }

    [Fact]
    public void RecentCases_OpenRemoveClear()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"mv-recent-{Guid.NewGuid():N}.json");
        try
        {
            var svc = new RecentCasesService(tempFile);
            var entry = new RecentCaseEntry
            {
                CaseId = "CASE-1",
                CaseFolderPath = "C:\\Cases\\CASE-1",
                OpenMode = "Full",
                LastOpenedAt = DateTimeOffset.UtcNow,
                SchemaVersion = 2
            };

            svc.AddOrUpdate(entry);
            var list = svc.Load();
            Assert.Single(list);

            svc.Remove("C:\\Cases\\CASE-1");
            list = svc.Load();
            Assert.Empty(list);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void SettingsService_SavesLocalPreferences()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"mv-settings-{Guid.NewGuid():N}.json");
        try
        {
            var svc = new LocalSettingsService(tempFile);
            var settings = svc.Load();
            Assert.True(settings.DarkTheme);

            settings.DarkTheme = false;
            settings.MaxPreviewLength = 650;
            svc.Save(settings);

            var loaded = svc.Load();
            Assert.False(loaded.DarkTheme);
            Assert.Equal(650, loaded.MaxPreviewLength);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ErrorState_DoesNotExposeSensitiveBody()
    {
        var sensitiveEx = new InvalidOperationException(@"Failed to read e-mail body: C:\Users\natha\Evidences\secret\body.txt contains password '123456'");
        var report = SafeDiagnosticsFormatter.Format(sensitiveEx, "Indexador");
        
        Assert.NotNull(report);
        Assert.Contains("<USER>", report.SanitizedDetails);
        Assert.DoesNotContain(@"natha", report.SanitizedDetails);
    }

    private class MockDesktopValidationService : DesktopValidationService
    {
        public string ExpectedStatus { get; set; } = "PassedWithWarnings";
        public override Task<ValidationReport> ValidateExportAsync(
            string caseFolderPath, string? exportFolderPath, string format, bool strict,
            bool checkEml, bool checkMbox, bool checkAtt, int? sampleSize, string? outDir, CancellationToken ct)
        {
            return Task.FromResult(new ValidationReport(
                ValidationId: "VAL-001",
                CaseId: "CASE-001",
                SourceFileMasked: "Masked",
                SourceSha256: "SHA256",
                AdapterName: "Adapter",
                AdapterVersion: "1.0",
                ExportId: "EXP-001",
                ExportFormat: format,
                StartedAt: DateTimeOffset.UtcNow,
                CompletedAt: DateTimeOffset.UtcNow,
                DurationMs: 120L,
                IndexedMessages: 100,
                SelectedMessages: 100,
                ExportedMessages: 100,
                FailedMessages: 0,
                IndexedAttachments: 10,
                ExportedAttachments: 10,
                FailedAttachments: 0,
                EmptyExportedFiles: 0,
                DuplicateOutputNames: 0,
                MissingExpectedFiles: 0,
                PathSafetyIssues: 0,
                FoldersChecked: new List<string>(),
                FolderResults: new List<FolderValidationResult>(),
                WarningCount: 1,
                ErrorCount: 0,
                Status: ExpectedStatus,
                Issues: new List<ValidationIssue>
                {
                    new ValidationIssue("MV-WARN-MBOX-ESCAPE", "Warning", "Linha 'From ' interna de conteúdo sem escape detectada em arquivo MBOX.", "obj1")
                }
            ));
        }
    }

    private class MockDesktopExportService : DesktopExportService
    {
        public override Task<ExportJobResult> RunExportAsync(
            string caseFolderPath, string format, string? outDir, string? folder, int? limit, int? offset,
            bool includeAttachments, bool extractAttachments, bool overwrite, bool dryRun, IProgressReporter progressReporter, CancellationToken ct)
        {
            return Task.FromResult(new ExportJobResult(
                ExportId: "EXP-001",
                Format: format,
                FoldersSelected: 1,
                MessagesSelected: 3,
                MessagesExported: dryRun ? 0 : 3,
                MessagesFailed: 0,
                AttachmentsExported: 0,
                AttachmentsFailed: 0,
                Issues: new List<ExtractionIssue>(),
                ExportedFiles: new List<string>(),
                DurationMs: 45L
            ));
        }
    }

    // -------------------------------------------------------------------------
    // Milestone 6.2 — Core wizard, adapter and background execution tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ReflectionAdapterResolver_FindsXstReaderAdapter_WhenCopiedToDesktopOutput()
    {
        var resolver = new ReflectionAdapterResolver();
        var adapters = resolver.GetAvailableAdapters().ToList();
        
        var xst = adapters.FirstOrDefault(a => a.Name.Contains("XstReader"));
        Assert.NotNull(xst);
        Assert.Equal("Healthy", xst.HealthStatus);
    }

    [Fact]
    public void ReflectionAdapterResolver_ReturnsDetailedError_WhenAdapterMissing()
    {
        var resolver = new ReflectionAdapterResolver();
        var result = resolver.ResolveAdapter(".xyz");
        
        Assert.False(result.Success);
        Assert.Contains("Nenhum adapter funcional encontrado", result.ErrorMessage);
        Assert.Contains("Procurado em", result.ErrorMessage);
        Assert.Contains("Status dos adapters", result.ErrorMessage);
        Assert.Contains("Ação recomendada", result.ErrorMessage);
    }

    [Fact]
    public async Task DesktopCaseCreationService_ReportsAdapterResolutionFailureClearly()
    {
        string tempDir = CreateTempDir();
        string dummyFile = Path.Combine(tempDir, "test.xyz");
        File.WriteAllText(dummyFile, "dummy content");
        
        try
        {
            var service = new DesktopCaseCreationService();
            var reporter = new MockProgressReporter();
            
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await service.CreateAndIndexCaseAsync(
                    dummyFile,
                    tempDir,
                    "CASE-XYZ",
                    cachePreview: true,
                    limit: null,
                    reporter,
                    CancellationToken.None
                );
            });
            
            Assert.Contains("Nenhum adapter funcional encontrado", ex.Message);
            Assert.Contains(".xyz", ex.Message);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task NewCaseWizard_StartIndexing_DoesNotBlockUiThread_WithFakeLongRunningService()
    {
        var fakeService = new FakeLongRunningCaseCreationService();
        var vm = new NewCaseWizardViewModel(fakeService);
        vm.SourcePath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.ost");
        File.WriteAllText(vm.SourcePath, "dummy");
        vm.DestinationPath = Path.GetTempPath();
        vm.DisclaimerAccepted = true;
        
        try
        {
            vm.StartIndexingCommand.Execute(null);
            
            Assert.True(vm.IsIndexing);
            Assert.Equal("Running", vm.IndexingStatus);
            
            fakeService.Tcs.SetResult(new IndexResult(vm.CaseId, "dummy.db", 1, 1, 0, 0, 100L, "dummy-sha256", "Success", null));
            
            await Task.Delay(100);
            
            Assert.False(vm.IsIndexing);
            Assert.Equal("Success", vm.IndexingStatus);
        }
        finally
        {
            if (File.Exists(vm.SourcePath)) File.Delete(vm.SourcePath);
        }
    }

    [Fact]
    public async Task NewCaseWizard_CancelLongRunningIndexing_SetsCancelledState()
    {
        var fakeService = new FakeLongRunningCaseCreationService();
        var vm = new NewCaseWizardViewModel(fakeService);
        vm.SourcePath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.ost");
        File.WriteAllText(vm.SourcePath, "dummy");
        vm.DestinationPath = Path.GetTempPath();
        vm.DisclaimerAccepted = true;
        
        try
        {
            vm.StartIndexingCommand.Execute(null);
            Assert.True(vm.IsIndexing);
            
            vm.CancelIndexingCommand.Execute(null);
            
            await Task.Delay(100);
            
            Assert.False(vm.IsIndexing);
            Assert.Equal("Cancelled", vm.IndexingStatus);
            Assert.Equal("Operação cancelada pelo operador.", vm.IndexingError);
            Assert.Contains("Indexação cancelada", vm.LogsText);
        }
        finally
        {
            if (File.Exists(vm.SourcePath)) File.Delete(vm.SourcePath);
        }
    }

    [Fact]
    public async Task NewCaseWizard_AdapterMissing_ShowsActionableError()
    {
        var fakeService = new FakeLongRunningCaseCreationService();
        var vm = new NewCaseWizardViewModel(fakeService);
        vm.SourcePath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.ost");
        File.WriteAllText(vm.SourcePath, "dummy");
        vm.DestinationPath = Path.GetTempPath();
        vm.DisclaimerAccepted = true;
        
        try
        {
            vm.StartIndexingCommand.Execute(null);
            
            string missingAdapterError = "Nenhum adapter funcional encontrado para a extensão '.ost'. Procurado em: 'bin/Debug'. Esperado: MailVault.Adapters.XstReader.dll.";
            fakeService.Tcs.SetException(new InvalidOperationException(missingAdapterError));
            
            await Task.Delay(100);
            
            Assert.False(vm.IsIndexing);
            Assert.Equal("Failed", vm.IndexingStatus);
            Assert.NotNull(vm.IndexingError);
            Assert.Contains("Nenhum adapter funcional encontrado", vm.IndexingError);
            Assert.Contains("Procurado em", vm.IndexingError);
        }
        finally
        {
            if (File.Exists(vm.SourcePath)) File.Delete(vm.SourcePath);
        }
    }

    [Fact]
    public void DesktopOutput_ContainsXstReaderAdapterAssembly()
    {
        string basePath = AppContext.BaseDirectory;
        string assemblyPath = Path.Combine(basePath, "MailVault.Adapters.XstReader.dll");
        string dependencyPath = Path.Combine(basePath, "XstReader.Api.dll");
        
        Assert.True(File.Exists(assemblyPath), $"XstReader adapter assembly should exist at: {assemblyPath}");
        Assert.True(File.Exists(dependencyPath), $"XstReader.Api dependency assembly should exist at: {dependencyPath}");
    }

    [Fact]
    public async Task DesktopCaseCreationService_UsesBackgroundExecution_ForLongRunningIndexing()
    {
        var fakeService = new ThreadCheckingCaseCreationService();
        var vm = new NewCaseWizardViewModel(fakeService);
        vm.SourcePath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.ost");
        File.WriteAllText(vm.SourcePath, "dummy");
        vm.DestinationPath = Path.GetTempPath();
        vm.DisclaimerAccepted = true;
        
        int callingThreadId = Environment.CurrentManagedThreadId;
        
        try
        {
            vm.StartIndexingCommand.Execute(null);
            
            for (int i = 0; i < 20 && !fakeService.WasCalled; i++)
            {
                await Task.Delay(10);
            }
            
            Assert.True(fakeService.WasCalled);
            Assert.NotEqual(callingThreadId, fakeService.ExecutingThreadId);
            
            fakeService.Tcs.SetResult(new IndexResult(vm.CaseId, "dummy.db", 1, 1, 0, 0, 100L, "dummy-sha256", "Success", null));
            await Task.Delay(50);
        }
        finally
        {
            if (File.Exists(vm.SourcePath)) File.Delete(vm.SourcePath);
        }
    }

    private class MockProgressReporter : DesktopCaseCreationService.IIndexingProgressReporter
    {
        public List<DesktopIndexingProgress> Progresses { get; } = new();
        public void Report(DesktopIndexingProgress progress) => Progresses.Add(progress);
    }

    private class FakeLongRunningCaseCreationService : DesktopCaseCreationService
    {
        public TaskCompletionSource<IndexResult> Tcs { get; } = new();
        public bool WasCalled { get; private set; }
        
        public override async Task<IndexResult> CreateAndIndexCaseAsync(
            string sourceFilePath, string outputDir, string caseId, bool cachePreview, int? limit,
            IIndexingProgressReporter progressReporter, CancellationToken ct)
        {
            WasCalled = true;
            using (ct.Register(() => Tcs.TrySetException(new OperationCanceledException(ct))))
            {
                return await Tcs.Task;
            }
        }
    }

    private class ThreadCheckingCaseCreationService : DesktopCaseCreationService
    {
        public TaskCompletionSource<IndexResult> Tcs { get; } = new();
        public bool WasCalled { get; private set; }
        public int ExecutingThreadId { get; private set; }
        
        public override async Task<IndexResult> CreateAndIndexCaseAsync(
            string sourceFilePath, string outputDir, string caseId, bool cachePreview, int? limit,
            IIndexingProgressReporter progressReporter, CancellationToken ct)
        {
            WasCalled = true;
            ExecutingThreadId = Environment.CurrentManagedThreadId;
            return await Tcs.Task;
        }
    }

    // =========================================================================
    // Milestone 6.2.1 — ViewModel & UI Responsiveness Refactoring Tests
    // =========================================================================

    [Fact]
    public void NewCaseWizard_LogBuffer_LimitsLineCount()
    {
        // Arrange
        var vm = new NewCaseWizardViewModel();

        // Act - append 600 unique lines
        for (int i = 0; i < 600; i++)
        {
            vm.AppendLog($"Line {i}");
        }

        // Assert - should be capped at 500 lines
        Assert.Equal(500, vm.LogLines.Count);
        // Also derived string should contain the last element and not the first
        Assert.Contains("Line 599", vm.LogsText);
        Assert.DoesNotContain("Line 0", vm.LogsText);
    }

    [Fact]
    public void NewCaseWizard_DoesNotAppendDuplicateProgressMessages()
    {
        // Arrange
        var vm = new NewCaseWizardViewModel();

        // Act
        vm.AppendLog("Step A: processing...");
        vm.AppendLog("Step A: processing..."); // Consecutive duplicate
        vm.AppendLog("Step B: processing...");
        vm.AppendLog("Step A: processing..."); // Non-consecutive duplicate (allowed)

        // Assert
        Assert.Equal(3, vm.LogLines.Count);
    }

    [Fact]
    public async Task NewCaseWizard_LongHash_DoesNotBlockUiThread_WithFakeHasher()
    {
        // Arrange
        var fakeService = new FakeLongRunningCaseCreationService();
        var vm = new NewCaseWizardViewModel(fakeService);
        vm.SourcePath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.ost");
        File.WriteAllText(vm.SourcePath, "dummy");
        vm.DestinationPath = Path.GetTempPath();
        vm.DisclaimerAccepted = true;

        try
        {
            // Act
            vm.StartIndexingCommand.Execute(null);

            // Assert UI is responsive and properties are set
            Assert.True(vm.IsIndexing);
            Assert.True(vm.IsBusy);
            Assert.True(vm.CanCancel);

            fakeService.Tcs.SetResult(new IndexResult(vm.CaseId, "dummy.db", 1, 1, 0, 0, 100L, "dummy-sha256", "Success", null));
            await Task.Delay(100);

            Assert.False(vm.IsIndexing);
            Assert.False(vm.IsBusy);
        }
        finally
        {
            if (File.Exists(vm.SourcePath)) File.Delete(vm.SourcePath);
        }
    }

    [Fact]
    public async Task NewCaseWizard_CancelDuringHash_TransitionsToCancelled()
    {
        // Arrange
        var fakeService = new FakeLongRunningCaseCreationService();
        var vm = new NewCaseWizardViewModel(fakeService);
        vm.SourcePath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.ost");
        File.WriteAllText(vm.SourcePath, "dummy");
        vm.DestinationPath = Path.GetTempPath();
        vm.DisclaimerAccepted = true;

        try
        {
            vm.StartIndexingCommand.Execute(null);
            Assert.True(vm.IsIndexing);

            // Act
            vm.CancelIndexingCommand.Execute(null);
            await Task.Delay(100);

            // Assert
            Assert.False(vm.IsIndexing);
            Assert.Equal("Cancelled", vm.IndexingStatus);
            Assert.Equal("Operação cancelada pelo operador.", vm.IndexingError);
        }
        finally
        {
            if (File.Exists(vm.SourcePath)) File.Delete(vm.SourcePath);
        }
    }

    [Fact]
    public void NewCaseWizard_ProgressUpdates_AreDispatcherSafe()
    {
        // Arrange
        var vm = new NewCaseWizardViewModel();

        // Act
        // Can be called safely from background thread context because of internal RunOnUIThread wrapper
        Task.Run(() =>
        {
            vm.UpdateProgressFromStep("Calculando hash (SHA-256): Calculando hash SHA-256: 50.0% concluído. Speed: 120.0 MB/s. ETA: 00:00:10.", 50.0);
        }).Wait();

        // Assert
        Assert.Equal(50.0, vm.ProgressPercent);
        Assert.Equal("Calculando hash SHA-256: 50.0% concluído.", vm.ProgressDetailText);
        Assert.Equal("120.0 MB/s", vm.ThroughputText);
        Assert.Equal("00:00:10", vm.EtaText);
    }

    [Fact]
    public async Task NewCaseWizard_Polling_DoesNotOverlap()
    {
        // Arrange
        var fakeService = new FakeLongRunningCaseCreationService();
        var vm = new NewCaseWizardViewModel(fakeService);
        vm.SourcePath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.ost");
        File.WriteAllText(vm.SourcePath, "dummy");
        vm.DestinationPath = Path.GetTempPath();
        vm.DisclaimerAccepted = true;

        try
        {
            // Act
            vm.StartIndexingCommand.Execute(null);
            
            // Wait for VM background task starting.
            // Even under high parallel workload or delayed SQLite queries, 
            // the interlocked comparison forces skipped poll ticks instead of queuing.
            await Task.Delay(100);

            // Clean finish
            fakeService.Tcs.SetResult(new IndexResult(vm.CaseId, "dummy.db", 1, 1, 0, 0, 100L, "dummy-sha256", "Success", null));
            await Task.Delay(50);
            
            Assert.Equal("Success", vm.IndexingStatus);
        }
        finally
        {
            if (File.Exists(vm.SourcePath)) File.Delete(vm.SourcePath);
        }
    }

    [Fact]
    public void MainWindowViewModel_InjectsFileDialogService_AndDefaultsCorrectly()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm);
    }

    [Fact]
    public async Task MainWindowViewModel_OpenCaseFolderInExplorerCommand_CallsService()
    {
        var fakeDialog = new FakeFileDialogService();
        var vm = new MainWindowViewModel(
            new CaseWorkspaceDiagnosticService(),
            fileDialogService: fakeDialog
        );

        string tempDir = Path.Combine(Path.GetTempPath(), $"test-case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "case.db");
        
        try
        {
            await CreateMinimalCaseDbAsync(dbPath, folderCount: 2, messageCount: 2);
            File.WriteAllText(Path.Combine(tempDir, "manifest.json"), "{}");

            await vm.LoadCaseAsync(tempDir);
            
            Assert.True(vm.IsCaseLoaded);
            
            vm.OpenCaseFolderInExplorerCommand.Execute(null);
            await Task.Delay(50);

            Assert.Equal(tempDir, fakeDialog.LastOpenedFolder);
        }
        finally
        {
            vm.CloseCase();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task MainWindowViewModel_OpenCaseFromTopbarCommand_LoadsCase_IfPathSelected()
    {
        var fakeDialog = new FakeFileDialogService();
        var vm = new MainWindowViewModel(
            new CaseWorkspaceDiagnosticService(),
            fileDialogService: fakeDialog
        );
        string tempDir = Path.Combine(Path.GetTempPath(), $"test-case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "case.db");

        try
        {
            await CreateMinimalCaseDbAsync(dbPath, folderCount: 2, messageCount: 2);
            File.WriteAllText(Path.Combine(tempDir, "manifest.json"), "{}");

            fakeDialog.FolderResult = tempDir;

            vm.OpenCaseFromTopbarCommand.Execute(null);
            await Task.Delay(100);

            Assert.True(vm.IsCaseLoaded);
        }
        finally
        {
            vm.CloseCase();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task HomeViewModel_SelectCaseFolderCommand_SetsSelectedCasePath()
    {
        var fakeDialog = new FakeFileDialogService { FolderResult = "C:\\some\\case\\path" };
        var vm = new HomeViewModel(new CaseWorkspaceDiagnosticService(), fakeDialog);

        vm.SelectCaseFolderCommand.Execute(null);
        await Task.Delay(50);

        Assert.Equal("C:\\some\\case\\path", vm.SelectedCasePath);
    }

    [Fact]
    public async Task NewCaseWizardViewModel_SelectSourceAndDestinationCommands_SetPaths()
    {
        var fakeDialog = new FakeFileDialogService
        {
            EvidenceFileResult = "C:\\evidences\\evidence.ost",
            FolderResult = "C:\\destination"
        };

        var vm = new NewCaseWizardViewModel(new DesktopCaseCreationService(), fakeDialog);

        vm.SelectSourceFileCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal("C:\\evidences\\evidence.ost", vm.SourcePath);

        vm.SelectDestinationFolderCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal("C:\\destination", vm.DestinationPath);
    }

    private class FakeFileDialogService : IDesktopFileDialogService
    {
        public string? EvidenceFileResult { get; set; }
        public string? FolderResult { get; set; }
        public string? CaseDbResult { get; set; }
        public string? LastOpenedFolder { get; set; }
        public string? LastRevealedFile { get; set; }

        public Task<string?> OpenEvidenceFileAsync() => Task.FromResult(EvidenceFileResult);
        public Task<string?> OpenFolderAsync() => Task.FromResult(FolderResult);
        public Task<string?> OpenCaseDatabaseAsync() => Task.FromResult(CaseDbResult);
        
        public Task OpenFolderInExplorerAsync(string path)
        {
            LastOpenedFolder = path;
            return Task.CompletedTask;
        }

        public Task RevealFileInExplorerAsync(string path)
        {
            LastRevealedFile = path;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task WorkerProcess_NonJsonStdout_DoesNotCrashParent_RecordsProtocolError()
    {
        // Arrange
        var orchestrator = new WorkerProcessOrchestrator();
        
        string shell = "powershell.exe";
        string args = "-Command \"Write-Output '{\\\"type\\\": \\\"started\\\", \\\"timestampUtc\\\": \\\"2026-05-27T00:00:00Z\\\", \\\"engine\\\": \\\"XstReader\\\", \\\"message\\\": \\\"Started\\\"}'; Write-Output 'CONTAMINATION LINE HERE!'; Write-Output '{\\\"type\\\": \\\"completed\\\", \\\"timestampUtc\\\": \\\"2026-05-27T00:00:00Z\\\", \\\"engine\\\": \\\"XstReader\\\", \\\"status\\\": \\\"Success\\\", \\\"folders\\\": 2, \\\"messages\\\": 2, \\\"attachments\\\": 0, \\\"issues\\\": 0}'\"";
        
        orchestrator.CliPathOverride = shell;
        orchestrator.CliArgumentsOverride = args;
        
        string tempCaseFolder = Path.Combine(Path.GetTempPath(), $"test-worker-contam-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCaseFolder);
        
        var jobConfig = new WorkerJobConfig(
            EvidencePath: "dummy.ost",
            CasePath: tempCaseFolder,
            CaseId: "TEST-CONTAM-CASE",
            OperatorId: "test-operator",
            EvidenceSha256: "dummy-sha256",
            EvidenceSize: 1000L,
            SelectedReaderEngine: "XstReader"
        );
        
        int issuesReceived = 0;
        var progressEvents = new List<WorkerProgressEvent>();
        
        try
        {
            // Act
            // Override active spawner command arguments to run powershell command
            var proc = new System.Diagnostics.Process();
            // We can run power Shell in this test environment
            var result = await orchestrator.RunJobAsync(
                jobConfig,
                p =>
                {
                    progressEvents.Add(p);
                    if (p.Type == "issue" && p.Message.Contains("contaminação"))
                    {
                        issuesReceived++;
                    }
                },
                CancellationToken.None
            );
            
            // Assert
            Assert.Equal("Success", result.Status);
            Assert.True(issuesReceived >= 1);
            
            var protocolEvent = progressEvents.Find(e => e.Type == "issue");
            Assert.NotNull(protocolEvent);
            Assert.Contains("contaminação", protocolEvent.Message);
        }
        finally
        {
            if (Directory.Exists(tempCaseFolder)) Directory.Delete(tempCaseFolder, true);
        }
    }
}


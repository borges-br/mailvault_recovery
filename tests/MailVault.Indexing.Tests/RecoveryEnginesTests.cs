using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Adapters.XstReader;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MailVault.Indexing.Tests;

public class RecoveryEnginesTests : IDisposable
{
    private readonly string _tempDir;

    public RecoveryEnginesTests()
    {
        _tempDir = Path.Combine(Directory.GetCurrentDirectory(), "recovery-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    [Fact]
    public async Task XstReaderRecoveryEngine_ThrowsFileNotFound_WhenBeginningSessionWithMissingFile()
    {
        // Arrange
        var engine = new XstReaderRecoveryEngine();
        string missingFile = Path.Combine(_tempDir, "nonexistent.pst");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.BeginReadSessionAsync(missingFile, CancellationToken.None));
    }

    [Fact]
    public async Task XstReaderRecoveryEngine_CapabilityCheck_ReturnsTrue()
    {
        // Arrange
        var engine = new XstReaderRecoveryEngine();
        string fakePst = Path.Combine(_tempDir, "fake.pst");
        await File.WriteAllTextAsync(fakePst, "fake pst data", Encoding.ASCII);

        // Act
        var capability = await engine.InspectAsync(fakePst, CancellationToken.None);
        var issues = engine.DrainIssues();

        // Assert
        Assert.NotNull(capability);
        Assert.Equal("XstReaderRecoveryEngine", capability.ReaderName);
        Assert.NotEmpty(issues);
        Assert.Contains(issues, i => i.Code == "MV-ERR-ADAPTER-REC-INSPECT");
    }

    [Fact]
    public async Task ThunderbirdMboxEngine_DiscoversMboxFiles_AndIndexesThem()
    {
        // Arrange
        string profileDir = Path.Combine(_tempDir, "TbProfile");
        Directory.CreateDirectory(profileDir);

        string maildir = Path.Combine(profileDir, "Mail", "Local Folders");
        Directory.CreateDirectory(maildir);

        // Create MBOX file using ASCII and \n (UNIX line endings) to avoid MimeParser envelope mismatch
        string inboxFile = Path.Combine(maildir, "Inbox");
        string email1 = "From sender@test.com Wed May 27 20:00:00 2026\nSubject: Test Thunderbird 1\nMessage-ID: <tb1@test.com>\nDate: Wed, 27 May 2026 20:00:00 +0000\n\nThis is body 1";
        string email2 = "From sender2@test.com Wed May 27 21:00:00 2026\nSubject: Test Thunderbird 2\nMessage-ID: <tb2@test.com>\nDate: Wed, 27 May 2026 21:00:00 +0000\n\nThis is body 2";
        await File.WriteAllTextAsync(inboxFile, email1 + "\n\n" + email2, Encoding.ASCII);

        // Also add an ignored .msf file
        await File.WriteAllTextAsync(inboxFile + ".msf", "dummy index data", Encoding.ASCII);

        // Set up the index store
        string caseDbDir = Path.Combine(_tempDir, "CASE-TB-TEST");
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDbDir, CancellationToken.None);

        var engine = new ThunderbirdMboxEngine();

        // Act
        var result = await engine.IndexAsync(
            profileDir,
            store,
            "CASE-TB-TEST",
            "Tester",
            cachePreview: false,
            limit: null,
            progress: null,
            CancellationToken.None);

        // Assert
        Assert.Equal("Success", result.Status);

        using var conn = new SqliteConnection($"Data Source={Path.Combine(caseDbDir, "case.db")}");
        await conn.OpenAsync();

        // Collect all messages for diagnostic
        using var diagCmd = new SqliteCommand("SELECT subject, internet_message_id FROM messages ORDER BY subject", conn);
        using var diagReader = diagCmd.ExecuteReader();
        var allMessages = new List<(string subject, string msgId)>();
        while (diagReader.Read())
        {
            allMessages.Add((diagReader.IsDBNull(0) ? "<null>" : diagReader.GetString(0),
                             diagReader.IsDBNull(1) ? "<null>" : diagReader.GetString(1)));
        }
        diagReader.Close();

        // Diagnostic output: show what was actually indexed
        string diagInfo = $"FoldersIndexed={result.FoldersIndexed}, MessagesIndexed={result.MessagesIndexed}, " +
                          $"Issues={result.IssuesDetected}, MsgCount={allMessages.Count}, " +
                          $"Msgs=[{string.Join("|", allMessages.Select(m => m.subject))}]";

        using var cmdFolders = new SqliteCommand("SELECT COUNT(*) FROM folders", conn);
        // Expect at minimum 1 folder saved (Inbox). Parent logical folders (e.g. "Local Folders") are
        // created implicitly by EnsureParentFoldersSavedAsync, so total may be >= 1.
        Assert.True(Convert.ToInt32(cmdFolders.ExecuteScalar()) >= 1, diagInfo);

        // Assert both messages were indexed
        Assert.True(allMessages.Count >= 2, $"Expected >= 2 messages but got {allMessages.Count}. {diagInfo}");

        var msg1 = allMessages.FirstOrDefault(m => m.subject == "Test Thunderbird 1");
        var msg2 = allMessages.FirstOrDefault(m => m.subject == "Test Thunderbird 2");

        Assert.True(msg1 != default, $"Message 'Test Thunderbird 1' not found. {diagInfo}");
        Assert.True(msg2 != default, $"Message 'Test Thunderbird 2' not found. {diagInfo}");
    }

    [Fact]
    public async Task ThunderbirdMboxEngine_ResolvesProfileFromProfilesIni()
    {
        // Arrange
        string tbRootDir = Path.Combine(_tempDir, "Thunderbird");
        Directory.CreateDirectory(tbRootDir);

        string profileDir = Path.Combine(tbRootDir, "Profiles", "default-profile");
        Directory.CreateDirectory(profileDir);

        string localFolders = Path.Combine(profileDir, "Mail", "Local Folders");
        Directory.CreateDirectory(localFolders);

        string mboxPath = Path.Combine(localFolders, "Inbox");
        string email = "From test@test.com Wed May 27 20:00:00 2026\nSubject: Thunderbird Profile Ini Test\nMessage-ID: <tb-ini@test.com>\nDate: Wed, 27 May 2026 20:00:00 +0000\n\nBody";
        await File.WriteAllTextAsync(mboxPath, email, Encoding.ASCII);

        string iniPath = Path.Combine(tbRootDir, "profiles.ini");
        string iniContent = @"
[Install]
Default=Profiles/default-profile

[Profile0]
Name=default
IsRelative=1
Path=Profiles/default-profile
Default=1
";
        await File.WriteAllTextAsync(iniPath, iniContent, Encoding.ASCII);

        // Set up the index store
        string caseDbDir = Path.Combine(_tempDir, "CASE-TB-INI");
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDbDir, CancellationToken.None);

        var engine = new ThunderbirdMboxEngine();

        // Act
        var result = await engine.IndexAsync(
            iniPath,
            store,
            "CASE-TB-INI",
            "Tester",
            cachePreview: false,
            limit: null,
            progress: null,
            CancellationToken.None);

        // Assert
        Assert.Equal("Success", result.Status);
        Assert.Equal(1, result.MessagesIndexed);
    }

    [Fact]
    public async Task MaildirEngine_IndexesMaildirCorrectly_WithFlags()
    {
        // Arrange
        string maildirDir = Path.Combine(_tempDir, "MyMaildir");
        Directory.CreateDirectory(maildirDir);

        string curDir = Path.Combine(maildirDir, "cur");
        string newDir = Path.Combine(maildirDir, "new");
        Directory.CreateDirectory(curDir);
        Directory.CreateDirectory(newDir);

        // Create standard EML in cur with "Seen" flag. Use ';' instead of ':' for Windows filename safety.
        string msgInCur = Path.Combine(curDir, "12345.M123P123.host;2,S");
        string emailCur = "Subject: Cur Email\r\nMessage-ID: <cur@test.com>\r\nDate: Wed, 27 May 2026 20:00:00 +0000\r\n\r\nCur Body";
        await File.WriteAllTextAsync(msgInCur, emailCur, Encoding.ASCII);

        // Create standard EML in new (no flags)
        string msgInNew = Path.Combine(newDir, "12346.M123P123.host;2,");
        string emailNew = "Subject: New Email\r\nMessage-ID: <new@test.com>\r\nDate: Wed, 27 May 2026 20:00:00 +0000\r\n\r\nNew Body";
        await File.WriteAllTextAsync(msgInNew, emailNew, Encoding.ASCII);

        // Set up index store
        string caseDbDir = Path.Combine(_tempDir, "CASE-MD-TEST");
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDbDir, CancellationToken.None);

        var engine = new MaildirEngine();

        // Act
        var result = await engine.IndexAsync(
            maildirDir,
            store,
            "CASE-MD-TEST",
            "Tester",
            cachePreview: false,
            limit: null,
            progress: null,
            CancellationToken.None);

        // Assert
        Assert.Equal("Success", result.Status);
        Assert.Equal(1, result.FoldersIndexed); // Root
        Assert.Equal(2, result.MessagesIndexed);

        // Check DB content for flags mapping
        using var conn = new SqliteConnection($"Data Source={Path.Combine(caseDbDir, "case.db")}");
        await conn.OpenAsync();

        using var cmd = new SqliteCommand("SELECT message_id, subject FROM messages ORDER BY subject", conn);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Inbox/12345.M123P123.host;2,S", reader.GetString(0));
        Assert.Equal("Cur Email", reader.GetString(1));

        Assert.True(reader.Read());
        Assert.Equal("Inbox/12346.M123P123.host;2,", reader.GetString(0));
        Assert.Equal("New Email", reader.GetString(1));
    }

    [Fact]
    public async Task EmlFolderEngine_IndexesEmlFilesRecursively()
    {
        // Arrange
        string rootDir = Path.Combine(_tempDir, "Emls");
        Directory.CreateDirectory(rootDir);

        string subDir = Path.Combine(rootDir, "Subfolder");
        Directory.CreateDirectory(subDir);

        // Write EML to root
        string eml1 = Path.Combine(rootDir, "root.eml");
        string email1 = "Subject: Root Email\r\nMessage-ID: <root@test.com>\r\nDate: Wed, 27 May 2026 20:00:00 +0000\r\n\r\nRoot Body";
        await File.WriteAllTextAsync(eml1, email1, Encoding.ASCII);

        // Write EML to subfolder
        string eml2 = Path.Combine(subDir, "sub.eml");
        string email2 = "Subject: Sub Email\r\nMessage-ID: <sub@test.com>\r\nDate: Wed, 27 May 2026 20:00:00 +0000\r\n\r\nSub Body";
        await File.WriteAllTextAsync(eml2, email2, Encoding.ASCII);

        // Set up index store
        string caseDbDir = Path.Combine(_tempDir, "CASE-EML-TEST");
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseDbDir, CancellationToken.None);

        var engine = new EmlFolderEngine();

        // Act
        var result = await engine.IndexAsync(
            rootDir,
            store,
            "CASE-EML-TEST",
            "Tester",
            cachePreview: false,
            limit: null,
            progress: null,
            CancellationToken.None);

        // Assert
        Assert.Equal("Success", result.Status);
        Assert.Equal(2, result.FoldersIndexed); // Root and Subfolder
        Assert.Equal(2, result.MessagesIndexed);

        // Check DB hierarchy
        using var conn = new SqliteConnection($"Data Source={Path.Combine(caseDbDir, "case.db")}");
        await conn.OpenAsync();

        using var cmd = new SqliteCommand("SELECT folder_id, display_name, parent_id FROM folders ORDER BY display_name", conn);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Emls", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));

        Assert.True(reader.Read());
        Assert.Equal("Subfolder", reader.GetString(1));
        Assert.Equal("Emls", reader.GetString(2));
    }
}

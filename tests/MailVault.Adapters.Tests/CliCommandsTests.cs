using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MailVault.Cli;
using Xunit;

namespace MailVault.Adapters.Tests;

public class CliCommandsTests : IDisposable
{
    private readonly string _tempWorkspaceDir;
    private readonly string _dummyPstFile;
    private readonly TextWriter _originalConsoleOut;

    public CliCommandsTests()
    {
        // Create an isolated temp testing directory within workspace scratch
        _tempWorkspaceDir = Path.Combine("c:\\Github\\mailvault_recovery\\scratch", $"cli-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspaceDir);

        // Create a dummy PST file so that FileInfo.Exists passes validation in the CLI
        _dummyPstFile = Path.Combine(_tempWorkspaceDir, "test-store.pst");
        File.WriteAllText(_dummyPstFile, "Dummy PST content for CLI validation");

        _originalConsoleOut = Console.Out;
    }

    public void Dispose()
    {
        // Restore original Console.Out
        Console.SetOut(_originalConsoleOut);

        // Cleanup temporary files
        try
        {
            if (Directory.Exists(_tempWorkspaceDir))
            {
                Directory.Delete(_tempWorkspaceDir, true);
            }
        }
        catch
        {
            // Ignore minor cleanup errors during test suite runs
        }
    }

    [Fact]
    public async Task TreeCommand_WithFakeMailStoreReader_PrintsHierarchyCorrectly()
    {
        // Arrange
        var reader = new FakeMailStoreReader();
        Program.InjectReader(reader);

        using var sw = new StringWriter();
        Console.SetOut(sw);

        var args = new[] { "tree", _dummyPstFile, "--out", _tempWorkspaceDir };

        // Act
        int exitCode = await Program.Main(args);
        string output = sw.ToString();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("Estrutura Hierárquica de Pastas:", output);
        Assert.Contains("Inbox (Mensagens: 5)", output);
        Assert.Contains("Subfolder (Mensagens: 2)", output);
        Assert.Contains("Sent Items (Mensagens: 1)", output);
        Assert.Contains("Total de Pastas: 3", output);
        Assert.Contains("Manifesto e trilha gravados em:", output);

        // Verify that manifest.json and audit.log were generated inside case folder
        string[] caseDirs = Directory.GetDirectories(_tempWorkspaceDir);
        Assert.Single(caseDirs);
        string caseDir = caseDirs[0];
        Assert.True(File.Exists(Path.Combine(caseDir, "audit.log")));
        Assert.True(File.Exists(Path.Combine(caseDir, "manifest.json")));
    }

    [Fact]
    public async Task ListCommand_WithLimitAndOffset_PaginatesCorrectly()
    {
        // Arrange
        var reader = new FakeMailStoreReader();
        Program.InjectReader(reader);

        using var sw = new StringWriter();
        Console.SetOut(sw);

        // We request a limit of 2 and offset of 1 on the Inbox (which has 5 emails total, msg-inbox-1 to msg-inbox-5)
        // Offset 1 skips msg-inbox-1, displaying msg-inbox-2 and msg-inbox-3
        var args = new[] { "list", _dummyPstFile, "--folder", "Inbox", "--limit", "2", "--offset", "1", "--out", _tempWorkspaceDir };

        // Act
        int exitCode = await Program.Main(args);
        string output = sw.ToString();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("Encontradas 5 mensagens no total. Exibindo 2 itens", output);
        
        // Should contain inbox-2 and inbox-3
        Assert.Contains("msg-inbox-2", output);
        Assert.Contains("Assunto do Fake Inbox 2", output);
        Assert.Contains("msg-inbox-3", output);
        Assert.Contains("Assunto do Fake Inbox 3", output);

        // Should NOT contain inbox-1 (due to offset) or inbox-4/5 (due to limit)
        Assert.DoesNotContain("msg-inbox-1", output);
        Assert.DoesNotContain("msg-inbox-4", output);
        Assert.DoesNotContain("msg-inbox-5", output);

        // Should show warning issue for msg-inbox-3 without crashing
        Assert.Contains("--> ALERTA: [MV-WARN-TEST]", output);
    }

    [Fact]
    public async Task PreviewCommand_WithTruncatedBody_DisplaysSafely()
    {
        // Arrange
        var reader = new FakeMailStoreReader();
        Program.InjectReader(reader);

        using var sw = new StringWriter();
        Console.SetOut(sw);

        // Request preview of msg-inbox-1 with body truncated at 5 lines
        var args = new[] { "preview", _dummyPstFile, "--message-id", "msg-inbox-1", "--body-lines", "5", "--out", _tempWorkspaceDir };

        // Act
        int exitCode = await Program.Main(args);
        string output = sw.ToString();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("Visualização Segura", output);
        Assert.Contains("Internal ID   : msg-inbox-1", output);
        Assert.Contains("Message ID    : <inbox-1@fake.local>", output);
        Assert.Contains("Assunto       : Assunto do Fake Inbox 1", output);
        Assert.Contains("Remetente     : Remetente Fake <from@fake.local>", output);
        Assert.Contains("anexo-1.txt", output);

        // Body print validation
        Assert.Contains("Este é o corpo da mensagem 1.", output);
        Assert.Contains("Linha 2 do corpo.", output);
        Assert.Contains("Linha 3 do corpo.", output);
        Assert.Contains("Linha extra 4 do e-mail simulado.", output);
        Assert.Contains("Linha extra 5 do e-mail simulado.", output);
        
        // Truncation compliance text
        Assert.Contains("TEXTO TRUNCADO SEGURAMENTE PARA COMPLIANCE FORENSE", output);
        
        // Line 6 should not be printed
        Assert.DoesNotContain("Linha extra 6 do e-mail simulado.", output);
    }

    [Fact]
    public async Task PreviewCommand_WithIssues_DisplaysIssuesWithoutCrash()
    {
        // Arrange
        var reader = new FakeMailStoreReader();
        Program.InjectReader(reader);

        using var sw = new StringWriter();
        Console.SetOut(sw);

        // Request preview of msg-inbox-3 (which has a simulated warning issue)
        var args = new[] { "preview", _dummyPstFile, "--message-id", "msg-inbox-3", "--out", _tempWorkspaceDir };

        // Act
        int exitCode = await Program.Main(args);
        string output = sw.ToString();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("ALERTAS/ISSUES DA MENSAGEM:", output);
        Assert.Contains("* [MV-WARN-TEST] [Warning] Mensagem possui propriedades corrompidas de cabeçalho.", output);
    }
}

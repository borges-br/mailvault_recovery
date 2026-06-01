using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using Xunit;

namespace MailVault.Core.Tests;

public class PffSignatureInspectorTests
{
    // Cria um cabeçalho PFF sintético de 600 bytes (suficiente para o offset de cripto Unicode 0x201).
    private static byte[] BuildHeader(int wVer, byte cryptByte, bool withMagic = true, char mc1 = 'S', char mc2 = 'M')
    {
        var b = new byte[600];
        if (withMagic) { b[0] = 0x21; b[1] = 0x42; b[2] = 0x44; b[3] = 0x4E; } // !BDN
        b[8] = (byte)mc1; b[9] = (byte)mc2;
        b[10] = (byte)(wVer & 0xFF);
        b[11] = (byte)((wVer >> 8) & 0xFF);
        bool ansi = wVer is 14 or 15;
        int cryptOffset = ansi ? 0x1CD : 0x201;
        b[cryptOffset] = cryptByte;
        return b;
    }

    [Fact]
    public void Inspect_ModernUnicodeOst_wVer36_DetectedAsUnicode()
    {
        // wVer=36 é o que OSTs modernos reais usam (validado contra corpus local).
        using var ms = new MemoryStream(BuildHeader(36, 0x00, mc1: 'S', mc2: 'O'));
        var info = PffSignatureInspector.Inspect(ms, ".ost");

        Assert.True(info.IsPff);
        Assert.Contains("Unicode", info.Architecture);
        Assert.Equal(36, info.RawVersion);
        Assert.Equal("SO", info.MagicClient);
        Assert.Contains("None", info.Encryption);
    }

    [Fact]
    public void Inspect_AnsiHeader_wVer14_DetectedAsAnsi()
    {
        using var ms = new MemoryStream(BuildHeader(14, 0x01));
        var info = PffSignatureInspector.Inspect(ms, ".pst");

        Assert.True(info.IsPff);
        Assert.Contains("ANSI", info.Architecture);
        Assert.Equal(14, info.RawVersion);
    }

    [Theory]
    [InlineData(0x00, "None")]
    [InlineData(0x01, "Compressible")]
    [InlineData(0x02, "High")]
    public void Inspect_EncryptionByte_MappedCorrectly(byte cryptByte, string expectedFragment)
    {
        using var ms = new MemoryStream(BuildHeader(23, cryptByte));
        var info = PffSignatureInspector.Inspect(ms, ".pst");
        Assert.Contains(expectedFragment, info.Encryption);
    }

    [Fact]
    public void Inspect_NoMagic_ReportsNotPff()
    {
        using var ms = new MemoryStream(BuildHeader(23, 0x00, withMagic: false));
        var info = PffSignatureInspector.Inspect(ms, ".ost");

        Assert.False(info.IsPff);
        Assert.Contains("!BDN", info.Notes);
    }

    [Fact]
    public void Inspect_ShortStream_ReportsNotPff()
    {
        using var ms = new MemoryStream(new byte[] { 0x21, 0x42, 0x44, 0x4E });
        var info = PffSignatureInspector.Inspect(ms, ".ost");
        Assert.False(info.IsPff);
    }

    [Fact]
    public void Inspect_MissingFile_DoesNotThrow_ReportsNotPff()
    {
        var info = PffSignatureInspector.Inspect(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".ost"));
        Assert.False(info.IsPff);
        Assert.Contains("não encontrado", info.Notes);
    }
}

public class UnsupportedPstExportWriterTests
{
    [Fact]
    public void Writer_IsSupported_IsFalse()
    {
        var writer = new UnsupportedPstExportWriter();
        Assert.False(writer.IsSupported);
        Assert.Equal("UnsupportedPstExportWriter", writer.WriterName);
    }

    [Fact]
    public async Task WriteAsync_ReturnsNotSupported_AndCreatesNoFile()
    {
        var writer = new UnsupportedPstExportWriter();
        string outPath = Path.Combine(Path.GetTempPath(), "mailvault-should-not-exist-" + Guid.NewGuid().ToString("N") + ".pst");
        var request = new PstExportRequest("source.ost", outPath);

        // O writer ignora o reader por contrato — null! é intencional neste caminho NÃO-SUPORTADO.
        var outcome = await writer.WriteAsync(null!, request, null, CancellationToken.None);

        Assert.False(outcome.Supported);
        Assert.False(outcome.Success);
        Assert.Null(outcome.OutputPath);
        Assert.Equal(0, outcome.ExportedMessages);
        Assert.Equal(UnsupportedPstExportWriter.NotSupportedCode, outcome.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Explanation));
        Assert.False(File.Exists(outPath)); // nunca cria PST falso
    }
}

public class RecoveryExportResultClassificationTests
{
    private static RecoveryExportResult Make(int total, int exported, int failed, int failedAtt = 0)
        => new(
            SourcePath: "x.ost", Engine: "Fake", StartedAt: DateTimeOffset.UtcNow,
            FinishedAt: DateTimeOffset.UtcNow, OutputDir: "out",
            TotalFolders: 1, TotalMessages: total, ExportedMessages: exported,
            FailedMessages: failed, ExportedAttachments: 0, FailedAttachments: failedAtt,
            Errors: Array.Empty<RecoveryExportIssue>());

    [Fact]
    public void Classify_AllExported_NoFailures_IsCompleto()
        => Assert.StartsWith("Completo", RecoveryExportRunner.ClassifyResult(Make(10, 10, 0)));

    [Fact]
    public void Classify_SomeFailures_IsParcial()
        => Assert.StartsWith("Parcial", RecoveryExportRunner.ClassifyResult(Make(10, 8, 2)));

    [Fact]
    public void Classify_AttachmentFailuresOnly_IsParcial()
        => Assert.StartsWith("Parcial", RecoveryExportRunner.ClassifyResult(Make(10, 10, 0, failedAtt: 3)));

    [Fact]
    public void Classify_NothingExportedButMessagesExisted_IsInconclusivo()
        => Assert.StartsWith("Inconclusivo", RecoveryExportRunner.ClassifyResult(Make(10, 0, 0)));

    [Fact]
    public void Classify_NoMessagesAtAll_IsInconclusivo()
        => Assert.StartsWith("Inconclusivo", RecoveryExportRunner.ClassifyResult(Make(0, 0, 0)));
}

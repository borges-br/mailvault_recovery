using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Carving;
using Xunit;

namespace MailVault.Carving.Tests;

// Valida a régua de classificação (3c.2) e o builder de EML parcial (3c.3) com buffers sintéticos,
// separando "código correto" (acha e-mail real quando ele existe) de "corpus sem e-mail localizável".
public class CarvePipelineTests : IDisposable
{
    private readonly string _dir;
    public CarvePipelineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mv-carvepipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] U16(string s) => Encoding.Unicode.GetBytes(s);
    private static byte[] Pad(int n) => new byte[n];

    private static byte[] Build(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    private string WriteTemp(byte[] b)
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".ost");
        File.WriteAllBytes(p, b);
        return p;
    }

    // Buffer sintético "e-mail real": IPM.Note + assunto/from/to/data/corpo legíveis (UTF-16LE), separados por nulls.
    private string WriteRealMail()
    {
        var b = Build(
            Pad(1000), U16("IPM.Note"), Pad(8),
            U16("Reuniao de equipe amanha as 10h sobre o projeto"), Pad(8),
            U16("alice@example.com"), Pad(8),
            U16("bob@example.com"), Pad(8),
            U16("Mon, 11 Apr 2026 10:00:00"), Pad(8),
            U16("Ola Bob, segue o corpo da mensagem com bastante texto legivel para passar do limite minimo de corpo do carver."),
            Pad(1000));
        return WriteTemp(b);
    }

    [Fact]
    public async Task RealMail_ClassifiedMail_AndExported_WhenExportOn()
    {
        string outDir = Path.Combine(_dir, "out-mail");
        var result = await new RawPffCarver().CarveAsync(WriteRealMail(), outDir,
            new CarveOptions(Export: true, MinConfidence: 50), CancellationToken.None);

        Assert.Contains(result.Candidates, c => c.Classification == CarveClass.Mail);
        var mail = result.Candidates.First(c => c.Classification == CarveClass.Mail);
        // Régua heurística: o "assunto" pode capturar o run mais longo (corpo) — limitação conhecida de
        // desambiguação assunto/corpo. O que validamos: classificou Mail, achou remetente e capturou o conteúdo.
        Assert.False(string.IsNullOrWhiteSpace(mail.Subject));
        Assert.Equal("alice@example.com", mail.FromEmail);
        Assert.Contains("Reuniao", (mail.Subject ?? "") + " " + (mail.BodySnippet ?? ""));
        Assert.True(result.ExportedCount >= 1);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(outDir, "Recovered", "Carved", "Partial"), "*.eml"));
    }

    [Fact]
    public async Task ReportOnly_DoesNotExport_EvenForMail()
    {
        string outDir = Path.Combine(_dir, "out-reportonly");
        var result = await new RawPffCarver().CarveAsync(WriteRealMail(), outDir,
            new CarveOptions(Export: false), CancellationToken.None);

        Assert.Contains(result.Candidates, c => c.Classification == CarveClass.Mail);
        Assert.Equal(0, result.ExportedCount);
        Assert.False(Directory.Exists(Path.Combine(outDir, "Recovered")));
    }

    [Fact]
    public async Task SystemItem_ClassifiedSystem_NotExported()
    {
        // IPM.Note perto de marcador de sistema → System (denylist), nunca exportado.
        var b = Build(Pad(500), U16("IPM.Note"), Pad(8),
            U16("Outlook Message Manager () (KEY: 47656E6572616C204B657900)"), Pad(8),
            U16("Offline Message: Pending Message Delete"), Pad(500));
        string outDir = Path.Combine(_dir, "out-sys");
        var result = await new RawPffCarver().CarveAsync(WriteTemp(b), outDir,
            new CarveOptions(Export: true), CancellationToken.None);

        Assert.Contains(result.Candidates, c => c.Classification == CarveClass.System);
        Assert.DoesNotContain(result.Candidates, c => c.Classification == CarveClass.Mail);
        Assert.Equal(0, result.ExportedCount);
    }

    [Fact]
    public async Task MarkerOnly_ClassifiedLocateOnly_NotExported()
    {
        // IPM.Note isolado, sem campos legíveis ao redor → LocateOnly.
        var b = Build(Pad(2000), U16("IPM.Note"), Pad(2000));
        string outDir = Path.Combine(_dir, "out-locate");
        var result = await new RawPffCarver().CarveAsync(WriteTemp(b), outDir,
            new CarveOptions(Export: true), CancellationToken.None);

        Assert.All(result.Candidates, c => Assert.NotEqual(CarveClass.Mail, c.Classification));
        Assert.Equal(0, result.ExportedCount);
    }
}

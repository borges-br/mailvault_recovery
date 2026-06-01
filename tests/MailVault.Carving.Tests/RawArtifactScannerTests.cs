using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Carving;
using Xunit;

namespace MailVault.Carving.Tests;

public class RawArtifactScannerTests : IDisposable
{
    private readonly string _dir;

    public RawArtifactScannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mv-carve-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private string WriteTemp(byte[] bytes)
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    [Fact]
    public async Task FindsIpmNote_Utf16_WithPreview()
    {
        var bytes = Concat(new byte[2000], Utf16("IPM.NoteAssunto de teste"), new byte[2000]);
        var result = await new RawArtifactScanner().ScanAsync(WriteTemp(bytes), new CarveOptions(), CancellationToken.None);

        Assert.Equal(CarveStatus.Completed, result.Status);
        Assert.True(result.TotalCandidates >= 1);
        Assert.True(result.CandidatesByKind.TryGetValue("IPM.Note", out var n) && n >= 1);
        var cand = result.Candidates.First(c => c.Encoding == "utf16le");
        Assert.Equal(2000, cand.Offset);
        Assert.Contains("IPM.Note", cand.Preview);
    }

    [Fact]
    public async Task FindsIpmNote_Ascii()
    {
        var bytes = Concat(new byte[500], Ascii("IPM.Note here"), new byte[500]);
        var result = await new RawArtifactScanner().ScanAsync(WriteTemp(bytes), new CarveOptions(), CancellationToken.None);
        Assert.Contains(result.Candidates, c => c.Encoding == "ascii" && c.Offset == 500);
    }

    [Fact]
    public async Task NoSignature_ZeroCandidates()
    {
        var bytes = Concat(Ascii("just some plain text without the marker"), new byte[1000]);
        var result = await new RawArtifactScanner().ScanAsync(WriteTemp(bytes), new CarveOptions(), CancellationToken.None);
        Assert.Equal(0, result.TotalCandidates);
        Assert.Equal(CarveStatus.Completed, result.Status);
    }

    [Fact]
    public async Task SignatureSpanningChunkBoundary_FoundExactlyOnce()
    {
        // Assinatura em offset 65534 atravessa a fronteira do chunk de 64 KB.
        int offset = 65534;
        var sig = Utf16("IPM.Note");
        var bytes = new byte[200 * 1024];
        Buffer.BlockCopy(sig, 0, bytes, offset, sig.Length);

        var opts = new CarveOptions(ChunkSizeBytes: 65536, OverlapBytes: 4096);
        var result = await new RawArtifactScanner().ScanAsync(WriteTemp(bytes), opts, CancellationToken.None);

        Assert.Equal(1, result.TotalCandidates); // achada uma vez, sem recontar a sobreposição
        Assert.Equal(offset, result.Candidates[0].Offset);
    }

    [Fact]
    public async Task RespectsMaxScanBytes()
    {
        var bytes = Concat(new byte[70000], Utf16("IPM.Note"), new byte[1000]);
        var opts = new CarveOptions(MaxScanBytes: 1000);
        var result = await new RawArtifactScanner().ScanAsync(WriteTemp(bytes), opts, CancellationToken.None);
        Assert.Equal(0, result.TotalCandidates); // assinatura está além do limite de scan
        Assert.Equal(CarveStatus.StoppedByScanLimit, result.Status);
    }

    [Fact]
    public async Task HeaderlessBuffer_StillScansAndFinds()
    {
        // Sem assinatura !BDN (cabeçalho destruído) — o carving deve funcionar mesmo assim.
        var bytes = Concat(new byte[100], Utf16("IPM.Note corpo recuperável"), new byte[100]);
        var result = await new RawArtifactScanner().ScanAsync(WriteTemp(bytes), new CarveOptions(), CancellationToken.None);
        Assert.False(result.HeaderIsPff);
        Assert.True(result.TotalCandidates >= 1);
    }

    [Fact]
    public async Task NoPreviews_OmitsPreview()
    {
        var bytes = Concat(new byte[100], Utf16("IPM.Note xyz"), new byte[100]);
        var result = await new RawArtifactScanner().ScanAsync(WriteTemp(bytes), new CarveOptions(NoPreviews: true), CancellationToken.None);
        Assert.True(result.TotalCandidates >= 1);
        Assert.All(result.Candidates, c => Assert.Null(c.Preview));
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;

namespace MailVault.Carving;

/// <summary>
/// Camada A+B do carver (Milestone 3c.1): varredura física read-only, em chunks com overlap,
/// procurando assinaturas (IPM.Note ASCII/UTF-16LE). SOMENTE-RELATÓRIO — não constrói nem exporta
/// mensagens (logo, é impossível gerar recuperação falsa nesta etapa). Nunca carrega o arquivo
/// inteiro em memória; respeita limites de bytes, candidatos, densidade e timeout.
/// </summary>
public sealed class RawArtifactScanner
{
    public async Task<CarveResult> ScanAsync(string filePath, CarveOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var candidates = new List<CarveCandidate>();
        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var status = CarveStatus.Completed;
        long fileSize = 0, bytesScanned = 0;

        // Diagnóstico de header (read-only). NÃO bloqueia o carving mesmo se !BDN ausente — esse é o ponto.
        PffSignatureInfo sig;
        try { sig = PffSignatureInspector.Inspect(filePath); }
        catch (Exception ex)
        {
            sig = new PffSignatureInfo(false, "Desconhecido", "Desconhecida", -1, "?", "N/A", -1, ex.Message);
            notes.Add($"Falha ao inspecionar header: {ex.Message}");
        }
        if (!sig.IsPff)
            notes.Add("Header sem assinatura !BDN — carving por varredura física é justamente para este caso.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.TimeoutSeconds is double t && t > 0) timeoutCts.CancelAfter(TimeSpan.FromSeconds(t));
        var token = timeoutCts.Token;

        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 1 << 20, useAsync: true);
            fileSize = fs.Length;

            int chunk = Math.Max(64 * 1024, options.ChunkSizeBytes);
            int overlap = Math.Min(Math.Max(CarveSignatures.MaxSignatureLength, options.OverlapBytes), chunk);
            long maxScan = options.MaxScanBytes > 0 ? Math.Min(options.MaxScanBytes, fileSize) : fileSize;

            byte[] carry = Array.Empty<byte>();
            long newStart = 0;                 // offset do arquivo onde os bytes recém-lidos começam
            var buf = new byte[chunk];

            while (newStart < maxScan)
            {
                token.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(chunk, maxScan - newStart);
                int read = await fs.ReadAsync(buf.AsMemory(0, toRead), token);
                if (read <= 0) break;

                // searchBuf = carry (cauda do chunk anterior) + bytes novos → captura sinais na borda.
                int searchLen = carry.Length + read;
                var searchBuf = new byte[searchLen];
                Buffer.BlockCopy(carry, 0, searchBuf, 0, carry.Length);
                Buffer.BlockCopy(buf, 0, searchBuf, carry.Length, read);

                ScanBuffer(searchBuf, carry.Length, newStart, options, candidates, byKind);
                bytesScanned = newStart + read;

                if (candidates.Count >= options.MaxCandidates)
                {
                    status = CarveStatus.StoppedByCandidateLimit;
                    notes.Add($"Limite de candidatos atingido ({options.MaxCandidates}).");
                    break;
                }
                if (options.MaxCandidatesPerMb > 0 && bytesScanned >= (1 << 20))
                {
                    double perMb = candidates.Count / (bytesScanned / 1048576.0);
                    if (perMb > options.MaxCandidatesPerMb)
                    {
                        status = CarveStatus.StoppedByDensityLimit;
                        notes.Add($"Densidade {perMb:F0} candidatos/MB > limite ({options.MaxCandidatesPerMb}/MB) — provável ruído; scan abortado.");
                        break;
                    }
                }

                int carryLen = Math.Min(overlap, searchLen);
                carry = searchBuf[(searchLen - carryLen)..];
                newStart += read;
            }

            if (status == CarveStatus.Completed && maxScan < fileSize)
            {
                status = CarveStatus.StoppedByScanLimit;
                notes.Add($"Scan limitado a {maxScan:N0} de {fileSize:N0} bytes (--max-scan-bytes).");
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            status = CarveStatus.Timeout;
            notes.Add("Scan interrompido por timeout; resultados parciais preservados.");
        }
        catch (OperationCanceledException)
        {
            status = CarveStatus.Timeout;
            notes.Add("Scan cancelado.");
        }
        catch (Exception ex)
        {
            status = CarveStatus.Failed;
            notes.Add($"Falha no scan: {ex.GetType().Name}: {ex.Message}");
        }

        string headerSummary = $"{sig.FormatFamily} · {sig.Architecture} · ofuscação {sig.Encryption}";
        return new CarveResult(filePath, fileSize, bytesScanned, sig.IsPff, headerSummary,
            candidates.Count, byKind, candidates, Math.Round(sw.Elapsed.TotalSeconds, 2), status, notes);
    }

    private void ScanBuffer(byte[] buf, int carryLen, long newStart, CarveOptions o,
        List<CarveCandidate> cands, Dictionary<string, int> byKind)
    {
        FindSignature(buf, carryLen, newStart, CarveSignatures.IpmNoteUtf16, CarveSignatures.KindIpmNote, "utf16le", 90, o, cands, byKind);
        FindSignature(buf, carryLen, newStart, CarveSignatures.IpmNoteAscii, CarveSignatures.KindIpmNote, "ascii", 80, o, cands, byKind);
    }

    private void FindSignature(byte[] buf, int carryLen, long newStart, byte[] sig, string kind, string enc,
        int confidence, CarveOptions o, List<CarveCandidate> cands, Dictionary<string, int> byKind)
    {
        var span = buf.AsSpan();
        int from = 0;
        while (from <= span.Length - sig.Length)
        {
            int idx = span.Slice(from).IndexOf(sig);
            if (idx < 0) break;
            int hit = from + idx;

            // Só aceita hits que avançam para os bytes novos (hit+len > carry) → evita recontar a sobreposição.
            if (hit + sig.Length > carryLen)
            {
                long offset = newStart - carryLen + hit;
                string? preview = o.NoPreviews ? null : ExtractPreview(buf, hit, enc, o.MaxPreviewBytes);
                cands.Add(new CarveCandidate(offset, kind, enc, confidence, preview));
                byKind[kind] = (byKind.TryGetValue(kind, out var c) ? c : 0) + 1;
                if (cands.Count >= o.MaxCandidates) return;
            }
            from = hit + 1;
        }
    }

    private static string? ExtractPreview(byte[] buf, int hit, string enc, int maxBytes)
    {
        try
        {
            int avail = Math.Min(maxBytes, buf.Length - hit);
            if (avail <= 0) return null;
            string decoded = enc == "utf16le"
                ? Encoding.Unicode.GetString(buf, hit, avail - (avail % 2))
                : Encoding.ASCII.GetString(buf, hit, avail);

            var sb = new StringBuilder(decoded.Length);
            foreach (var ch in decoded)
                sb.Append(char.IsControl(ch) ? ' ' : ch);
            string preview = sb.ToString().Trim();
            return preview.Length == 0 ? null : preview;
        }
        catch { return null; }
    }
}

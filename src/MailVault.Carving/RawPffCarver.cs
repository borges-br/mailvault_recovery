using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Carving;

/// <summary>
/// Orquestrador do carver: Phase 1 (RawArtifactScanner, sinais físicos) → Phase 2 (classificação por
/// janela física + exportação OPCIONAL de EML parcial, gated por options.Export + threshold). Read-only.
/// </summary>
public sealed class RawPffCarver
{
    public async Task<CarvePipelineResult> CarveAsync(string filePath, string outputDir, CarveOptions options, CancellationToken ct)
    {
        var scan = await new RawArtifactScanner().ScanAsync(filePath, options, ct);

        var classified = new List<ClassifiedCandidate>();
        var classCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var notes = new List<string>(scan.Notes);
        int exported = 0, seq = 0;

        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 1 << 20, useAsync: true);

            foreach (var cand in scan.Candidates)
            {
                ct.ThrowIfCancellationRequested();

                long start = Math.Max(0, cand.Offset - options.PreWindowBytes);
                long end = Math.Min(fs.Length, cand.Offset + options.PostWindowBytes);
                int len = (int)(end - start);
                if (len <= 0) continue;
                var win = new byte[len];
                fs.Position = start;
                int rd = await ReadFullAsync(fs, win, ct);
                if (rd < len) Array.Resize(ref win, rd);

                var cc = CarveFieldExtractor.Classify(win, cand.Offset, cand.Encoding, options);

                bool shouldExport = options.Export &&
                    (cc.Classification == CarveClass.Mail ||
                     (cc.Classification == CarveClass.Orphan && options.ExportOrphans));
                if (shouldExport)
                {
                    try
                    {
                        var rel = await CarvedMessageBuilder.ExportAsync(cc, outputDir, ++seq, ct);
                        cc = cc with { ExportedRelativePath = rel };
                        exported++;
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"Falha ao exportar candidato @{cand.Offset}: {ex.Message}");
                    }
                }

                classified.Add(cc);
                string key = cc.Classification.ToString();
                classCounts[key] = (classCounts.TryGetValue(key, out var n) ? n : 0) + 1;
            }
        }
        catch (OperationCanceledException)
        {
            notes.Add("Classificação cancelada; resultados parciais preservados.");
        }
        catch (Exception ex)
        {
            notes.Add($"Falha na fase de classificação: {ex.GetType().Name}: {ex.Message}");
        }

        return new CarvePipelineResult(
            scan.SourcePath, scan.FileSizeBytes, scan.BytesScanned, scan.HeaderIsPff, scan.HeaderSummary,
            scan.TotalCandidates, scan.CandidatesByKind, classCounts, exported, options.Export,
            classified, scan.ElapsedSeconds, scan.Status, notes);
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(total), ct);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }
}

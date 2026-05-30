using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Carving;

/// <summary>Camada E: grava o relatório de carving (JSON + Markdown). Report-only — sem EML no 3c.1.</summary>
public static class CarvingReportWriter
{
    public const string JsonName = "_mailvault-carving-report.json";
    public const string MarkdownName = "_mailvault-carving-report.md";

    public static async Task WriteAsync(CarveResult r, string outputDir, int maxCandidatesInReport, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var sample = r.Candidates.Take(maxCandidatesInReport).ToList();

        var data = new
        {
            sourcePath = r.SourcePath,
            fileSizeBytes = r.FileSizeBytes,
            bytesScanned = r.BytesScanned,
            headerIsPff = r.HeaderIsPff,
            headerSummary = r.HeaderSummary,
            status = r.Status.ToString(),
            elapsedSeconds = r.ElapsedSeconds,
            totalCandidates = r.TotalCandidates,
            candidatesByKind = r.CandidatesByKind,
            candidatesInReport = sample.Count,
            candidates = sample.Select(c => new
            {
                offset = c.Offset,
                kind = c.Kind,
                encoding = c.Encoding,
                confidence = c.Confidence,
                preview = c.Preview
            }),
            notes = r.Notes,
            disclaimer = "Report-only (Milestone 3c.1): candidatos são SINAIS FÍSICOS, não mensagens recuperadas. " +
                         "Nenhum EML foi exportado nesta fase."
        };

        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(outputDir, JsonName), json, ct);

        var sb = new StringBuilder();
        sb.AppendLine("# Relatório de Carving (Raw Artifact Scan) — MailVault");
        sb.AppendLine();
        sb.AppendLine($"- **Arquivo:** `{r.SourcePath}`");
        sb.AppendLine($"- **Tamanho:** {r.FileSizeBytes:N0} bytes · **Escaneado:** {r.BytesScanned:N0} bytes");
        sb.AppendLine($"- **Header:** {r.HeaderSummary} (PFF/!BDN: {(r.HeaderIsPff ? "presente" : "AUSENTE")})");
        sb.AppendLine($"- **Status:** {r.Status} · **Tempo:** {r.ElapsedSeconds:F2}s");
        sb.AppendLine($"- **Total de candidatos:** {r.TotalCandidates}");
        sb.AppendLine();
        sb.AppendLine("> Report-only (3c.1): candidatos são **sinais físicos**, não mensagens recuperadas. Nenhum EML exportado.");
        sb.AppendLine();
        sb.AppendLine("## Candidatos por tipo");
        sb.AppendLine();
        if (r.CandidatesByKind.Count == 0) sb.AppendLine("_Nenhum candidato._");
        else
        {
            sb.AppendLine("| Tipo | Ocorrências |");
            sb.AppendLine("|---|---:|");
            foreach (var kv in r.CandidatesByKind.OrderByDescending(k => k.Value))
                sb.AppendLine($"| {kv.Key} | {kv.Value} |");
        }
        sb.AppendLine();
        sb.AppendLine($"## Amostra de candidatos (até {maxCandidatesInReport})");
        sb.AppendLine();
        if (sample.Count == 0) sb.AppendLine("_Nenhum._");
        else
        {
            sb.AppendLine("| Offset | Tipo | Enc | Conf | Preview |");
            sb.AppendLine("|---:|---|---|---:|---|");
            foreach (var c in sample)
            {
                string prev = (c.Preview ?? "").Replace("|", "/").Replace("\n", " ").Replace("\r", " ");
                if (prev.Length > 60) prev = prev.Substring(0, 57) + "...";
                sb.AppendLine($"| {c.Offset} | {c.Kind} | {c.Encoding} | {c.Confidence} | {prev} |");
            }
        }
        if (r.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Notas / limitações");
            sb.AppendLine();
            foreach (var n in r.Notes) sb.AppendLine($"- {n}");
        }
        await File.WriteAllTextAsync(Path.Combine(outputDir, MarkdownName), sb.ToString(), Encoding.UTF8);
    }
}

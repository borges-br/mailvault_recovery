using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Carving;

/// <summary>Camada E: grava o relatório de carving (JSON + Markdown) do pipeline classificado.</summary>
public static class CarvingReportWriter
{
    public const string JsonName = "_mailvault-carving-report.json";
    public const string MarkdownName = "_mailvault-carving-report.md";

    public static async Task WriteAsync(CarvePipelineResult r, string outputDir, int maxInReport, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var sample = r.Candidates.Take(maxInReport).ToList();

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
            classificationCounts = r.ClassificationCounts,
            exportEnabled = r.ExportEnabled,
            exportedCount = r.ExportedCount,
            candidatesInReport = sample.Count,
            candidates = sample.Select(c => new
            {
                offset = c.Offset,
                encoding = c.Encoding,
                classification = c.Classification.ToString(),
                score = c.Score,
                subject = c.Subject,
                fromEmail = c.FromEmail,
                toEmail = c.ToEmail,
                date = c.DateText,
                bodySnippet = Trunc(c.BodySnippet, 200),
                reason = c.Reason,
                exported = c.ExportedRelativePath
            }),
            notes = r.Notes,
            disclaimer = "Carving: candidatos são SINAIS FÍSICOS classificados por heurística. EMLs exportados são " +
                         "PARCIAIS (headers sintéticos X-MailVault-*), nunca cópias fiéis. System = item interno do OST."
        };

        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(outputDir, JsonName), json, ct);

        var sb = new StringBuilder();
        sb.AppendLine("# Relatório de Carving — MailVault");
        sb.AppendLine();
        sb.AppendLine($"- **Arquivo:** `{r.SourcePath}`");
        sb.AppendLine($"- **Tamanho:** {r.FileSizeBytes:N0} · **Escaneado:** {r.BytesScanned:N0} bytes");
        sb.AppendLine($"- **Header:** {r.HeaderSummary} (!BDN: {(r.HeaderIsPff ? "presente" : "AUSENTE")})");
        sb.AppendLine($"- **Status:** {r.Status} · **Tempo:** {r.ElapsedSeconds:F2}s · **Candidatos:** {r.TotalCandidates}");
        sb.AppendLine($"- **Export habilitado:** {r.ExportEnabled} · **EMLs parciais exportados:** {r.ExportedCount}");
        sb.AppendLine();
        sb.AppendLine("## Classificação");
        sb.AppendLine();
        if (r.ClassificationCounts.Count == 0) sb.AppendLine("_Nenhum candidato classificado._");
        else
        {
            sb.AppendLine("| Classe | Qtd | Significado |");
            sb.AppendLine("|---|---:|---|");
            foreach (var kv in r.ClassificationCounts.OrderByDescending(k => k.Value))
                sb.AppendLine($"| {kv.Key} | {kv.Value} | {ClassMeaning(kv.Key)} |");
        }
        sb.AppendLine();
        sb.AppendLine("> EMLs exportados são **parciais** (pasta `Recovered/Carved/Partial` ou `Orphaned Items`, headers `X-MailVault-*`). " +
                      "`System` = item interno do OST (não exportado). `LocateOnly` = marcador sem campos legíveis (não exportado).");
        sb.AppendLine();
        sb.AppendLine($"## Amostra de candidatos (até {maxInReport})");
        sb.AppendLine();
        if (sample.Count == 0) sb.AppendLine("_Nenhum._");
        else
        {
            sb.AppendLine("| Offset | Classe | Score | Assunto | E-mail | Data | Exportado |");
            sb.AppendLine("|---:|---|---:|---|---|---|---|");
            foreach (var c in sample)
                sb.AppendLine($"| {c.Offset} | {c.Classification} | {c.Score} | {Cell(c.Subject)} | {Cell(c.FromEmail)} | {Cell(c.DateText)} | {Cell(c.ExportedRelativePath)} |");
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

    private static string ClassMeaning(string c) => c switch
    {
        "Mail" => "evidência suficiente → EML parcial em Partial/",
        "Orphan" => "evidência parcial → Orphaned Items/",
        "System" => "item interno do OST (não é e-mail) → descartado",
        "LocateOnly" => "marcador sem campos legíveis → só relatório",
        _ => ""
    };

    private static string Cell(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        s = s.Replace("|", "/").Replace("\n", " ").Replace("\r", " ").Trim();
        return s.Length > 40 ? s.Substring(0, 37) + "..." : s;
    }

    private static string? Trunc(string? s, int n) =>
        s == null ? null : (s.Length > n ? s.Substring(0, n) : s);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Exporters.Eml;

namespace MailVault.Carving;

/// <summary>
/// Camada D (3c.3): monta um MailItem PARCIAL a partir de um cluster classificado e exporta como EML
/// (mesmo serializer do recover-eml), com headers sintéticos que deixam EXPLÍCITO ser recuperação parcial.
/// Nunca apresenta como mensagem completa. Sem anexos reconstruídos nesta fase.
/// </summary>
internal static class CarvedMessageBuilder
{
    private sealed class NoAttachments : IAttachmentContentProvider
    {
        public Task<Stream> OpenAttachmentStreamAsync(MessageId m, AttachmentId a, CancellationToken ct)
            => throw new NotSupportedException("Carving 3c.3 não reconstrói anexos.");
    }

    /// <summary>Exporta e retorna o caminho relativo (sob outputDir), ou null se não exportou.</summary>
    public static async Task<string?> ExportAsync(ClassifiedCandidate c, string outputDir, int seq, CancellationToken ct)
    {
        string subFolder = c.Classification == CarveClass.Mail
            ? Path.Combine("Recovered", "Carved", "Partial")
            : Path.Combine("Recovered", "Carved", "Orphaned Items");
        string dir = Path.Combine(outputDir, subFolder);
        Directory.CreateDirectory(dir);

        string syntheticHeaders =
            "X-MailVault-Recovery: carved-partial\r\n" +
            $"X-MailVault-Confidence: {c.Score}\r\n" +
            $"X-MailVault-Classification: {c.Classification}\r\n" +
            $"X-MailVault-SourceOffset: {c.Offset}\r\n";

        var item = new MailItem(
            InternalId: $"carved-{c.Offset}",
            InternetMessageId: $"<carved-{c.Offset}@mailvault.local>",
            Subject: c.Subject ?? "(assunto não recuperado)",
            From: new MailAddressRef(null, c.FromEmail ?? "carved-unknown@mailvault.local"),
            To: c.ToEmail != null ? new List<MailAddressRef> { new(null, c.ToEmail) } : new List<MailAddressRef>(),
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: null,
            ReceivedAt: null,
            PlainTextBody: BuildBody(c),
            HtmlBody: null,
            Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string> { { "PR_TRANSPORT_MESSAGE_HEADERS", syntheticHeaders } },
            Issues: Array.Empty<ExtractionIssue>());

        string name = $"{seq:D6}_{Sanitize(item.Subject)}.eml";
        string path = Path.Combine(dir, name);
        var exporter = new EmlExporter();
        await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            await exporter.ExportMessageAsync(item, new NoAttachments(), fs, ct);

        return Path.Combine(subFolder, name);
    }

    private static string BuildBody(ClassifiedCandidate c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("*** MENSAGEM PARCIALMENTE RECUPERADA POR CARVING — NÃO É CÓPIA FIEL ***");
        sb.AppendLine($"Offset físico: {c.Offset} · Confiança: {c.Score} · Classificação: {c.Classification}");
        sb.AppendLine($"Motivo: {c.Reason}");
        sb.AppendLine();
        if (c.DateText != null) sb.AppendLine($"[data detectada] {c.DateText}");
        if (c.FromEmail != null) sb.AppendLine($"[e-mail detectado] {c.FromEmail}");
        if (c.ToEmail != null) sb.AppendLine($"[e-mail detectado] {c.ToEmail}");
        sb.AppendLine();
        sb.AppendLine("--- Fragmento de corpo recuperado (best-effort) ---");
        sb.AppendLine(c.BodySnippet ?? "(corpo não recuperado)");
        return sb.ToString();
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (clean.Length > 60) clean = clean.Substring(0, 57) + "...";
        return string.IsNullOrWhiteSpace(clean) ? "carved" : clean;
    }
}

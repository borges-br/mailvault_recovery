using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MailVault.Carving;

/// <summary>
/// Camada C+D (3c.2): a partir da janela física ao redor de um marcador, extrai runs legíveis,
/// classifica (Mail / Orphan / System / LocateOnly) e atribui um score de confiança. Conservador
/// por design — itens internos do OST caem na denylist e NÃO viram e-mail.
/// </summary>
internal static class CarveFieldExtractor
{
    private static readonly string[] SystemMarkers =
    {
        "Outlook Message Manager", "MessageManager", "IPM.MessageManager",
        "Pending Message Delete", "Pending Folder Delete", "Offline Message",
        "(KEY:", "IPM.Microsoft", "IPM.Configuration", "IPM.Aggregator"
    };

    private static readonly Regex EmailRx =
        new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    private static readonly Regex DateRx =
        new(@"\b(\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{2,4}|(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun),?\s+\d{1,2}\s+\w{3,9}\s+\d{4})\b",
            RegexOptions.Compiled);

    public static ClassifiedCandidate Classify(byte[] window, long offset, string encoding, CarveOptions o)
    {
        var runs = ExtractRuns(window, 5);
        string joined = string.Join("  ", runs);

        bool isSystem = SystemMarkers.Any(m => joined.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);

        var emails = EmailRx.Matches(joined).Select(m => m.Value)
            .Where(e => !e.Contains("@mailvault", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dateMatch = DateRx.Match(joined);
        string? date = dateMatch.Success ? dateMatch.Value : null;

        string? subject = runs.Where(IsSubjectLike).OrderByDescending(r => r.Length).FirstOrDefault();

        var bodyRuns = runs.Where(r => r.Length >= 40 && IsBodyLike(r)).ToList();
        string? body = bodyRuns.Count > 0 ? string.Join("\n", bodyRuns) : null;
        if (body != null && body.Length > o.MaxBodyChars) body = body.Substring(0, o.MaxBodyChars);

        int score = 0;
        if (subject != null) score += 40;
        if (emails.Count >= 1) score += 20;
        if (emails.Count >= 2) score += 10;
        if (body != null) score += 20;
        if (date != null) score += 10;

        CarveClass cls;
        string reason;
        if (isSystem) { cls = CarveClass.System; score = 0; reason = "Item interno do OST (denylist de sistema) — não é e-mail."; }
        else if (score >= o.MinConfidence) { cls = CarveClass.Mail; reason = $"Evidência suficiente (score {score})."; }
        else if (score >= 20 && (subject != null || body != null)) { cls = CarveClass.Orphan; reason = $"Evidência parcial (score {score})."; }
        else { cls = CarveClass.LocateOnly; reason = "Marcador presente, sem campos legíveis suficientes."; }

        return new ClassifiedCandidate(
            offset, encoding, cls, score, subject,
            emails.ElementAtOrDefault(0), emails.ElementAtOrDefault(1), date, body, reason, null);
    }

    private static List<string> ExtractRuns(byte[] win, int minLen)
    {
        var result = new List<string>();
        var sb = new StringBuilder();

        // UTF-16LE: pares (imprimível, 0x00)
        for (int i = 0; i + 1 < win.Length; i += 2)
        {
            byte lo = win[i], hi = win[i + 1];
            if (hi == 0 && lo >= 0x20 && lo < 0x7F) sb.Append((char)lo);
            else { if (sb.Length >= minLen) result.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length >= minLen) result.Add(sb.ToString());

        // ASCII
        sb.Clear();
        foreach (var b in win)
        {
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else { if (sb.Length >= minLen) result.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length >= minLen) result.Add(sb.ToString());

        return result;
    }

    private static bool IsFolderOrPath(string r) =>
        r.Contains('/') || r.Contains('\\') ||
        r is "Localizador" or "IPM_SUBTREE" or "NON_IPM_SUBTREE" or "Caixa de entrada" ||
        r.StartsWith("Raiz", StringComparison.OrdinalIgnoreCase) ||
        r.StartsWith("IPF.", StringComparison.OrdinalIgnoreCase) ||
        r.StartsWith("IPM.", StringComparison.OrdinalIgnoreCase);

    private static bool IsHexKey(string r) => r.Length >= 8 && r.All(Uri.IsHexDigit);

    private static bool IsSubjectLike(string r)
    {
        if (r.Length < 8) return false;
        if (IsFolderOrPath(r) || IsHexKey(r)) return false;
        if (EmailRx.IsMatch(r)) return false;
        if (SystemMarkers.Any(m => r.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
        int letters = r.Count(char.IsLetter);
        return letters >= 6 && (r.Contains(' ') || letters >= r.Length * 0.6);
    }

    private static bool IsBodyLike(string r) => IsSubjectLike(r) || r.Count(char.IsLetter) >= 30;
}

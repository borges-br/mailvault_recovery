using System;
using System.Collections.Generic;

namespace MailVault.Core.Normalization;

public static class BodyPreviewSanitizer
{
    public static string? Sanitize(string? textBody, string? htmlBody, int maxLines = 30)
    {
        string? source = !string.IsNullOrEmpty(textBody) ? textBody : htmlBody;
        if (string.IsNullOrEmpty(source)) return null;

        var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int count = Math.Min(lines.Length, maxLines);
        var resultLines = new List<string>(count);

        for (int i = 0; i < count; i++)
        {
            string line = lines[i].Trim();
            // Safety truncate wide lines
            if (line.Length > 200)
            {
                line = line.Substring(0, 197) + "...";
            }
            resultLines.Add(line);
        }

        string preview = string.Join("\n", resultLines);
        if (lines.Length > maxLines)
        {
            preview += $"\n[... PREVIEW TRUNCADO - {lines.Length - maxLines} LINHAS OCULTAS ...]";
        }

        return preview;
    }
}

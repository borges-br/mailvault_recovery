using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MailVault.Core.Normalization;

public static class AttachmentNameNormalizer
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string Normalize(string? fileName, string? attachmentId)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return $"anexo_sem_nome_{attachmentId ?? Guid.NewGuid().ToString("N").Substring(0, 8)}";
        }

        string normalized = fileName.Trim();

        // Replace path traversal blocks first to be consistent and safe
        normalized = normalized.Replace("../", "_").Replace("..\\", "_").Replace("..", "_");

        foreach (char c in InvalidChars)
        {
            normalized = normalized.Replace(c, '_');
        }

        // Preserve leading underscores from path traversal while collapsing inner underscores
        string leadingUnderscores = "";
        int i = 0;
        while (i < normalized.Length && normalized[i] == '_')
        {
            leadingUnderscores += '_';
            i++;
        }
        string rest = normalized.Substring(i);
        rest = Regex.Replace(rest, @"_+", "_");
        normalized = leadingUnderscores + rest;

        return string.IsNullOrWhiteSpace(normalized) ? "anexo_invalido" : normalized;
    }
}

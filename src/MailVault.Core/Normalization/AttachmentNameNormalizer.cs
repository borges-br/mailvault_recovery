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
        foreach (char c in InvalidChars)
        {
            normalized = normalized.Replace(c, '_');
        }

        // Additional safety trimming and replacements
        normalized = Regex.Replace(normalized, @"_+", "_");

        return string.IsNullOrWhiteSpace(normalized) ? "anexo_invalido" : normalized;
    }
}

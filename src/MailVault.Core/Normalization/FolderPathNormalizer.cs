using System;

namespace MailVault.Core.Normalization;

public static class FolderPathNormalizer
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Root";

        // Outlook paths use backslash as separator, normalize to forward slash for uniform indexing
        string normalized = path.Replace("\\", "/").Trim();

        // Remove duplicate slashes
        while (normalized.Contains("//"))
        {
            normalized = normalized.Replace("//", "/");
        }

        // Trim leading and trailing slashes
        normalized = normalized.Trim('/');

        return string.IsNullOrEmpty(normalized) ? "Root" : normalized;
    }
}

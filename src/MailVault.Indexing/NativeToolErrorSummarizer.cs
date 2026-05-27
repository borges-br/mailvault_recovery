using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MailVault.Indexing;

public sealed class ErrorSignature
{
    public string Signature { get; set; } = string.Empty;
    public int Count { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
}

public static class NativeToolErrorSummarizer
{
    public static List<ErrorSignature> Summarize(string stderr, out string categoryClassified)
    {
        categoryClassified = "Unknown";
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return new List<ErrorSignature>();
        }

        var lines = stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var signatures = new Dictionary<string, ErrorSignature>();

        bool hasLocalDescriptor = false;
        bool hasAttachmentFailure = false;

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Sanitization: If a line resembles email body content (extremely rare in libpff stderr), bypass or mask it.
            // libpff stderr lines typically start with libpff_ or are purely technical.
            if (trimmed.Length > 200 && !trimmed.Contains("libpff_", StringComparison.OrdinalIgnoreCase) && !trimmed.Contains("unable to", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "[SANITIZED LINE - SENSITIVE CONTENT MASKED]";
            }

            // Normalize dynamic identifiers (e.g. "identifier: 1649" -> "identifier: <ID>")
            string signature = Regex.Replace(trimmed, @"identifier:\s*\d+", "identifier: <ID>");
            signature = Regex.Replace(signature, @"identifier\s+\d+", "identifier <ID>");
            signature = Regex.Replace(signature, @":\s*\d+", ": <NUM>");
            signature = Regex.Replace(signature, @"\b\d{4,}\b", "<VAL>"); // Mask large numbers

            if (signature.Contains("invalid local descriptors node", StringComparison.OrdinalIgnoreCase) ||
                signature.Contains("unable to retrieve local descriptor", StringComparison.OrdinalIgnoreCase))
            {
                hasLocalDescriptor = true;
            }
            if (signature.Contains("unable to determine attachments", StringComparison.OrdinalIgnoreCase))
            {
                hasAttachmentFailure = true;
            }

            string category = "Other";
            if (signature.Contains("local_descriptors", StringComparison.OrdinalIgnoreCase) || signature.Contains("local descriptor", StringComparison.OrdinalIgnoreCase))
            {
                category = "LocalDescriptor";
            }
            else if (signature.Contains("attachment", StringComparison.OrdinalIgnoreCase))
            {
                category = "AttachmentDescriptor";
            }

            if (signatures.TryGetValue(signature, out var existing))
            {
                existing.Count++;
            }
            else
            {
                signatures[signature] = new ErrorSignature
                {
                    Signature = signature,
                    Count = 1,
                    Category = category,
                    Severity = "WarningOrError"
                };
            }
        }

        var categories = new List<string>();
        if (hasLocalDescriptor)
        {
            categories.Add("LocalDescriptorCorruptionOrUnsupportedStructure");
        }
        if (hasAttachmentFailure)
        {
            categories.Add("AttachmentDescriptorFailure");
        }

        if (categories.Count > 0)
        {
            categoryClassified = string.Join(";", categories);
        }

        return signatures.Values.ToList();
    }

    public static string SanitizeAndLimitStderr(string stderr, int maxBytes = 2 * 1024 * 1024)
    {
        if (string.IsNullOrEmpty(stderr)) return string.Empty;

        // Clean up any potential email bodies (very rare in stderr butforensically safe)
        var lines = stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var sanitizedLines = new List<string>();

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 200 && !trimmed.Contains("libpff_", StringComparison.OrdinalIgnoreCase) && !trimmed.Contains("unable to", StringComparison.OrdinalIgnoreCase))
            {
                sanitizedLines.Add("[SANITIZED LINE - SENSITIVE CONTENT MASKED]");
            }
            else
            {
                sanitizedLines.Add(line);
            }
        }

        string result = string.Join(Environment.NewLine, sanitizedLines);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(result);

        if (bytes.Length > maxBytes)
        {
            // Truncate to maximum bytes and append warning
            byte[] truncated = new byte[maxBytes];
            Array.Copy(bytes, truncated, maxBytes);
            string truncatedString = System.Text.Encoding.UTF8.GetString(truncated);
            
            // Clean up last potentially broken UTF-8 character and append warning
            int lastNewLine = truncatedString.LastIndexOf(Environment.NewLine);
            if (lastNewLine > 0)
            {
                truncatedString = truncatedString.Substring(0, lastNewLine);
            }
            
            return truncatedString + Environment.NewLine + "[TECHNICAL WARNING: RAW STDERR TRUNCATED DUE TO 2MB FORENSIC LIMIT]";
        }

        return result;
    }
}

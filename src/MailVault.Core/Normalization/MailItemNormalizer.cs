using System;
using System.Collections.Generic;
using MailVault.Domain;

namespace MailVault.Core.Normalization;

public static class MailItemNormalizer
{
    public static MailItem Normalize(MailItem item, bool cachePreview = true)
    {
        var normalizedFrom = item.From != null 
            ? new MailAddressRef(item.From.Name?.Trim(), item.From.Address?.Trim()) 
            : null;

        var normalizedTo = new List<MailAddressRef>();
        if (item.To != null)
        {
            foreach (var r in item.To)
            {
                normalizedTo.Add(new MailAddressRef(r.Name?.Trim(), r.Address?.Trim()));
            }
        }

        var normalizedCc = new List<MailAddressRef>();
        if (item.Cc != null)
        {
            foreach (var r in item.Cc)
            {
                normalizedCc.Add(new MailAddressRef(r.Name?.Trim(), r.Address?.Trim()));
            }
        }

        var normalizedBcc = new List<MailAddressRef>();
        if (item.Bcc != null)
        {
            foreach (var r in item.Bcc)
            {
                normalizedBcc.Add(new MailAddressRef(r.Name?.Trim(), r.Address?.Trim()));
            }
        }

        // Timezone fallback: if zero offset, ensure UTC is clear, otherwise keep original
        DateTimeOffset? normalizedSent = item.SentAt;
        DateTimeOffset? normalizedReceived = item.ReceivedAt;

        // Preview cache calculation (body_preview truncates to 30 lines)
        string? bodyPreview = cachePreview 
            ? BodyPreviewSanitizer.Sanitize(item.PlainTextBody, item.HtmlBody) 
            : null;

        // Attachments normalization
        var normalizedAttachments = new List<AttachmentRef>();
        if (item.Attachments != null)
        {
            foreach (var att in item.Attachments)
            {
                string cleanName = AttachmentNameNormalizer.Normalize(att.FileName, att.InternalId);
                normalizedAttachments.Add(new AttachmentRef(
                    InternalId: att.InternalId,
                    FileName: cleanName,
                    ContentType: att.ContentType?.Trim(),
                    SizeBytes: att.SizeBytes,
                    ContentId: att.ContentId?.Trim(),
                    IsInline: att.IsInline
                ));
            }
        }

        // Technical details sanitization for issues (trunca a 500 chars para compliance forense)
        var normalizedIssues = new List<ExtractionIssue>();
        if (item.Issues != null)
        {
            foreach (var issue in item.Issues)
            {
                string? cleanDetails = issue.TechnicalDetails;
                if (cleanDetails != null && cleanDetails.Length > 500)
                {
                    cleanDetails = cleanDetails.Substring(0, 497) + "...";
                }

                normalizedIssues.Add(new ExtractionIssue(
                    Code: issue.Code,
                    Severity: issue.Severity,
                    Message: issue.Message,
                    ObjectId: issue.ObjectId,
                    TechnicalDetails: cleanDetails
                ));
            }
        }

        return new MailItem(
            InternalId: item.InternalId,
            InternetMessageId: item.InternetMessageId?.Trim(),
            Subject: item.Subject?.Trim(),
            From: normalizedFrom,
            To: normalizedTo,
            Cc: normalizedCc,
            Bcc: normalizedBcc,
            SentAt: normalizedSent,
            ReceivedAt: normalizedReceived,
            PlainTextBody: bodyPreview, // Truncate preview cached body in this normalized item
            HtmlBody: null,             // Avoid caching full HTML body
            Attachments: normalizedAttachments,
            RawProperties: item.RawProperties,
            Issues: normalizedIssues
        );
    }
}

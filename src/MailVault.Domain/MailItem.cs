using System;
using System.Collections.Generic;

namespace MailVault.Domain;

public sealed record MailItem(
    string InternalId,
    string? InternetMessageId,
    string? Subject,
    MailAddressRef? From,
    IReadOnlyList<MailAddressRef> To,
    IReadOnlyList<MailAddressRef> Cc,
    IReadOnlyList<MailAddressRef> Bcc,
    DateTimeOffset? SentAt,
    DateTimeOffset? ReceivedAt,
    string? PlainTextBody,
    string? HtmlBody,
    IReadOnlyList<AttachmentRef> Attachments,
    IReadOnlyDictionary<string, string> RawProperties,
    IReadOnlyList<ExtractionIssue> Issues
);

namespace MailVault.Domain;

public sealed record AttachmentRef(
    string InternalId,
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    string? ContentId,
    bool IsInline
);

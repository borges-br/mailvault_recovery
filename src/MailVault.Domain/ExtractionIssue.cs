namespace MailVault.Domain;

public sealed record ExtractionIssue(
    string Code,
    string Severity, // e.g. "Warning", "Error", "Info"
    string Message,
    string? ObjectId,
    string? TechnicalDetails
);

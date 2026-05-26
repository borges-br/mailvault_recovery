using System.Collections.Generic;

namespace MailVault.Domain;

public sealed record StoreMetadata(
    string SourcePath,
    long SizeBytes,
    string Sha256,
    string DetectedFormat,
    string ReaderName,
    IReadOnlyList<ExtractionIssue> Issues
);

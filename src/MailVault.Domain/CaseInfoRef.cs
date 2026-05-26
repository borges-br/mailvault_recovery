using System;

namespace MailVault.Domain;

public sealed record CaseInfoRef(
    string CaseId,
    string SourceFile,
    long SourceSizeBytes,
    string SourceSha256,
    string OperatorName,
    DateTimeOffset StartedAt,
    string AdapterName,
    string AdapterVersion
);

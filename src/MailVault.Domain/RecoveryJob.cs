using System;

namespace MailVault.Domain;

public sealed record RecoveryJob(
    string JobId,
    string CaseId,
    string SourceFilePath,
    string OutputDirectory,
    string OperatorName,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status
);

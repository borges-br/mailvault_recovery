using System;
using System.Collections.Generic;

namespace MailVault.Domain;

public sealed record RecoveryManifest(
    string CaseId,
    string SourceFile,
    long SourceSizeBytes,
    string SourceSha256,
    string OperatorName,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string ToolVersion,
    IReadOnlyList<string> Actions,
    IReadOnlyList<ExtractionIssue> Warnings
);

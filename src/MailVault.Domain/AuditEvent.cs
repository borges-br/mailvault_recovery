using System;

namespace MailVault.Domain;

public sealed record AuditEvent(
    string EventId,
    DateTimeOffset Timestamp,
    string Action,
    string OperatorName,
    string Details,
    string? FilePath = null,
    string? FileHash = null
);

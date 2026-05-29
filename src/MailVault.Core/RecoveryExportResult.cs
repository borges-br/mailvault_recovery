using System;
using System.Collections.Generic;

namespace MailVault.Core;

public sealed record RecoveryExportIssue(
    string? MessageId,
    string? FolderPath,
    string ErrorCode,
    string ErrorMessage,
    string? TechnicalDetails = null
);

/// <summary>Status final/terminal de uma sessão de exportação de recuperação.</summary>
public enum RecoveryExportStatus
{
    Completed,
    PartialCompleted,
    CancelledByUser,
    CancelledByTimeout,
    Failed
}

/// <summary>
/// Opções de execução: limites de escopo, cancelamento por timeout e cadência de checkpoint.
/// Todos opcionais — defaults preservam o comportamento anterior (exportar tudo).
/// </summary>
public sealed record RecoveryExportOptions(
    int? MaxMessages = null,
    int? MaxFolderMessages = null,
    double? TimeoutSeconds = null,
    int MessageTimeoutSeconds = 30,
    int CheckpointEveryMessages = 50,
    double CheckpointIntervalSeconds = 30,
    string? ProgressJsonPath = null,
    bool ForceFullMessageReRead = false);

/// <summary>
/// Métricas de performance da sessão. Permite otimizar com base em medição (não no escuro).
/// Tempos de etapa são totais acumulados (ms) ao longo de toda a execução.
/// </summary>
public sealed record RecoveryExportMetrics(
    double WallClockSeconds,
    double MessagesPerSecond,
    double AvgMillisecondsPerMessage,
    double MegabytesPerMinute,
    long BytesWritten,
    long LargestMessageBytes,
    string? LargestMessageName,
    long LargestAttachmentBytes,
    string? LargestAttachmentName,
    string? SlowestFolder,
    double SlowestFolderSeconds,
    double GetMessageMs,
    double SerializeWriteMs,
    double AttachmentMs,
    string SlowestStage);

public sealed record RecoveryExportResult(
    string SourcePath,
    string Engine,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string OutputDir,
    int TotalFolders,
    int TotalMessages,
    int ExportedMessages,
    int FailedMessages,
    int ExportedAttachments,
    int FailedAttachments,
    IReadOnlyList<RecoveryExportIssue> Errors,
    RecoveryExportStatus Status = RecoveryExportStatus.Completed,
    RecoveryExportMetrics? Metrics = null
);

public sealed record RecoveryExportProgress(
    string Phase,
    string CurrentFolder,
    int FoldersProcessed,
    int MessagesExported,
    int MessagesFailed,
    int AttachmentsExported,
    int AttachmentsFailed,
    string StatusMessage
);

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
    IReadOnlyList<RecoveryExportIssue> Errors
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

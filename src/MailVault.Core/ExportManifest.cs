using System;
using System.Collections.Generic;
using MailVault.Domain;

namespace MailVault.Core;

public sealed record ExportedMessageRecord(
    string MessageId,
    string FolderPath,
    string SubjectTruncated,
    string RelativePath,
    string Status,
    int AttachmentCount
);

public sealed record ExportManifest(
    string ExportId,
    string CaseId,
    string SourceFile,
    string SourceSha256,
    string AdapterName,
    string AdapterVersion,
    string ExportFormat,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string OutputDirectory,
    int FoldersSelected,
    int MessagesSelected,
    int MessagesExported,
    int MessagesFailed,
    int AttachmentsExported,
    int AttachmentsFailed,
    IReadOnlyList<ExtractionIssue> Issues,
    IReadOnlyList<ExportedMessageRecord> ExportedMessages,
    string ToolVersion
);

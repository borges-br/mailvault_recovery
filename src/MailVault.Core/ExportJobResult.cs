using System.Collections.Generic;
using MailVault.Domain;

namespace MailVault.Core;

public sealed record ExportJobResult(
    string ExportId,
    string Format,
    int FoldersSelected,
    int MessagesSelected,
    int MessagesExported,
    int MessagesFailed,
    int AttachmentsExported,
    int AttachmentsFailed,
    IReadOnlyList<ExtractionIssue> Issues,
    IReadOnlyList<string> ExportedFiles,
    long DurationMs
);

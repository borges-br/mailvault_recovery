using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MailVault.Validation;

public sealed record FolderValidationResult(
    [property: JsonPropertyName("folder_name")] string FolderName,
    [property: JsonPropertyName("indexed_messages")] int IndexedMessages,
    [property: JsonPropertyName("exported_messages")] int ExportedMessages,
    [property: JsonPropertyName("mismatch_count")] int MismatchCount
);

public sealed record ValidationIssue(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity, // "Warning" ou "Error"
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("object_id")] string? ObjectId
);

public sealed record ValidationReport(
    [property: JsonPropertyName("validation_id")] string ValidationId,
    [property: JsonPropertyName("case_id")] string CaseId,
    [property: JsonPropertyName("source_file_masked")] string SourceFileMasked,
    [property: JsonPropertyName("source_sha256")] string SourceSha256,
    [property: JsonPropertyName("adapter_name")] string AdapterName,
    [property: JsonPropertyName("adapter_version")] string AdapterVersion,
    [property: JsonPropertyName("export_id")] string ExportId,
    [property: JsonPropertyName("export_format")] string ExportFormat,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("indexed_messages")] int IndexedMessages,
    [property: JsonPropertyName("selected_messages")] int SelectedMessages,
    [property: JsonPropertyName("exported_messages")] int ExportedMessages,
    [property: JsonPropertyName("failed_messages")] int FailedMessages,
    [property: JsonPropertyName("indexed_attachments")] int IndexedAttachments,
    [property: JsonPropertyName("exported_attachments")] int ExportedAttachments,
    [property: JsonPropertyName("failed_attachments")] int FailedAttachments,
    [property: JsonPropertyName("empty_exported_files")] int EmptyExportedFiles,
    [property: JsonPropertyName("duplicate_output_names")] int DuplicateOutputNames,
    [property: JsonPropertyName("missing_expected_files")] int MissingExpectedFiles,
    [property: JsonPropertyName("path_safety_issues")] int PathSafetyIssues,
    [property: JsonPropertyName("folders_checked")] IReadOnlyList<string> FoldersChecked,
    [property: JsonPropertyName("folder_results")] IReadOnlyList<FolderValidationResult> FolderResults,
    [property: JsonPropertyName("warning_count")] int WarningCount,
    [property: JsonPropertyName("error_count")] int ErrorCount,
    [property: JsonPropertyName("status")] string Status, // "Passed", "PassedWithWarnings", "Failed"
    [property: JsonPropertyName("issues")] IReadOnlyList<ValidationIssue> Issues
);

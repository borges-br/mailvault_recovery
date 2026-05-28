using System.IO;

namespace MailVault.Core;

public sealed record ExportJobOptions(
    string CaseFolder,
    string Format,
    string OutputDir,
    string? FolderIdOrPath = null,
    int? Limit = null,
    int? Offset = null,
    bool IncludeAttachments = true,
    bool ExtractAttachments = false,
    bool PreserveFolderStructure = true,
    bool Overwrite = false,
    bool DryRun = false,
    bool SkipIntegrityCheck = false
);

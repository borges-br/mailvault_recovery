using System.Collections.Generic;

namespace MailVault.Domain;

public sealed record FolderNode(
    FolderId Id,
    FolderId? ParentId,
    string DisplayName,
    string FullPath,
    int? MessageCount,
    IReadOnlyList<FolderNode> Children
);

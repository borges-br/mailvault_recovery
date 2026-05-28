using System;

namespace MailVault.Domain;

public enum ItemType
{
    Mail,
    Calendar,
    Contact,
    Task,
    Journal,
    Note,
    Unknown
}

public enum ErrorStatus
{
    Success,
    Partial,
    Failed
}

public enum ExportStatus
{
    Pending,
    Exported,
    Failed,
    Skipped
}

public sealed class MailVaultMessageCanonical
{
    public string Id { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string Engine { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string SourceMessageId { get; set; } = string.Empty;
    public string InternetMessageId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Cc { get; set; } = string.Empty;
    public string Bcc { get; set; } = string.Empty;
    public DateTimeOffset? SentDate { get; set; }
    public DateTimeOffset? ReceivedDate { get; set; }
    public bool BodyTextAvailable { get; set; }
    public bool BodyHtmlAvailable { get; set; }
    public bool BodyRtfAvailable { get; set; }
    public bool HasAttachments { get; set; }
    public int AttachmentCount { get; set; }
    public ItemType ItemType { get; set; } = ItemType.Unknown;
    public string Flags { get; set; } = string.Empty;
    public string Categories { get; set; } = string.Empty;
    public ErrorStatus ErrorStatus { get; set; } = ErrorStatus.Success;
    public ExportStatus ExportStatus { get; set; } = ExportStatus.Pending;
    public string MapiPropertiesJsonExtra { get; set; } = string.Empty;
}

public sealed class MailVaultAttachmentCanonical
{
    public string Id { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsEmbeddedMessage { get; set; }
    public bool IsCorrupted { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

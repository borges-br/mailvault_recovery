using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using XstReader;

namespace MailVault.Adapters.XstReader;

public sealed class XstReaderMailStoreReader : IMailStoreReader
{
    private string? _filePath;
    private string? _rootFolderPath;

    public string ReaderName => "XstReader.Api Engine";

    public XstReaderMailStoreReader()
    {
    }

    public XstReaderMailStoreReader(string filePath)
    {
        _filePath = filePath;
    }

    public Task<StoreMetadata> InspectAsync(string filePath, CancellationToken ct)
    {
        _filePath = filePath;

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Arquivo PST/OST não encontrado para inspeção.", filePath);
        }

        var issues = new List<ExtractionIssue>();
        long size = 0;
        string format = "Unknown";

        try
        {
            var fileInfo = new FileInfo(filePath);
            size = fileInfo.Length;
            string ext = fileInfo.Extension.ToLowerInvariant();
            format = ext == ".ost" ? "OST (Offline Outlook Data)" : (ext == ".pst" ? "PST (Outlook Personal Information Store)" : "Non-standard data store");

            // Open briefly to verify XstReader can parse the header without exception
            using var xstFile = new XstFile(filePath);
            var root = xstFile.RootFolder;
            _rootFolderPath = root.Path;
        }
        catch (Exception ex)
        {
            issues.Add(new ExtractionIssue(
                Code: "MV-ERR-ADAPTER-INSPECT",
                Severity: "Error",
                Message: $"Falha ao inspecionar o arquivo PST/OST usando XstReader: {ex.Message}",
                ObjectId: Path.GetFileName(filePath),
                TechnicalDetails: ex.ToString()
            ));
        }

        var metadata = new StoreMetadata(
            SourcePath: filePath,
            SizeBytes: size,
            Sha256: string.Empty, // Will be computed in the orchestrator/CLI by streaming HashService
            DetectedFormat: format,
            ReaderName: ReaderName,
            Issues: issues
        );

        return Task.FromResult(metadata);
    }

    public async IAsyncEnumerable<FolderNode> EnumerateFoldersAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_filePath))
        {
            throw new InvalidOperationException("Reader não inicializado. Chame InspectAsync primeiro.");
        }

        using var xstFile = new XstFile(_filePath);
        var root = xstFile.RootFolder;
        _rootFolderPath = root.Path;

        foreach (var folder in root.Folders)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapFolderNode(folder, isRoot: true);
        }

        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<MailItem> EnumerateMessagesAsync(FolderId folderId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_filePath))
        {
            throw new InvalidOperationException("Reader não inicializado. Chame InspectAsync primeiro.");
        }

        using var xstFile = new XstFile(_filePath);
        var targetFolder = FindFolderByPath(xstFile.RootFolder, folderId.Value);

        if (targetFolder != null)
        {
            foreach (var msg in targetFolder.Messages)
            {
                ct.ThrowIfCancellationRequested();
                yield return MapMailItem(msg);
            }
        }

        await Task.CompletedTask;
    }

    public Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_filePath))
        {
            throw new InvalidOperationException("Reader não inicializado. Chame InspectAsync primeiro.");
        }

        using var xstFile = new XstFile(_filePath);
        var xstAttach = FindAttachmentByPath(xstFile.RootFolder, attachment.InternalId);
        if (xstAttach == null)
        {
            throw new FileNotFoundException($"Anexo com ID {attachment.InternalId} não foi encontrado.");
        }

        var memoryStream = new MemoryStream();
        xstAttach.SaveToStream(memoryStream);
        memoryStream.Position = 0;
        return Task.FromResult<Stream>(memoryStream);
    }

    public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_filePath))
        {
            throw new InvalidOperationException("Reader não inicializado. Chame InspectAsync primeiro.");
        }

        using var xstFile = new XstFile(_filePath);
        var xstAttach = FindAttachmentByPath(xstFile.RootFolder, attachmentId.Value);
        if (xstAttach == null)
        {
            throw new FileNotFoundException($"Anexo com ID {attachmentId.Value} não foi encontrado.");
        }

        var memoryStream = new MemoryStream();
        xstAttach.SaveToStream(memoryStream);
        memoryStream.Position = 0;
        return Task.FromResult<Stream>(memoryStream);
    }

    public Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_filePath))
        {
            return Task.FromResult(OperationResult<MailItem>.Failure(new ExtractionIssue(
                Code: "MV-ERR-UNINITIALIZED",
                Severity: "Critical",
                Message: "Reader não inicializado. Chame InspectAsync primeiro.",
                ObjectId: messageId.Value,
                TechnicalDetails: null
            )));
        }

        try
        {
            using var xstFile = new XstFile(_filePath);
            var msg = FindMessageByPath(xstFile.RootFolder, messageId.Value);

            if (msg == null)
            {
                return Task.FromResult(OperationResult<MailItem>.Failure(new ExtractionIssue(
                    Code: "MV-ERR-MSG-NOT-FOUND",
                    Severity: "Error",
                    Message: $"Mensagem com ID '{messageId.Value}' não foi encontrada no arquivo.",
                    ObjectId: messageId.Value,
                    TechnicalDetails: null
                )));
            }

            var mailItem = MapMailItem(msg);
            return Task.FromResult(OperationResult<MailItem>.Ok(mailItem));
        }
        catch (Exception ex)
        {
            return Task.FromResult(OperationResult<MailItem>.Failure(new ExtractionIssue(
                Code: "MV-ERR-ADAPTER-GETMSG",
                Severity: "Error",
                Message: $"Falha ao ler mensagem por ID do XstReader: {ex.Message}",
                ObjectId: messageId.Value,
                TechnicalDetails: ex.ToString()
            )));
        }
    }

    // Helper folder mapper
    private FolderNode MapFolderNode(XstFolder folder, bool isRoot = false)
    {
        var childrenList = new List<FolderNode>();
        foreach (var sub in folder.Folders)
        {
            childrenList.Add(MapFolderNode(sub, isRoot: false));
        }

        FolderId? parentId = null;
        if (!isRoot && folder.ParentFolder != null && 
            !string.IsNullOrEmpty(folder.ParentFolder.Path) && 
            folder.ParentFolder.Path != "\\")
        {
            parentId = new FolderId(folder.ParentFolder.Path);
        }

        return new FolderNode(
            Id: new FolderId(folder.Path),
            ParentId: parentId,
            DisplayName: folder.DisplayName ?? "Unamed Folder",
            FullPath: folder.Path,
            MessageCount: folder.ContentCount,
            Children: childrenList
        );
    }

    // Helper message mapper
    private MailItem MapMailItem(XstMessage msg)
    {
        var fromRef = !string.IsNullOrEmpty(msg.From) ? new MailAddressRef(msg.From, null) : null;
        
        var toList = new List<MailAddressRef>();
        var ccList = new List<MailAddressRef>();
        var bccList = new List<MailAddressRef>();

        if (msg.Recipients != null)
        {
            if (msg.Recipients.To != null)
            {
                foreach (var rec in msg.Recipients.To)
                {
                    toList.Add(new MailAddressRef(rec.DisplayName, rec.Address));
                }
            }
            if (msg.Recipients.Cc != null)
            {
                foreach (var rec in msg.Recipients.Cc)
                {
                    ccList.Add(new MailAddressRef(rec.DisplayName, rec.Address));
                }
            }
            if (msg.Recipients.Bcc != null)
            {
                foreach (var rec in msg.Recipients.Bcc)
                {
                    bccList.Add(new MailAddressRef(rec.DisplayName, rec.Address));
                }
            }
        }

        string? plainText = null;
        string? htmlText = null;

        if (msg.Body != null)
        {
            string bodyFormat = msg.Body.Format.ToString();
            if (bodyFormat.Contains("Html", StringComparison.OrdinalIgnoreCase))
            {
                htmlText = msg.Body.Text;
            }
            else
            {
                plainText = msg.Body.Text;
            }
        }

        var attachList = new List<AttachmentRef>();
        if (msg.Attachments != null)
        {
            foreach (var att in msg.Attachments)
            {
                attachList.Add(new AttachmentRef(
                    InternalId: att.Path,
                    FileName: att.FileName,
                    ContentType: null, // Mapped MAPI properties can be extracted for MIME type in subsequent steps
                    SizeBytes: att.Size,
                    ContentId: att.ContentId,
                    IsInline: att.IsInlineAttachment
                ));
            }
        }

        var rawProperties = new Dictionary<string, string>();
        var issues = new List<ExtractionIssue>();

        // Properties collection doesn't implement IEnumerable, keeping it empty for this step to preserve net10.0 compliance
        // msg.Properties mapping can be done via specific properties if required.

        return new MailItem(
            InternalId: msg.Path,
            InternetMessageId: msg.InternetMessageId,
            Subject: msg.Subject,
            From: fromRef,
            To: toList,
            Cc: ccList,
            Bcc: bccList,
            SentAt: msg.SubmittedTime.HasValue ? new DateTimeOffset(msg.SubmittedTime.Value) : null,
            ReceivedAt: msg.ReceivedTime.HasValue ? new DateTimeOffset(msg.ReceivedTime.Value) : null,
            PlainTextBody: plainText,
            HtmlBody: htmlText,
            Attachments: attachList,
            RawProperties: rawProperties,
            Issues: issues
        );
    }

    // Navigational helper methods
    private XstFolder? FindFolderByPath(XstFolder current, string path)
    {
        if (current.Path == path) return current;
        foreach (var sub in current.Folders)
        {
            var found = FindFolderByPath(sub, path);
            if (found != null) return found;
        }
        return null;
    }

    private XstMessage? FindMessageByPath(XstFolder current, string path)
    {
        foreach (var msg in current.Messages)
        {
            if (msg.Path == path) return msg;
        }
        foreach (var sub in current.Folders)
        {
            var found = FindMessageByPath(sub, path);
            if (found != null) return found;
        }
        return null;
    }

    private XstAttachment? FindAttachmentByPath(XstFolder current, string path)
    {
        foreach (var msg in current.Messages)
        {
            foreach (var att in msg.Attachments)
            {
                if (att.Path == path) return att;
            }
        }
        foreach (var sub in current.Folders)
        {
            var found = FindAttachmentByPath(sub, path);
            if (found != null) return found;
        }
        return null;
    }
}

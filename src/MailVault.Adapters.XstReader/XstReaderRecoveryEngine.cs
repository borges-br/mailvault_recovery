using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using XstReader;

namespace MailVault.Adapters.XstReader;

public sealed class XstReaderRecoveryEngine : IMailStoreReader, ISessionAwareMailStoreReader, IExtractionIssueSource, IMetadataOnlyAware
{
    public bool MetadataOnly { get; set; } = false;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<ExtractionIssue> _issues = new();
    private readonly Dictionary<string, XstFolder> _folderCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<MailAddressRef> EmptyAddressList = Array.Empty<MailAddressRef>();
    private static readonly IReadOnlyDictionary<string, string> EmptyProperties = new Dictionary<string, string>(0);

    private string? _filePath;
    private string? _rootFolderPath;
    private XstFile? _sessionFile;
    private XstFolder? _sessionRoot;

    // Índices path→objeto construídos uma única vez por sessão (lazy). Tornam
    // GetMessageAsync/OpenAttachment O(1) em vez de varrer/reparsear a árvore inteira
    // (re-materializando folder.Messages) a cada chamada — o gargalo da exportação.
    private Dictionary<string, XstMessage>? _messageIndex;
    private Dictionary<string, XstAttachment>? _attachmentIndex;

    public string ReaderName => "XstReaderRecoveryEngine";

    public async Task BeginReadSessionAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Arquivo PST/OST não encontrado para sessão de recuperação.", filePath);
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_sessionFile != null && string.Equals(_filePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DisposeSessionNoLock();

            _filePath = filePath;
            _sessionFile = new XstFile(filePath);
            _sessionRoot = _sessionFile.RootFolder;
            _rootFolderPath = _sessionRoot.Path;
            _folderCache.Clear();
            CacheFolder(_sessionRoot);
        }
        catch (Exception ex)
        {
            AddIssue("MV-ERR-XST-REC-SESSION-BEGIN", "Critical", "Falha ao iniciar sessão no motor de recuperação XstReader.", Path.GetFileName(filePath), ex);
            DisposeSessionNoLock();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task EndReadSessionAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            DisposeSessionNoLock();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoreMetadata> InspectAsync(string filePath, CancellationToken ct)
    {
        _filePath = filePath;

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Arquivo PST/OST não encontrado para inspeção.", filePath);
        }

        var issues = new List<ExtractionIssue>();
        long size = 0;
        string format = "Unknown";

        await _lock.WaitAsync(ct);
        try
        {
            var fileInfo = new FileInfo(filePath);
            size = fileInfo.Length;
            string ext = fileInfo.Extension.ToLowerInvariant();
            format = ext == ".ost" ? "OST (Offline Outlook Data)" : (ext == ".pst" ? "PST (Outlook Personal Information Store)" : "Non-standard data store");

            if (_sessionRoot != null)
            {
                _rootFolderPath = _sessionRoot.Path;
            }
            else
            {
                using var xstFile = new XstFile(filePath);
                _rootFolderPath = xstFile.RootFolder.Path;
            }
        }
        catch (Exception ex)
        {
            var issue = CreateIssue(
                "MV-ERR-ADAPTER-REC-INSPECT",
                "Error",
                $"Falha ao inspecionar usando XstReader: {ex.Message}",
                Path.GetFileName(filePath),
                ex);
            issues.Add(issue);
            AddIssue(issue);
        }
        finally
        {
            _lock.Release();
        }

        return new StoreMetadata(
            SourcePath: filePath,
            SizeBytes: size,
            Sha256: string.Empty,
            DetectedFormat: format,
            ReaderName: ReaderName,
            Issues: issues
        );
    }

    public async IAsyncEnumerable<FolderNode> EnumerateFoldersAsync([EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var folders = new List<FolderNode>();

        await _lock.WaitAsync(ct);
        try
        {
            var root = _sessionRoot;
            if (root == null && _filePath != null)
            {
                _sessionFile = new XstFile(_filePath);
                root = _sessionFile.RootFolder;
                _sessionRoot = root;
            }

            if (root != null)
            {
                _rootFolderPath = root.Path;
                _folderCache.Clear();
                CacheFolder(root);

                foreach (var folder in SafeFolders(root, root.Path))
                {
                    ct.ThrowIfCancellationRequested();
                    folders.Add(MapFolderNode(folder, isRoot: true, ct));
                }
            }
        }
        catch (Exception ex)
        {
            AddIssue("MV-ERR-XST-REC-FOLDER-ENUM", "Error", "Falha ao enumerar pastas com XstReader.", _rootFolderPath ?? Path.GetFileName(_filePath ?? ""), ex);
        }
        finally
        {
            _lock.Release();
        }

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            yield return folder;
        }
    }

    public async IAsyncEnumerable<MailItem> EnumerateMessagesAsync(FolderId folderId, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        XstFolder? targetFolder = null;

        await _lock.WaitAsync(ct);
        try
        {
            var root = _sessionRoot;
            if (root == null && _filePath != null)
            {
                _sessionFile = new XstFile(_filePath);
                root = _sessionFile.RootFolder;
                _sessionRoot = root;
            }

            if (root != null)
            {
                targetFolder = FindFolderByPath(root, folderId.Value);
            }
        }
        finally
        {
            _lock.Release();
        }

        if (targetFolder == null)
        {
            AddIssue("MV-WARN-XST-REC-FOLDER-NOT-FOUND", "Warning", "Pasta não encontrada no motor de recuperação.", folderId.Value, null);
            yield break;
        }

        List<XstMessage> msgList;
        await _lock.WaitAsync(ct);
        try
        {
            msgList = SafeMessages(targetFolder, folderId.Value).ToList();
        }
        finally
        {
            _lock.Release();
        }

        foreach (var msg in msgList)
        {
            ct.ThrowIfCancellationRequested();
            MailItem? mailItem = null;

            await _lock.WaitAsync(ct);
            try
            {
                mailItem = MapMailItem(msg, folderId.Value);
            }
            catch (Exception ex)
            {
                AddIssue("MV-ERR-XST-REC-MESSAGE-MAP", "Error", "Falha ao mapear mensagem no motor de recuperação.", SafeObjectId(() => msg.Path, folderId.Value), ex);
            }
            finally
            {
                _lock.Release();
            }

            if (mailItem != null)
            {
                yield return mailItem;
            }
        }

        // Release processed folder content cache to free memory!
        await _lock.WaitAsync(ct);
        try
        {
            targetFolder.ClearContents();
        }
        catch
        {
            // Best-effort cleanup
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct)
    {
        return await OpenAttachmentByIdAsync(attachment.InternalId, ct);
    }

    public async Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct)
    {
        return await OpenAttachmentByIdAsync(attachmentId.Value, ct);
    }

    public async Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var root = _sessionRoot;
            if (root == null && _filePath != null)
            {
                _sessionFile = new XstFile(_filePath);
                root = _sessionFile.RootFolder;
                _sessionRoot = root;
            }

            if (root == null)
            {
                return OperationResult<MailItem>.Failure(new ExtractionIssue("MV-ERR-REC-NO-SESSION", "Error", "Leitor não inicializado.", messageId.Value, null));
            }

            _messageIndex ??= BuildMessageIndex(root);
            if (!_messageIndex.TryGetValue(messageId.Value, out var msg) || msg == null)
            {
                return OperationResult<MailItem>.Failure(new ExtractionIssue(
                    Code: "MV-ERR-MSG-NOT-FOUND",
                    Severity: "Error",
                    Message: $"Mensagem com ID '{messageId.Value}' não encontrada no arquivo.",
                    ObjectId: messageId.Value,
                    TechnicalDetails: null
                ));
            }

            var mailItem = MapMailItem(msg, messageId.Value);
            return OperationResult<MailItem>.Ok(mailItem);
        }
        catch (Exception ex)
        {
            var issue = CreateIssue(
                "MV-ERR-ADAPTER-REC-GETMSG",
                "Error",
                $"Falha ao ler mensagem no motor de recuperação: {ex.Message}",
                messageId.Value,
                ex);
            AddIssue(issue);
            return OperationResult<MailItem>.Failure(issue);
        }
        finally
        {
            _lock.Release();
        }
    }

    public IReadOnlyList<ExtractionIssue> DrainIssues()
    {
        lock (_issues)
        {
            var copy = _issues.ToArray();
            _issues.Clear();
            return copy;
        }
    }

    private async Task<Stream> OpenAttachmentByIdAsync(string attachmentId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var root = _sessionRoot;
            if (root == null && _filePath != null)
            {
                _sessionFile = new XstFile(_filePath);
                root = _sessionFile.RootFolder;
                _sessionRoot = root;
            }

            if (root == null)
            {
                throw new InvalidOperationException("Sessão de leitura não iniciada.");
            }

            _attachmentIndex ??= BuildAttachmentIndex(root);
            if (!_attachmentIndex.TryGetValue(attachmentId, out var xstAttach) || xstAttach == null)
            {
                throw new FileNotFoundException($"Anexo com ID {attachmentId} não encontrado no arquivo.");
            }

            var memoryStream = new MemoryStream();
            xstAttach.SaveToStream(memoryStream);
            memoryStream.Position = 0;
            return memoryStream;
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            AddIssue("MV-ERR-XST-REC-ATTACHMENT", "Error", "Falha ao carregar anexo usando XstReader.", attachmentId, ex);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void DisposeSessionNoLock()
    {
        _folderCache.Clear();
        _messageIndex = null;
        _attachmentIndex = null;
        _sessionRoot = null;
        _sessionFile?.Dispose();
        _sessionFile = null;
    }

    private FolderNode MapFolderNode(XstFolder folder, bool isRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CacheFolder(folder);

        var childrenList = new List<FolderNode>();
        foreach (var sub in SafeFolders(folder, SafeObjectId(() => folder.Path, _rootFolderPath ?? "root")))
        {
            ct.ThrowIfCancellationRequested();
            childrenList.Add(MapFolderNode(sub, isRoot: false, ct));
        }

        FolderId? parentId = null;
        if (!isRoot)
        {
            string? parentPath = SafeString(() => folder.ParentFolder?.Path);
            if (!string.IsNullOrEmpty(parentPath) && parentPath != "\\")
            {
                parentId = new FolderId(parentPath);
            }
        }

        string folderPath = SafeString(() => folder.Path) ?? Guid.NewGuid().ToString("N");
        string displayName = SafeString(() => folder.DisplayName) ?? "Unnamed Folder";
        int messageCount = SafeInt(() => folder.ContentCount, 0, folderPath);

        return new FolderNode(
            Id: new FolderId(folderPath),
            ParentId: parentId,
            DisplayName: displayName,
            FullPath: folderPath,
            MessageCount: messageCount,
            Children: childrenList
        );
    }

    private MailItem MapMailItem(XstMessage msg, string folderId)
    {
        string internalId = SafeString(() => msg.Path) ?? $"{folderId}/{Guid.NewGuid():N}";
        var issues = new List<ExtractionIssue>();

        var fromRef = CreateAddress(SafeString(() => msg.From), null);
        var toList = MetadataOnly ? EmptyAddressList : SafeRecipients(() => msg.Recipients?.To, internalId, "To", issues);
        var ccList = MetadataOnly ? EmptyAddressList : SafeRecipients(() => msg.Recipients?.Cc, internalId, "Cc", issues);
        var bccList = MetadataOnly ? EmptyAddressList : SafeRecipients(() => msg.Recipients?.Bcc, internalId, "Bcc", issues);

        string? plainText = null;
        string? htmlText = null;
        if (!MetadataOnly)
        {
            try
            {
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
            }
            catch (Exception ex)
            {
                var issue = CreateIssue("MV-WARN-XST-REC-BODY", "Warning", "Falha ao ler corpo da mensagem; metadados preservados.", internalId, ex);
                issues.Add(issue);
                AddIssue(issue);
            }
        }

        var attachList = new List<AttachmentRef>();
        try
        {
            if (msg.Attachments != null)
            {
                foreach (var att in msg.Attachments)
                {
                    string attId = SafeString(() => att.Path) ?? $"{internalId}/attachment/{Guid.NewGuid():N}";
                    string? fileName = SafeString(() => att.FileName);

                    if (MetadataOnly)
                    {
                        // Skip Size/ContentId/IsInline reads — avoids extra PST/OST property table lookups
                        attachList.Add(new AttachmentRef(attId, fileName, null, null, null, false));
                    }
                    else
                    {
                        attachList.Add(new AttachmentRef(
                            InternalId: attId,
                            FileName: fileName,
                            ContentType: null,
                            SizeBytes: SafeNullableLong(() => att.Size, internalId),
                            ContentId: SafeString(() => att.ContentId),
                            IsInline: SafeBool(() => att.IsInlineAttachment, false, internalId)
                        ));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            var issue = CreateIssue("MV-WARN-XST-REC-ATTACHMENTS", "Warning", "Falha ao ler anexos da mensagem no motor de recuperação.", internalId, ex);
            issues.Add(issue);
            AddIssue(issue);
        }

        return new MailItem(
            InternalId: internalId,
            InternetMessageId: SafeString(() => msg.InternetMessageId),
            Subject: SafeString(() => msg.Subject),
            From: fromRef,
            To: toList,
            Cc: ccList,
            Bcc: bccList,
            SentAt: SafeDate(() => msg.SubmittedTime),
            ReceivedAt: SafeDate(() => msg.ReceivedTime),
            PlainTextBody: plainText,
            HtmlBody: htmlText,
            Attachments: attachList,
            RawProperties: EmptyProperties,
            Issues: issues.Count > 0 ? (IReadOnlyList<ExtractionIssue>)issues : Array.Empty<ExtractionIssue>()
        );
    }

    private IEnumerable<XstFolder> SafeFolders(XstFolder folder, string objectId)
    {
        try
        {
            return folder.Folders?.ToArray() ?? Array.Empty<XstFolder>();
        }
        catch (Exception ex)
        {
            AddIssue("MV-ERR-XST-REC-FOLDER-CHILDREN", "Error", "Falha ao ler subpastas no motor de recuperação.", objectId, ex);
            return Array.Empty<XstFolder>();
        }
    }

    private IEnumerable<XstMessage> SafeMessages(XstFolder folder, string objectId)
    {
        try
        {
            return folder.Messages?.ToArray() ?? Array.Empty<XstMessage>();
        }
        catch (Exception ex)
        {
            AddIssue("MV-ERR-XST-REC-FOLDER-MESSAGES", "Error", "Falha ao ler mensagens no motor de recuperação.", objectId, ex);
            return Array.Empty<XstMessage>();
        }
    }

    private XstFolder? FindFolderByPath(XstFolder current, string path)
    {
        if (_folderCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        string? currentPath = SafeString(() => current.Path);
        if (string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            CacheFolder(current);
            return current;
        }

        foreach (var sub in SafeFolders(current, currentPath ?? path))
        {
            var found = FindFolderByPath(sub, path);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private XstMessage? FindMessageByPath(XstFolder current, string path)
    {
        foreach (var msg in SafeMessages(current, SafeObjectId(() => current.Path, path)))
        {
            if (string.Equals(SafeString(() => msg.Path), path, StringComparison.OrdinalIgnoreCase))
            {
                return msg;
            }
        }

        foreach (var sub in SafeFolders(current, SafeObjectId(() => current.Path, path)))
        {
            var found = FindMessageByPath(sub, path);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private XstAttachment? FindAttachmentByPath(XstFolder current, string path)
    {
        foreach (var msg in SafeMessages(current, SafeObjectId(() => current.Path, path)))
        {
            try
            {
                foreach (var att in msg.Attachments ?? Array.Empty<XstAttachment>())
                {
                    if (string.Equals(SafeString(() => att.Path), path, StringComparison.OrdinalIgnoreCase))
                    {
                        return att;
                    }
                }
            }
            catch (Exception ex)
            {
                AddIssue("MV-WARN-XST-REC-ATTACH-SEARCH", "Warning", "Falha ao procurar anexo em mensagem.", SafeObjectId(() => msg.Path, path), ex);
            }
        }

        foreach (var sub in SafeFolders(current, SafeObjectId(() => current.Path, path)))
        {
            var found = FindAttachmentByPath(sub, path);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private Dictionary<string, XstMessage> BuildMessageIndex(XstFolder root)
    {
        var index = new Dictionary<string, XstMessage>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<XstFolder>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            foreach (var msg in SafeMessages(folder, SafeObjectId(() => folder.Path, "msg-index")))
            {
                var p = SafeString(() => msg.Path);
                if (!string.IsNullOrEmpty(p)) index[p!] = msg;
            }
            foreach (var sub in SafeFolders(folder, SafeObjectId(() => folder.Path, "msg-index")))
            {
                stack.Push(sub);
            }
        }
        return index;
    }

    private Dictionary<string, XstAttachment> BuildAttachmentIndex(XstFolder root)
    {
        var index = new Dictionary<string, XstAttachment>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<XstFolder>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            foreach (var msg in SafeMessages(folder, SafeObjectId(() => folder.Path, "att-index")))
            {
                try
                {
                    foreach (var att in msg.Attachments ?? Enumerable.Empty<XstAttachment>())
                    {
                        var ap = SafeString(() => att.Path);
                        if (!string.IsNullOrEmpty(ap)) index[ap!] = att;
                    }
                }
                catch { /* mensagem sem anexos legíveis; ignora */ }
            }
            foreach (var sub in SafeFolders(folder, SafeObjectId(() => folder.Path, "att-index")))
            {
                stack.Push(sub);
            }
        }
        return index;
    }

    private void CacheFolder(XstFolder folder)
    {
        string? path = SafeString(() => folder.Path);
        if (!string.IsNullOrWhiteSpace(path))
        {
            _folderCache[path] = folder;
        }
    }

    private void AddIssue(string code, string severity, string message, string? objectId, Exception? ex)
    {
        AddIssue(CreateIssue(code, severity, message, objectId, ex));
    }

    private void AddIssue(ExtractionIssue issue)
    {
        lock (_issues)
        {
            _issues.Add(issue);
        }
    }

    private static ExtractionIssue CreateIssue(string code, string severity, string message, string? objectId, Exception? ex)
    {
        return new ExtractionIssue(
            Code: code,
            Severity: severity,
            Message: message,
            ObjectId: objectId,
            TechnicalDetails: ex == null ? null : $"{ex.GetType().Name}: {ex.Message}");
    }

    private static MailAddressRef? CreateAddress(string? name, string? address)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(address))
        {
            return null;
        }
        return new MailAddressRef(name, address);
    }

    private List<MailAddressRef> SafeRecipients(Func<IEnumerable<dynamic>?> getter, string objectId, string field, List<ExtractionIssue> issues)
    {
        var result = new List<MailAddressRef>();
        try
        {
            var recipients = getter();
            if (recipients == null)
            {
                return result;
            }

            foreach (var recipient in recipients)
            {
                result.Add(new MailAddressRef((string?)recipient.DisplayName, (string?)recipient.Address));
            }
        }
        catch (Exception ex)
        {
            var issue = CreateIssue("MV-WARN-XST-REC-RECIPIENTS", "Warning", $"Falha ao ler destinatários ({field}).", objectId, ex);
            issues.Add(issue);
            AddIssue(issue);
        }

        return result;
    }

    private int SafeInt(Func<int> getter, int fallback, string objectId)
    {
        try
        {
            return getter();
        }
        catch (Exception ex)
        {
            AddIssue("MV-WARN-XST-REC-INT-PROP", "Warning", "Falha ao ler propriedade numérica.", objectId, ex);
            return fallback;
        }
    }

    private long? SafeNullableLong(Func<long> getter, string objectId)
    {
        try
        {
            return getter();
        }
        catch (Exception ex)
        {
            AddIssue("MV-WARN-XST-REC-LONG-PROP", "Warning", "Falha ao ler tamanho do anexo.", objectId, ex);
            return null;
        }
    }

    private bool SafeBool(Func<bool> getter, bool fallback, string objectId)
    {
        try
        {
            return getter();
        }
        catch (Exception ex)
        {
            AddIssue("MV-WARN-XST-REC-BOOL-PROP", "Warning", "Falha ao ler flag booleana.", objectId, ex);
            return fallback;
        }
    }

    private static string? SafeString(Func<string?> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private static string SafeObjectId(Func<string?> getter, string fallback)
    {
        return SafeString(getter) ?? fallback;
    }

    private DateTimeOffset? SafeDate(Func<DateTime?> getter)
    {
        try
        {
            var value = getter();
            return value.HasValue ? new DateTimeOffset(value.Value) : null;
        }
        catch (Exception ex)
        {
            AddIssue("MV-WARN-XST-REC-DATE-PROP", "Warning", "Falha ao ler data da mensagem.", _filePath == null ? null : Path.GetFileName(_filePath), ex);
            return null;
        }
    }
}

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

public sealed class XstReaderMailStoreReader : IMailStoreReader, ISessionAwareMailStoreReader, IExtractionIssueSource, IMetadataOnlyAware
{
    public bool MetadataOnly { get; set; } = false;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly List<ExtractionIssue> _issues = new();
    private readonly Dictionary<string, XstFolder> _folderCache = new(StringComparer.OrdinalIgnoreCase);

    private string? _filePath;
    private string? _rootFolderPath;
    private XstFile? _sessionFile;
    private XstFolder? _sessionRoot;

    // Índices path→objeto construídos uma única vez por sessão (lazy). Tornam
    // GetMessageAsync/OpenAttachment O(1) em vez de varrer a árvore a cada chamada.
    // Só são usados/válidos enquanto a sessão (e o XstFile) estiver aberta.
    private Dictionary<string, XstMessage>? _messageIndex;
    private Dictionary<string, XstAttachment>? _attachmentIndex;

    public string ReaderName => "XstReader.Api Engine";

    public int XstFileOpenCount { get; private set; }

    public XstReaderMailStoreReader()
    {
    }

    public XstReaderMailStoreReader(string filePath)
    {
        _filePath = filePath;
    }

    public async Task BeginReadSessionAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Arquivo PST/OST não encontrado para sessão de leitura.", filePath);
        }

        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_sessionFile != null && string.Equals(_filePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DisposeSessionNoLock();

            _filePath = filePath;
            _sessionFile = OpenXstFile(filePath);
            _sessionRoot = _sessionFile.RootFolder;
            _rootFolderPath = _sessionRoot.Path;
            _folderCache.Clear();
            CacheFolder(_sessionRoot);
        }
        catch (Exception ex)
        {
            AddIssue("MV-ERR-XST-SESSION-BEGIN", "Critical", "Falha ao abrir sessão de leitura XstReader.", Path.GetFileName(filePath), ex);
            DisposeSessionNoLock();
            throw;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task EndReadSessionAsync(CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            DisposeSessionNoLock();
        }
        finally
        {
            _sessionGate.Release();
        }
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

            string rootPath = RunWithRoot(root =>
            {
                _rootFolderPath = root.Path;
                return root.Path;
            }, ct);

            _rootFolderPath = rootPath;
        }
        catch (Exception ex)
        {
            var issue = CreateIssue(
                "MV-ERR-ADAPTER-INSPECT",
                "Error",
                $"Falha ao inspecionar o arquivo PST/OST usando XstReader: {ex.Message}",
                Path.GetFileName(filePath),
                ex);
            issues.Add(issue);
            AddIssue(issue);
        }

        var metadata = new StoreMetadata(
            SourcePath: filePath,
            SizeBytes: size,
            Sha256: string.Empty,
            DetectedFormat: format,
            ReaderName: ReaderName,
            Issues: issues
        );

        return Task.FromResult(metadata);
    }

    public async IAsyncEnumerable<FolderNode> EnumerateFoldersAsync([EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var folders = new List<FolderNode>();

        try
        {
            folders = RunWithRoot(root =>
            {
                _rootFolderPath = root.Path;
                if (_sessionFile != null)
                {
                    _folderCache.Clear();
                    CacheFolder(root);
                }

                var mapped = new List<FolderNode>();
                foreach (var folder in SafeFolders(root, root.Path))
                {
                    ct.ThrowIfCancellationRequested();
                    mapped.Add(MapFolderNode(folder, isRoot: true, ct));
                }

                return mapped;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddIssue("MV-ERR-XST-FOLDER-ENUM", "Error", "Falha ao enumerar pastas com XstReader.", _rootFolderPath ?? Path.GetFileName(_filePath ?? ""), ex);
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

        await _sessionGate.WaitAsync(ct);
        try
        {
            var root = _sessionRoot ?? (_sessionFile ??= OpenXstFile(_filePath ?? throw new InvalidOperationException("Reader não inicializado."))).RootFolder;
            targetFolder = FindFolderByPath(root, folderId.Value);
        }
        finally
        {
            _sessionGate.Release();
        }

        if (targetFolder == null)
        {
            AddIssue("MV-WARN-XST-FOLDER-NOT-FOUND", "Warning", "Pasta não encontrada durante enumeração de mensagens.", folderId.Value, null);
            yield break;
        }

        var msgList = SafeMessages(targetFolder, folderId.Value);
        foreach (var msg in msgList)
        {
            ct.ThrowIfCancellationRequested();
            MailItem? mailItem = null;
            try
            {
                mailItem = MapMailItem(msg, folderId.Value);
            }
            catch (Exception ex)
            {
                AddIssue("MV-ERR-XST-MESSAGE-MAP", "Error", "Falha ao mapear mensagem; item ignorado.", SafeObjectId(() => msg.Path, folderId.Value), ex);
            }

            if (mailItem != null)
            {
                yield return mailItem;
            }
        }
    }

    public Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct)
    {
        return OpenAttachmentByIdAsync(attachment.InternalId, ct);
    }

    public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct)
    {
        return OpenAttachmentByIdAsync(attachmentId.Value, ct);
    }

    public Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct)
    {
        try
        {
            var msg = RunWithRoot(root => FindMessageCached(root, messageId.Value), ct);

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

            var mailItem = MapMailItem(msg, messageId.Value);
            return Task.FromResult(OperationResult<MailItem>.Ok(mailItem));
        }
        catch (Exception ex)
        {
            var issue = CreateIssue(
                "MV-ERR-ADAPTER-GETMSG",
                "Error",
                $"Falha ao ler mensagem por ID do XstReader: {ex.Message}",
                messageId.Value,
                ex);
            AddIssue(issue);
            return Task.FromResult(OperationResult<MailItem>.Failure(issue));
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

    private Task<Stream> OpenAttachmentByIdAsync(string attachmentId, CancellationToken ct)
    {
        try
        {
            var xstAttach = RunWithRoot(root => FindAttachmentCached(root, attachmentId), ct);
            if (xstAttach == null)
            {
                throw new FileNotFoundException($"Anexo com ID {attachmentId} não foi encontrado.");
            }

            var memoryStream = new MemoryStream();
            xstAttach.SaveToStream(memoryStream);
            memoryStream.Position = 0;
            return Task.FromResult<Stream>(memoryStream);
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            AddIssue("MV-ERR-XST-ATTACHMENT", "Error", "Falha ao abrir anexo com XstReader.", attachmentId, ex);
            throw;
        }
    }

    private T RunWithRoot<T>(Func<XstFolder, T> action, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_filePath))
        {
            throw new InvalidOperationException("Reader não inicializado. Chame InspectAsync primeiro.");
        }

        ct.ThrowIfCancellationRequested();
        _sessionGate.Wait(ct);
        try
        {
            if (_sessionRoot != null)
            {
                return action(_sessionRoot);
            }

            using var xstFile = OpenXstFile(_filePath);
            var root = xstFile.RootFolder;
            return action(root);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private XstFile OpenXstFile(string filePath)
    {
        XstFileOpenCount++;
        return new XstFile(filePath);
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
        var toList = MetadataOnly ? new List<MailAddressRef>() : SafeRecipients(() => msg.Recipients?.To, internalId, "To", issues);
        var ccList = MetadataOnly ? new List<MailAddressRef>() : SafeRecipients(() => msg.Recipients?.Cc, internalId, "Cc", issues);
        var bccList = MetadataOnly ? new List<MailAddressRef>() : SafeRecipients(() => msg.Recipients?.Bcc, internalId, "Bcc", issues);

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
                var issue = CreateIssue("MV-WARN-XST-BODY", "Warning", "Falha ao ler corpo da mensagem; metadados preservados.", internalId, ex);
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
                    attachList.Add(new AttachmentRef(
                        InternalId: SafeString(() => att.Path) ?? $"{internalId}/attachment/{Guid.NewGuid():N}",
                        FileName: SafeString(() => att.FileName),
                        ContentType: null,
                        SizeBytes: SafeNullableLong(() => att.Size, internalId),
                        ContentId: SafeString(() => att.ContentId),
                        IsInline: SafeBool(() => att.IsInlineAttachment, false, internalId)
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            var issue = CreateIssue("MV-WARN-XST-ATTACHMENTS", "Warning", "Falha ao ler anexos da mensagem; mensagem preservada.", internalId, ex);
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
            RawProperties: new Dictionary<string, string>(),
            Issues: issues
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
            AddIssue("MV-ERR-XST-FOLDER-CHILDREN", "Error", "Falha ao ler subpastas; ramo ignorado.", objectId, ex);
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
            AddIssue("MV-ERR-XST-FOLDER-MESSAGES", "Error", "Falha ao ler mensagens da pasta; pasta ignorada.", objectId, ex);
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
                AddIssue("MV-WARN-XST-ATTACHMENT-SEARCH", "Warning", "Falha ao procurar anexo em mensagem; item ignorado.", SafeObjectId(() => msg.Path, path), ex);
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

    // Busca via índice O(1) quando há sessão aberta; caso contrário, varredura direta.
    private XstMessage? FindMessageCached(XstFolder root, string path)
    {
        if (_sessionRoot != null)
        {
            _messageIndex ??= BuildMessageIndex(root);
            return _messageIndex.TryGetValue(path, out var m) ? m : null;
        }
        return FindMessageByPath(root, path);
    }

    private XstAttachment? FindAttachmentCached(XstFolder root, string path)
    {
        if (_sessionRoot != null)
        {
            _attachmentIndex ??= BuildAttachmentIndex(root);
            return _attachmentIndex.TryGetValue(path, out var a) ? a : null;
        }
        return FindAttachmentByPath(root, path);
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
        if (_sessionFile == null)
        {
            return;
        }

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
            var issue = CreateIssue("MV-WARN-XST-RECIPIENTS", "Warning", $"Falha ao ler destinatários ({field}); campo omitido.", objectId, ex);
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
            AddIssue("MV-WARN-XST-INT-PROP", "Warning", "Falha ao ler propriedade numérica do XstReader.", objectId, ex);
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
            AddIssue("MV-WARN-XST-LONG-PROP", "Warning", "Falha ao ler tamanho do anexo.", objectId, ex);
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
            AddIssue("MV-WARN-XST-BOOL-PROP", "Warning", "Falha ao ler flag booleana do XstReader.", objectId, ex);
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
            AddIssue("MV-WARN-XST-DATE-PROP", "Warning", "Falha ao ler data da mensagem.", _filePath == null ? null : Path.GetFileName(_filePath), ex);
            return null;
        }
    }
}

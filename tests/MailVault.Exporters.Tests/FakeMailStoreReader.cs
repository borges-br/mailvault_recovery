using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;

namespace MailVault.Exporters.Tests;

public class FakeMailStoreReader : IMailStoreReader
{
    public string ReaderName => "Fake Exporters Test Mail Store Reader";

    private readonly List<FolderNode> _folders;
    private readonly Dictionary<string, List<MailItem>> _messagesByFolder;
    private readonly Dictionary<string, MailItem> _messagesById;

    public FakeMailStoreReader()
    {
        var subFolder = new FolderNode(
            Id: new FolderId("Inbox/Subfolder"),
            ParentId: new FolderId("Inbox"),
            DisplayName: "Subfolder",
            FullPath: "Inbox/Subfolder",
            MessageCount: 2,
            Children: new List<FolderNode>()
        );

        var inbox = new FolderNode(
            Id: new FolderId("Inbox"),
            ParentId: null,
            DisplayName: "Inbox",
            FullPath: "Inbox",
            MessageCount: 3,
            Children: new List<FolderNode> { subFolder }
        );

        var sentItems = new FolderNode(
            Id: new FolderId("Sent Items"),
            ParentId: null,
            DisplayName: "Sent Items",
            FullPath: "Sent Items",
            MessageCount: 1,
            Children: new List<FolderNode>()
        );

        _folders = new List<FolderNode> { inbox, sentItems };

        var inboxMsgs = new List<MailItem>();
        for (int i = 1; i <= 3; i++)
        {
            var bodyLines = new List<string> {
                $"Este é o corpo da mensagem {i}.",
                "Linha 2 do corpo.",
                "Linha 3 do corpo."
            };
            for (int l = 4; l <= 10; l++)
            {
                bodyLines.Add($"Linha extra {l} do e-mail simulado.");
            }

            inboxMsgs.Add(new MailItem(
                InternalId: $"msg-inbox-{i}",
                InternetMessageId: $"<inbox-{i}@fake.local>",
                Subject: $"Assunto do Fake Inbox {i}",
                From: new MailAddressRef("Remetente Fake", "from@fake.local"),
                To: new List<MailAddressRef> { new MailAddressRef("Destinatario Fake", "to@fake.local") },
                Cc: new List<MailAddressRef>(),
                Bcc: new List<MailAddressRef>(),
                SentAt: DateTimeOffset.Parse("2026-05-26T10:00:00-03:00"),
                ReceivedAt: DateTimeOffset.Parse("2026-05-26T10:05:00-03:00"),
                PlainTextBody: string.Join("\n", bodyLines),
                HtmlBody: "<html><body>Este e o corpo em HTML.</body></html>",
                Attachments: new List<AttachmentRef> {
                    new AttachmentRef(
                        InternalId: $"att-inbox-{i}-1",
                        FileName: $"anexo-{i}.txt",
                        ContentType: "text/plain",
                        SizeBytes: 1234,
                        ContentId: null,
                        IsInline: false
                    )
                },
                RawProperties: new Dictionary<string, string> { { "0x0037", "Subject Property" } },
                Issues: new List<ExtractionIssue>()
            ));
        }

        var subMsgs = new List<MailItem>();
        for (int i = 1; i <= 2; i++)
        {
            subMsgs.Add(new MailItem(
                InternalId: $"msg-sub-{i}",
                InternetMessageId: $"<sub-{i}@fake.local>",
                Subject: $"Assunto do Subfolder {i}",
                From: new MailAddressRef("Sub Remetente", "sub@fake.local"),
                To: new List<MailAddressRef> { new MailAddressRef("Destinatario Fake", "to@fake.local") },
                Cc: new List<MailAddressRef>(),
                Bcc: new List<MailAddressRef>(),
                SentAt: DateTimeOffset.Parse("2026-05-26T11:00:00-03:00"),
                ReceivedAt: DateTimeOffset.Parse("2026-05-26T11:02:00-03:00"),
                PlainTextBody: $"Este é o corpo da sub-mensagem {i}.",
                HtmlBody: null,
                Attachments: new List<AttachmentRef>(),
                RawProperties: new Dictionary<string, string>(),
                Issues: new List<ExtractionIssue>()
            ));
        }

        var sentMsgs = new List<MailItem>
        {
            new MailItem(
                InternalId: "msg-sent-1",
                InternetMessageId: "<sent-1@fake.local>",
                Subject: "Assunto do Enviado",
                From: new MailAddressRef("Eu", "me@fake.local"),
                To: new List<MailAddressRef> { new MailAddressRef("Destinatario", "receiver@fake.local") },
                Cc: new List<MailAddressRef>(),
                Bcc: new List<MailAddressRef>(),
                SentAt: DateTimeOffset.Parse("2026-05-26T12:00:00-03:00"),
                ReceivedAt: null,
                PlainTextBody: "Este é o corpo da mensagem enviada.",
                HtmlBody: null,
                Attachments: new List<AttachmentRef>(),
                RawProperties: new Dictionary<string, string>(),
                Issues: new List<ExtractionIssue>()
            )
        };

        _messagesByFolder = new Dictionary<string, List<MailItem>>
        {
            { "Inbox", inboxMsgs },
            { "Inbox/Subfolder", subMsgs },
            { "Sent Items", sentMsgs }
        };

        _messagesById = inboxMsgs.Concat(subMsgs).Concat(sentMsgs).ToDictionary(m => m.InternalId);
    }

    public Task<StoreMetadata> InspectAsync(string filePath, CancellationToken ct)
    {
        return Task.FromResult(new StoreMetadata(
            SourcePath: filePath,
            SizeBytes: 987654,
            Sha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            DetectedFormat: "Fake Store",
            ReaderName: ReaderName,
            Issues: new List<ExtractionIssue>()
        ));
    }

    public async IAsyncEnumerable<FolderNode> EnumerateFoldersAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var folder in _folders)
        {
            ct.ThrowIfCancellationRequested();
            yield return folder;
        }
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<MailItem> EnumerateMessagesAsync(FolderId folderId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_messagesByFolder.TryGetValue(folderId.Value, out var list))
        {
            foreach (var msg in list)
            {
                ct.ThrowIfCancellationRequested();
                yield return msg;
            }
        }
        await Task.CompletedTask;
    }

    public Task<Stream> OpenAttachmentAsync(AttachmentRef attachment, CancellationToken ct)
    {
        var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Fake attachment content"));
        return Task.FromResult<Stream>(ms);
    }

    public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct)
    {
        var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"Fake attachment content for message {messageId.Value} and attachment {attachmentId.Value}"));
        return Task.FromResult<Stream>(ms);
    }

    public Task<OperationResult<MailItem>> GetMessageAsync(MessageId messageId, CancellationToken ct)
    {
        if (_messagesById.TryGetValue(messageId.Value, out var msg))
        {
            return Task.FromResult(OperationResult<MailItem>.Ok(msg));
        }

        return Task.FromResult(OperationResult<MailItem>.Failure(new ExtractionIssue(
            Code: "MV-ERR-NOT-FOUND",
            Severity: "Error",
            Message: $"Mensagem {messageId.Value} não encontrada no store fake.",
            ObjectId: messageId.Value,
            TechnicalDetails: null
        )));
    }
}

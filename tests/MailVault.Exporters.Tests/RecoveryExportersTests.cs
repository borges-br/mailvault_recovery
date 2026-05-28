using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Exporters.Eml;
using MimeKit;
using Xunit;

namespace MailVault.Exporters.Tests;

public class RecoveryExportersTests : IDisposable
{
    private readonly string _tempDir;

    public RecoveryExportersTests()
    {
        _tempDir = Path.Combine(Directory.GetCurrentDirectory(), "recovery-exporter-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    [Fact]
    public async Task MailVaultMimeSerializer_FallsBackToPlaceholder_WhenBodiesAreMissing()
    {
        // Arrange
        var message = new MailItem(
            InternalId: "msg-no-body",
            InternetMessageId: "<none@test.com>",
            Subject: "E-mail sem corpo",
            From: new MailAddressRef("Remetente", "sender@test.com"),
            To: new List<MailAddressRef>(),
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.UtcNow,
            ReceivedAt: DateTimeOffset.UtcNow,
            PlainTextBody: null, // MISSING plain text
            HtmlBody: null,      // MISSING HTML body
            Attachments: new List<AttachmentRef>(),
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        );

        var provider = new SuccessAttachmentProvider();

        // Act
        var mimeMessage = await MailVaultMimeSerializer.SerializeAsync(message, provider, CancellationToken.None);

        // Assert
        Assert.NotNull(mimeMessage);
        Assert.Equal("Body could not be recovered.", mimeMessage.TextBody.Trim());
    }

    [Fact]
    public async Task MailVaultMimeSerializer_InjectsErrorAttachment_WhenAttachmentStreamThrows()
    {
        // Arrange
        var message = new MailItem(
            InternalId: "msg-bad-att",
            InternetMessageId: "<att-fail@test.com>",
            Subject: "E-mail com anexo corrompido",
            From: new MailAddressRef("Remetente", "sender@test.com"),
            To: new List<MailAddressRef>(),
            Cc: new List<MailAddressRef>(),
            Bcc: new List<MailAddressRef>(),
            SentAt: DateTimeOffset.UtcNow,
            ReceivedAt: DateTimeOffset.UtcNow,
            PlainTextBody: "O anexo desta mensagem deve falhar na exportação.",
            HtmlBody: null,
            Attachments: new List<AttachmentRef>
            {
                new AttachmentRef("bad-att-id-123", "planilha.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 4096, null, false)
            },
            RawProperties: new Dictionary<string, string>(),
            Issues: new List<ExtractionIssue>()
        );

        var provider = new FailingAttachmentProvider();

        // Act
        var mimeMessage = await MailVaultMimeSerializer.SerializeAsync(message, provider, CancellationToken.None);

        // Assert
        Assert.NotNull(mimeMessage);
        Assert.Single(mimeMessage.Attachments);

        var attachment = mimeMessage.Attachments.First();
        Assert.NotNull(attachment);
        Assert.Equal("ERROR_ATTACHMENT_planilha.xlsx.txt", attachment.ContentType.Name);

        // Parse content of the injected error attachment
        using var memoryStream = new MemoryStream();
        if (attachment is MimePart part)
        {
            part.Content.DecodeTo(memoryStream);
        }
        string content = Encoding.UTF8.GetString(memoryStream.ToArray());

        Assert.Contains("ERROR: O anexo 'planilha.xlsx' não pôde ser recuperado", content);
        Assert.Contains("Simulated attachment stream retrieval failure", content);
    }

    private class SuccessAttachmentProvider : IAttachmentContentProvider
    {
        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct)
        {
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("anexo_ok")));
        }
    }

    private class FailingAttachmentProvider : IAttachmentContentProvider
    {
        public Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct)
        {
            throw new InvalidOperationException("Simulated attachment stream retrieval failure.");
        }
    }
}

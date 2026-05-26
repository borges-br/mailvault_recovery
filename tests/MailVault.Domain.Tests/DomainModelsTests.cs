using System;
using System.Collections.Generic;
using MailVault.Domain;
using Xunit;

namespace MailVault.Domain.Tests;

public class DomainModelsTests
{
    [Fact]
    public void MailItem_ShouldConstructCorrectly_WithImutability()
    {
        // Arrange & Act
        var from = new MailAddressRef("John Doe", "john.doe@example.com");
        var to = new List<MailAddressRef> { new("Alice Smith", "alice.smith@example.com") };
        var cc = new List<MailAddressRef>();
        var bcc = new List<MailAddressRef>();
        var attachments = new List<AttachmentRef>
        {
            new("att-01", "document.pdf", "application/pdf", 1024, "cid-01", false)
        };
        var rawProperties = new Dictionary<string, string>
        {
            { "PR_SUBJECT", "Important info" }
        };
        var issues = new List<ExtractionIssue>();

        var mailItem = new MailItem(
            InternalId: "msg-123",
            InternetMessageId: "<id-123@example.com>",
            Subject: "Important info",
            From: from,
            To: to,
            Cc: cc,
            Bcc: bcc,
            SentAt: DateTimeOffset.Now,
            ReceivedAt: DateTimeOffset.Now,
            PlainTextBody: "Hello world",
            HtmlBody: "<p>Hello world</p>",
            Attachments: attachments,
            RawProperties: rawProperties,
            Issues: issues
        );

        // Assert
        Assert.Equal("msg-123", mailItem.InternalId);
        Assert.Equal("Important info", mailItem.Subject);
        Assert.Equal("john.doe@example.com", mailItem.From?.Address);
        Assert.Single(mailItem.To);
        Assert.Equal("alice.smith@example.com", mailItem.To[0].Address);
        Assert.Single(mailItem.Attachments);
        Assert.Equal("document.pdf", mailItem.Attachments[0].FileName);
        Assert.True(mailItem.RawProperties.ContainsKey("PR_SUBJECT"));
    }

    [Fact]
    public void FolderNode_ShouldFormHierarchicalTree()
    {
        // Arrange
        var folderId1 = new FolderId("folder-1");
        var folderId2 = new FolderId("folder-2");

        var childFolder = new FolderNode(
            Id: folderId2,
            ParentId: folderId1,
            DisplayName: "Inbox",
            FullPath: "/Inbox",
            MessageCount: 42,
            Children: Array.Empty<FolderNode>()
        );

        var parentFolder = new FolderNode(
            Id: folderId1,
            ParentId: null,
            DisplayName: "Root",
            FullPath: "/",
            MessageCount: 0,
            Children: new[] { childFolder }
        );

        // Assert
        Assert.Null(parentFolder.ParentId);
        Assert.Single(parentFolder.Children);
        Assert.Equal(folderId1, parentFolder.Id);
        Assert.Equal(folderId1, childFolder.ParentId);
        Assert.Equal(folderId2, parentFolder.Children[0].Id);
        Assert.Equal(42, parentFolder.Children[0].MessageCount);
    }
}

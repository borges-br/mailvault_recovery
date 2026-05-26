using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Indexing;
using MailVault.Validation;
using MimeKit;
using Xunit;

namespace MailVault.Validation.Tests;

public class ValidationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dummyFile;
    private readonly string _dummyFileSha256;

    public ValidationTests()
    {
        _tempDir = Path.Combine("c:\\Github\\mailvault_recovery\\scratch", $"val-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _dummyFile = Path.Combine(_tempDir, "evidence.pst");
        File.WriteAllText(_dummyFile, "Dummy PST data for validation tests");
        
        var hashService = new HashService();
        _dummyFileSha256 = hashService.CalculateSha256Async(_dummyFile, new NullProgress(), CancellationToken.None).GetAwaiter().GetResult();
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
            // Ignore minor cleanup errors
        }
    }

    private async Task<ICaseIndexStore> SetupIndexStoreAsync(int messageCount, int attachmentCount)
    {
        var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(_tempDir, CancellationToken.None);

        using (var writer = store.CreateWriter())
        {
            await writer.BeginTransactionAsync(CancellationToken.None);

            await writer.SaveCaseInfoAsync(
                caseId: "CASE-VAL-TEST",
                sourceFile: _dummyFile,
                sourceSize: 1024,
                sourceSha256: _dummyFileSha256,
                operatorName: "test-operator",
                startedAt: DateTimeOffset.Now,
                adapterName: "FakeAdapter",
                adapterVersion: "1.0.0.0",
                ct: CancellationToken.None
            );

            var folder = new FolderNode(new FolderId("Inbox"), null, "Inbox", "Inbox", messageCount, new List<FolderNode>());
            await writer.SaveFolderAsync(folder, CancellationToken.None);

            for (int i = 1; i <= messageCount; i++)
            {
                var attachments = new List<AttachmentRef>();
                if (i == 1 && attachmentCount > 0)
                {
                    for (int a = 1; a <= attachmentCount; a++)
                    {
                        attachments.Add(new AttachmentRef($"att-{a}", $"document-{a}.pdf", "application/pdf", 100, null, false));
                    }
                }

                var msg = new MailItem(
                    InternalId: $"msg-{i}",
                    InternetMessageId: $"<msg-{i}@test.local>",
                    Subject: $"Assunto Teste {i}",
                    From: new MailAddressRef("Remetente", "sender@test.local"),
                    To: new List<MailAddressRef>(),
                    Cc: new List<MailAddressRef>(),
                    Bcc: new List<MailAddressRef>(),
                    SentAt: DateTimeOffset.Now,
                    ReceivedAt: DateTimeOffset.Now,
                    PlainTextBody: "Preview do corpo do email",
                    HtmlBody: null,
                    Attachments: attachments,
                    RawProperties: new Dictionary<string, string>(),
                    Issues: new List<ExtractionIssue>()
                );

                await writer.SaveMessageAsync(msg, folder.Id, CancellationToken.None);
            }

            await writer.CommitTransactionAsync(CancellationToken.None);
        }

        return store;
    }

    // 1. ValidationReport_WritesJsonWithoutSensitiveBody
    [Fact]
    public void ValidationReport_WritesJsonWithoutSensitiveBody()
    {
        // Arrange
        var report = new ValidationReport(
            ValidationId: "VAL-1",
            CaseId: "CASE-1",
            SourceFileMasked: "C:\\Users\\<USER>\\evidence.pst",
            SourceSha256: "SHA256",
            AdapterName: "Adapter",
            AdapterVersion: "1.0.0.0",
            ExportId: "EXP-1",
            ExportFormat: "eml",
            StartedAt: DateTimeOffset.Now,
            CompletedAt: DateTimeOffset.Now,
            DurationMs: 10,
            IndexedMessages: 10,
            SelectedMessages: 10,
            ExportedMessages: 10,
            FailedMessages: 0,
            IndexedAttachments: 2,
            ExportedAttachments: 2,
            FailedAttachments: 0,
            EmptyExportedFiles: 0,
            DuplicateOutputNames: 0,
            MissingExpectedFiles: 0,
            PathSafetyIssues: 0,
            FoldersChecked: new List<string> { "Inbox" },
            FolderResults: new List<FolderValidationResult>(),
            WarningCount: 0,
            ErrorCount: 0,
            Status: "Passed",
            Issues: new List<ValidationIssue>()
        );

        // Act
        string json = JsonSerializer.Serialize(report);

        // Assert
        Assert.DoesNotContain("PlainTextBody", json);
        Assert.DoesNotContain("HtmlBody", json);
        Assert.DoesNotContain("Corpo", json);
    }

    // 2. ValidateCommand_WithSyntheticEmlExport_ReturnsPassed
    [Fact]
    public async Task ValidateCommand_WithSyntheticEmlExport_ReturnsPassed()
    {
        // Arrange
        using var store = await SetupIndexStoreAsync(messageCount: 2, attachmentCount: 1);
        
        // Generate EML files and export-manifest.json
        string exportDir = Path.Combine(_tempDir, "exports-passed");
        Directory.CreateDirectory(Path.Combine(exportDir, "eml"));

        var exportedMessages = new List<ExportedMessageRecord>();

        for (int i = 1; i <= 2; i++)
        {
            var mimeMsg = new MimeMessage();
            mimeMsg.From.Add(new MailboxAddress("Remetente", "sender@test.local"));
            mimeMsg.Subject = $"Assunto Teste {i}";
            mimeMsg.MessageId = $"msg-{i}@test.local";
            
            var body = new TextPart("plain") { Text = "Corpo do email" };
            
            if (i == 1)
            {
                var multipart = new Multipart("mixed") { body };
                var attPart = new MimePart("application/pdf")
                {
                    Content = new MimeContent(new MemoryStream(new byte[100])),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                    FileName = "document-1.pdf"
                };
                multipart.Add(attPart);
                mimeMsg.Body = multipart;
            }
            else
            {
                mimeMsg.Body = body;
            }

            string relativePath = $"eml/msg-{i}.eml";
            string fullPath = Path.Combine(exportDir, relativePath);
            using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write))
            {
                mimeMsg.WriteTo(fs);
            }

            exportedMessages.Add(new ExportedMessageRecord(
                MessageId: $"msg-{i}",
                FolderPath: "Inbox",
                SubjectTruncated: $"Assunto Teste {i}",
                RelativePath: relativePath,
                Status: "Success",
                AttachmentCount: (i == 1) ? 1 : 0
            ));
        }

        var manifest = new ExportManifest(
            ExportId: "EXP-Passed",
            CaseId: "CASE-VAL-TEST",
            SourceFile: _dummyFile,
            SourceSha256: _dummyFileSha256,
            AdapterName: "FakeAdapter",
            AdapterVersion: "1.0.0.0",
            ExportFormat: "eml",
            StartedAt: DateTimeOffset.Now,
            CompletedAt: DateTimeOffset.Now,
            OutputDirectory: exportDir,
            FoldersSelected: 1,
            MessagesSelected: 2,
            MessagesExported: 2,
            MessagesFailed: 0,
            AttachmentsExported: 1,
            AttachmentsFailed: 0,
            Issues: new List<ExtractionIssue>(),
            ExportedMessages: exportedMessages,
            ToolVersion: "1.0.0.0"
        );

        string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(exportDir, "export-manifest.json"), manifestJson);

        var engine = new ValidationEngine();

        // Act
        var report = await engine.ValidateAsync(
            caseFolder: _tempDir,
            exportFolderOverride: exportDir,
            formatOverride: "auto",
            strict: true,
            checkEmlParse: true,
            checkMboxStructure: true,
            checkAttachments: true,
            sampleSize: null,
            outDir: null,
            ct: CancellationToken.None
        );

        // Assert
        Assert.Equal("Passed", report.Status);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(0, report.WarningCount);
        Assert.Equal(2, report.ExportedMessages);
    }

    // 3. ValidateCommand_DetectsMissingExportedMessage
    [Fact]
    public async Task ValidateCommand_DetectsMissingExportedMessage()
    {
        // Arrange
        using var store = await SetupIndexStoreAsync(messageCount: 2, attachmentCount: 0);

        string exportDir = Path.Combine(_tempDir, "exports-missing");
        Directory.CreateDirectory(exportDir);

        var exportedMessages = new List<ExportedMessageRecord>
        {
            new ExportedMessageRecord("msg-1", "Inbox", "Subject", "eml/msg-1.eml", "Success", 0),
            new ExportedMessageRecord("msg-2", "Inbox", "Subject", "eml/msg-2.eml", "Success", 0)
        };

        var manifest = new ExportManifest("EXP-1", "CASE-VAL-TEST", _dummyFile, _dummyFileSha256, "FakeAdapter", "1.0.0.0", "eml", DateTimeOffset.Now, DateTimeOffset.Now, exportDir, 1, 2, 2, 0, 0, 0, new List<ExtractionIssue>(), exportedMessages, "1.0.0");
        File.WriteAllText(Path.Combine(exportDir, "export-manifest.json"), JsonSerializer.Serialize(manifest));

        // Create only EML 1, EML 2 is missing!
        Directory.CreateDirectory(Path.Combine(exportDir, "eml"));
        File.WriteAllText(Path.Combine(exportDir, "eml/msg-1.eml"), "EML content");

        var engine = new ValidationEngine();

        // Act
        var report = await engine.ValidateAsync(_tempDir, exportDir, "eml", strict: false, checkEmlParse: false, checkMboxStructure: false, checkAttachments: false, sampleSize: null, outDir: null, CancellationToken.None);

        // Assert
        Assert.Equal("Failed", report.Status);
        Assert.Equal(1, report.MissingExpectedFiles);
        Assert.Contains(report.Issues, i => i.Code == "VAL-ERR-MISSINGMSG");
    }

    // 4. ValidateCommand_DetectsEmptyEmlFile
    [Fact]
    public async Task ValidateCommand_DetectsEmptyEmlFile()
    {
        // Arrange
        using var store = await SetupIndexStoreAsync(messageCount: 1, attachmentCount: 0);
        string exportDir = Path.Combine(_tempDir, "exports-empty");
        Directory.CreateDirectory(Path.Combine(exportDir, "eml"));

        File.WriteAllText(Path.Combine(exportDir, "eml/msg-1.eml"), ""); // Empty!

        var exportedMessages = new List<ExportedMessageRecord> { new ExportedMessageRecord("msg-1", "Inbox", "Subject", "eml/msg-1.eml", "Success", 0) };
        var manifest = new ExportManifest("EXP-1", "CASE-VAL-TEST", _dummyFile, _dummyFileSha256, "FakeAdapter", "1.0.0.0", "eml", DateTimeOffset.Now, DateTimeOffset.Now, exportDir, 1, 1, 1, 0, 0, 0, new List<ExtractionIssue>(), exportedMessages, "1.0.0");
        File.WriteAllText(Path.Combine(exportDir, "export-manifest.json"), JsonSerializer.Serialize(manifest));

        var engine = new ValidationEngine();

        // Act
        var report = await engine.ValidateAsync(_tempDir, exportDir, "eml", strict: false, checkEmlParse: false, checkMboxStructure: false, checkAttachments: false, sampleSize: null, outDir: null, CancellationToken.None);

        // Assert
        Assert.Equal("Failed", report.Status);
        Assert.Equal(1, report.EmptyExportedFiles);
        Assert.Contains(report.Issues, i => i.Code == "VAL-ERR-EMPTYEML");
    }

    // 5. ValidateCommand_DetectsDuplicateOutputName
    [Fact]
    public async Task ValidateCommand_DetectsDuplicateOutputName()
    {
        // Arrange
        using var store = await SetupIndexStoreAsync(messageCount: 2, attachmentCount: 0);
        string exportDir = Path.Combine(_tempDir, "exports-dup");
        Directory.CreateDirectory(Path.Combine(exportDir, "eml/folderA"));
        Directory.CreateDirectory(Path.Combine(exportDir, "eml/folderB"));

        // Create files with same name in different folders
        File.WriteAllText(Path.Combine(exportDir, "eml/folderA/msg-1.eml"), "EML A");
        File.WriteAllText(Path.Combine(exportDir, "eml/folderB/msg-1.eml"), "EML B");

        var engine = new ValidationEngine();

        // Act
        var report = await engine.ValidateAsync(_tempDir, exportDir, "eml", strict: false, checkEmlParse: false, checkMboxStructure: false, checkAttachments: false, sampleSize: null, outDir: null, CancellationToken.None);

        // Assert
        Assert.Equal(1, report.DuplicateOutputNames);
        Assert.Contains(report.Issues, i => i.Code == "VAL-WARN-DUPNAME");
    }

    // 6. ValidateCommand_DetectsAttachmentMismatch
    [Fact]
    public async Task ValidateCommand_DetectsAttachmentMismatch()
    {
        // Arrange
        using var store = await SetupIndexStoreAsync(messageCount: 1, attachmentCount: 2); // Expected: 2 attachments
        string exportDir = Path.Combine(_tempDir, "exports-mismatch");
        Directory.CreateDirectory(Path.Combine(exportDir, "eml"));

        var mimeMsg = new MimeMessage();
        mimeMsg.Subject = "Assunto";
        mimeMsg.MessageId = "msg-1@test.local";
        
        // EML only has 1 attachment (mismatch!)
        var body = new TextPart("plain") { Text = "Body" };
        var multipart = new Multipart("mixed") { body };
        multipart.Add(new MimePart("application/pdf")
        {
            Content = new MimeContent(new MemoryStream(new byte[100])),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            FileName = "document-1.pdf"
        });
        mimeMsg.Body = multipart;

        string relativePath = "eml/msg-1.eml";
        using (var fs = new FileStream(Path.Combine(exportDir, relativePath), FileMode.Create))
        {
            mimeMsg.WriteTo(fs);
        }

        var exportedMessages = new List<ExportedMessageRecord> { new ExportedMessageRecord("msg-1", "Inbox", "Subject", relativePath, "Success", 2) };
        var manifest = new ExportManifest("EXP-1", "CASE-VAL-TEST", _dummyFile, _dummyFileSha256, "FakeAdapter", "1.0.0.0", "eml", DateTimeOffset.Now, DateTimeOffset.Now, exportDir, 1, 1, 1, 0, 2, 0, new List<ExtractionIssue>(), exportedMessages, "1.0.0");
        File.WriteAllText(Path.Combine(exportDir, "export-manifest.json"), JsonSerializer.Serialize(manifest));

        var engine = new ValidationEngine();

        // Act
        var report = await engine.ValidateAsync(_tempDir, exportDir, "eml", strict: true, checkEmlParse: true, checkMboxStructure: false, checkAttachments: false, sampleSize: null, outDir: null, CancellationToken.None);

        // Assert
        Assert.Equal(1, report.ExportedAttachments); // physical has 1
        Assert.Equal(2, report.IndexedAttachments); // db expects 2
    }

    // 7. MboxValidator_CountsMessagesByFromDelimiter
    [Fact]
    public async Task MboxValidator_CountsMessagesByFromDelimiter()
    {
        // Arrange
        using var store = await SetupIndexStoreAsync(messageCount: 2, attachmentCount: 0);
        string exportDir = Path.Combine(_tempDir, "exports-mbox-count");
        Directory.CreateDirectory(Path.Combine(exportDir, "mbox"));

        string mboxPath = Path.Combine(exportDir, "mbox/mbox");
        using (var sw = new StreamWriter(mboxPath))
        {
            sw.Write("From sender@test.local Tue May 26 15:30:00 2026\r\n");
            sw.Write("Subject: Test 1\r\n");
            sw.Write("\r\n");
            sw.Write("From sender@test.local Tue May 26 15:31:00 2026\r\n");
            sw.Write("Subject: Test 2\r\n");
            sw.Write("\r\n");
        }

        var engine = new ValidationEngine();

        // Act
        var report = await engine.ValidateAsync(_tempDir, exportDir, "mbox", strict: true, checkEmlParse: false, checkMboxStructure: true, checkAttachments: false, sampleSize: null, outDir: null, CancellationToken.None);

        // Assert
        Assert.Equal(2, report.ExportedMessages);
        Assert.Equal(0, report.PathSafetyIssues); // No escape issues
        Assert.Equal("Passed", report.Status);
    }

    // 8. MboxValidator_DetectsUnsafeUnescapedFromLine
    [Fact]
    public async Task MboxValidator_DetectsUnsafeUnescapedFromLine()
    {
        // Arrange
        using var store = await SetupIndexStoreAsync(messageCount: 1, attachmentCount: 0);
        string exportDir = Path.Combine(_tempDir, "exports-mbox-unsafe");
        Directory.CreateDirectory(Path.Combine(exportDir, "mbox"));

        string mboxPath = Path.Combine(exportDir, "mbox/mbox");
        using (var sw = new StreamWriter(mboxPath))
        {
            sw.Write("From sender@test.local Tue May 26 15:30:00 2026\r\n");
            sw.Write("Subject: Test 1\r\n");
            sw.Write("From Roberto Silva\r\n"); // Unescaped in body (no blank line preceding!)
            sw.Write("\r\n");
        }

        var engine = new ValidationEngine();

        // Act
        var report = await engine.ValidateAsync(_tempDir, exportDir, "mbox", strict: true, checkEmlParse: false, checkMboxStructure: true, checkAttachments: false, sampleSize: null, outDir: null, CancellationToken.None);

        // Assert
        Assert.Equal("Failed", report.Status);
        Assert.Equal(1, report.PathSafetyIssues); // Unescaped From line counts as path/safety issue
        Assert.Contains(report.Issues, i => i.Code == "VAL-ERR-UNESCAPEDFROM");
    }

    // 9. Validation_DoesNotIncludeEmailBodyInReport
    [Fact]
    public async Task Validation_DoesNotIncludeEmailBodyInReport()
    {
        // Arrange
        using var store = await SetupIndexStoreAsync(messageCount: 1, attachmentCount: 0);
        string exportDir = Path.Combine(_tempDir, "exports-body-check");
        Directory.CreateDirectory(Path.Combine(exportDir, "eml"));

        File.WriteAllText(Path.Combine(exportDir, "eml/msg-1.eml"), "Subject: Test\r\nMessage-Id: msg-1\r\n\r\nEste e o corpo da mensagem privada de e-mail.");

        var exportedMessages = new List<ExportedMessageRecord> { new ExportedMessageRecord("msg-1", "Inbox", "Test", "eml/msg-1.eml", "Success", 0) };
        var manifest = new ExportManifest("EXP-1", "CASE-VAL-TEST", _dummyFile, _dummyFileSha256, "FakeAdapter", "1.0.0.0", "eml", DateTimeOffset.Now, DateTimeOffset.Now, exportDir, 1, 1, 1, 0, 0, 0, new List<ExtractionIssue>(), exportedMessages, "1.0.0");
        File.WriteAllText(Path.Combine(exportDir, "export-manifest.json"), JsonSerializer.Serialize(manifest));

        var engine = new ValidationEngine();

        // Act
        var report = await engine.ValidateAsync(_tempDir, exportDir, "eml", strict: false, checkEmlParse: true, checkMboxStructure: false, checkAttachments: false, sampleSize: null, outDir: null, CancellationToken.None);
        string jsonReport = JsonSerializer.Serialize(report);

        // Assert
        Assert.DoesNotContain("corpo da mensagem privada", jsonReport);
        Assert.DoesNotContain("Este e o corpo", jsonReport);
    }

    // 10. CorpusPolicy_GitignoreContainsLocalCorpusRules
    [Fact]
    public void CorpusPolicy_GitignoreContainsLocalCorpusRules()
    {
        // Arrange
        string gitignorePath = "c:\\Github\\mailvault_recovery\\.gitignore";
        Assert.True(File.Exists(gitignorePath));

        // Act
        string content = File.ReadAllText(gitignorePath);

        // Assert
        Assert.Contains(".local-corpus/", content);
        Assert.Contains("validation-results/", content);
        Assert.Contains("mailvault-cases/", content);
        Assert.Contains("exports/", content);
        Assert.Contains("*.ost", content);
        Assert.Contains("*.pst", content);
        Assert.Contains("*.msg", content);
        Assert.Contains("*.eml", content);
        Assert.Contains("*.mbox", content);
        Assert.Contains("*.db", content);
        Assert.Contains("*.db-shm", content);
        Assert.Contains("*.db-wal", content);
    }

    private sealed class NullProgress : IProgressReporter
    {
        public void ReportProgress(double percentage, string status) { }
    }
}

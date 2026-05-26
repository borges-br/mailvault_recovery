using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using MailVault.Indexing;
using MimeKit;

namespace MailVault.Validation;

public sealed class ValidationEngine
{
    public async Task<ValidationReport> ValidateAsync(
        string caseFolder,
        string? exportFolderOverride,
        string formatOverride,
        bool strict,
        bool checkEmlParse,
        bool checkMboxStructure,
        bool checkAttachments,
        int? sampleSize,
        string? outDir,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string validationId = $"VAL-{Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant()}";

        // 1. Check case folder and case.db
        string dbPath = Path.Combine(caseFolder, "case.db");
        if (!File.Exists(dbPath))
        {
            throw new FileNotFoundException($"Banco de dados relacional case.db não encontrado na pasta do caso: '{caseFolder}'", dbPath);
        }

        // Initialize Store and Reader
        using var store = new SqliteCaseIndexStore();
        await store.InitializeAsync(caseFolder, ct);
        using var caseReader = store.CreateReader();

        var caseInfo = await caseReader.GetCaseInfoAsync(ct);
        if (caseInfo == null)
        {
            throw new InvalidOperationException("Metadados do caso estão ausentes de case.db.");
        }

        // 2. Load export-manifest.json if exists
        string resolvedExportDir = exportFolderOverride ?? Path.Combine(caseFolder, "exports");
        string manifestPath = Path.Combine(resolvedExportDir, "export-manifest.json");

        if (!File.Exists(manifestPath) && string.IsNullOrEmpty(exportFolderOverride))
        {
            // Try looking inside case folder directly
            manifestPath = Path.Combine(caseFolder, "export-manifest.json");
            if (File.Exists(manifestPath))
            {
                resolvedExportDir = caseFolder;
            }
            else
            {
                // Try looking in exports subfolder
                manifestPath = Path.Combine(caseFolder, "exports", "export-manifest.json");
                if (File.Exists(manifestPath))
                {
                    resolvedExportDir = Path.Combine(caseFolder, "exports");
                }
            }
        }

        ExportManifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(manifestPath, ct);
                manifest = JsonSerializer.Deserialize<ExportManifest>(json);
                if (manifest != null && string.IsNullOrEmpty(exportFolderOverride))
                {
                    resolvedExportDir = manifest.OutputDirectory;
                }
            }
            catch (Exception)
            {
                // Manifest corrupted, proceed without it but log warning
            }
        }

        // 3. Setup metrics
        int indexedMessages = await caseReader.GetMessageCountAsync(ct);
        int indexedAttachments = await caseReader.GetAttachmentCountAsync(ct);

        int selectedMessages = manifest?.MessagesSelected ?? indexedMessages;
        int exportedMessagesManifest = manifest?.MessagesExported ?? 0;
        int failedMessagesManifest = manifest?.MessagesFailed ?? 0;
        int exportedAttachmentsManifest = manifest?.AttachmentsExported ?? 0;
        int failedAttachmentsManifest = manifest?.AttachmentsFailed ?? 0;

        string exportFormat = formatOverride.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? (manifest?.ExportFormat ?? "eml")
            : formatOverride;

        var issues = new List<ValidationIssue>();
        int emptyExportedFiles = 0;
        int duplicateOutputNames = 0;
        int missingExpectedFiles = 0;
        int pathSafetyIssues = 0;
        int exportedMessagesCount = 0;
        int exportedAttachmentsCount = 0;
        int failedAttachmentsCount = 0;

        var foldersCheckedList = new List<string>();
        var folderResultsList = new List<FolderValidationResult>();

        // Gather folders
        var roots = await MailVault.Core.ListExtensions.ToListAsync(caseReader.GetFolderHierarchyAsync(ct), ct);
        var allFolders = new List<FolderNode>();
        FlattenFolderHierarchy(roots, allFolders);

        foreach (var folder in allFolders)
        {
            foldersCheckedList.Add(folder.FullPath);
        }

        // 4. Validate Physically
        if (Directory.Exists(resolvedExportDir))
        {
            if (exportFormat.Equals("eml", StringComparison.OrdinalIgnoreCase))
            {
                string[] emlFiles = Directory.GetFiles(resolvedExportDir, "*.eml", SearchOption.AllDirectories);
                
                // Track duplicate filenames
                var dupGroup = emlFiles.GroupBy(Path.GetFileName).Where(g => g.Count() > 1);
                foreach (var dup in dupGroup)
                {
                    duplicateOutputNames += dup.Count() - 1;
                    issues.Add(new ValidationIssue(
                        Code: "VAL-WARN-DUPNAME",
                        Severity: "Warning",
                        Message: $"Nome de arquivo de exportação duplicado detectado: '{dup.Key}'",
                        ObjectId: dup.Key
                    ));
                }

                // Check EML contents and path safety
                int checkedSampleCount = 0;
                foreach (var emlFile in emlFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    // Check Path Traversal
                    if (!IsSafePath(emlFile, resolvedExportDir))
                    {
                        pathSafetyIssues++;
                        issues.Add(new ValidationIssue(
                            Code: "VAL-ERR-TRAVERSAL",
                            Severity: "Error",
                            Message: $"Tentativa de Path Traversal detectada no caminho do arquivo exportado: '{MaskPath(emlFile)}'",
                            ObjectId: Path.GetFileName(emlFile)
                        ));
                    }

                    // Check Empty EML
                    var fileInfo = new FileInfo(emlFile);
                    if (fileInfo.Length == 0)
                    {
                        emptyExportedFiles++;
                        issues.Add(new ValidationIssue(
                            Code: "VAL-ERR-EMPTYEML",
                            Severity: "Error",
                            Message: $"Arquivo EML de destino está vazio (0 bytes): '{Path.GetFileName(emlFile)}'",
                            ObjectId: Path.GetFileName(emlFile)
                        ));
                    }

                    exportedMessagesCount++;

                    // Parse EML if requested and within sample size
                    if (checkEmlParse && (sampleSize == null || checkedSampleCount < sampleSize.Value) && fileInfo.Length > 0)
                    {
                        checkedSampleCount++;
                        try
                        {
                            using var fs = new FileStream(emlFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                            var mimeMsg = MimeMessage.Load(fs);

                            // Validate minimum headers
                            bool hasIdentifier = !string.IsNullOrEmpty(mimeMsg.MessageId) || 
                                                 !string.IsNullOrEmpty(mimeMsg.Subject) || 
                                                 mimeMsg.Date != DateTimeOffset.MinValue || 
                                                 mimeMsg.From.Count > 0;

                            if (!hasIdentifier)
                            {
                                issues.Add(new ValidationIssue(
                                    Code: "VAL-ERR-EMLPARSE",
                                    Severity: "Error",
                                    Message: $"Arquivo EML inválido: ausência de cabeçalhos de identificação mínimos em '{Path.GetFileName(emlFile)}'",
                                    ObjectId: Path.GetFileName(emlFile)
                                ));
                            }

                            // Count attachments inside MIME
                            int attachmentsInMime = mimeMsg.Attachments.Count();
                            exportedAttachmentsCount += attachmentsInMime;
                        }
                        catch (Exception ex)
                        {
                            issues.Add(new ValidationIssue(
                                Code: "VAL-ERR-EMLCORRUPT",
                                Severity: "Error",
                                Message: $"Falha técnica ao decodificar arquivo EML '{Path.GetFileName(emlFile)}': {ex.Message}",
                                ObjectId: Path.GetFileName(emlFile)
                            ));
                        }
                    }
                }

                // Check Missing EML Expected Messages (from manifest)
                if (manifest != null)
                {
                    foreach (var msgRecord in manifest.ExportedMessages)
                    {
                        if (msgRecord.Status.Equals("Success", StringComparison.OrdinalIgnoreCase))
                        {
                            string expectedPath = Path.Combine(resolvedExportDir, msgRecord.RelativePath);
                            if (!File.Exists(expectedPath))
                            {
                                missingExpectedFiles++;
                                issues.Add(new ValidationIssue(
                                    Code: "VAL-ERR-MISSINGMSG",
                                    Severity: "Error",
                                    Message: $"Mensagem exportada esperada ausente no disco: '{msgRecord.RelativePath}' (ID: {msgRecord.MessageId})",
                                    ObjectId: msgRecord.MessageId
                                ));
                            }
                        }
                    }
                }
            }
            else if (exportFormat.Equals("mbox", StringComparison.OrdinalIgnoreCase))
            {
                // MBOX validation
                string[] mboxFiles = Directory.GetFiles(resolvedExportDir, "*", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f).Equals("mbox", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                foreach (var mboxFile in mboxFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    // Check Path Traversal
                    if (!IsSafePath(mboxFile, resolvedExportDir))
                    {
                        pathSafetyIssues++;
                        issues.Add(new ValidationIssue(
                            Code: "VAL-ERR-TRAVERSAL",
                            Severity: "Error",
                            Message: $"Tentativa de Path Traversal detectada no arquivo MBOX: '{MaskPath(mboxFile)}'",
                            ObjectId: Path.GetFileName(mboxFile)
                        ));
                    }

                    var fileInfo = new FileInfo(mboxFile);
                    if (fileInfo.Length == 0)
                    {
                        emptyExportedFiles++;
                        issues.Add(new ValidationIssue(
                            Code: "VAL-ERR-EMPTYMBOX",
                            Severity: "Error",
                            Message: $"Arquivo MBOX de destino está vazio (0 bytes): '{MaskPath(mboxFile)}'",
                            ObjectId: Path.GetFileName(mboxFile)
                        ));
                    }

                    if (checkMboxStructure && fileInfo.Length > 0)
                    {
                        int fromDelimiters = 0;
                        int unescapedFromCount = 0;
                        bool lastLineWasBlank = true;
                        int lineNum = 0;

                        try
                        {
                            using (var fs = new FileStream(mboxFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (var reader = new StreamReader(fs, System.Text.Encoding.UTF8))
                            {
                                string? line;
                                while ((line = await reader.ReadLineAsync()) != null)
                                {
                                    lineNum++;
                                    if (line.StartsWith("From ", StringComparison.Ordinal))
                                    {
                                        // Valid envelope From line is at line 1 or preceded by a blank line
                                        if (lineNum == 1 || lastLineWasBlank)
                                        {
                                            fromDelimiters++;
                                        }
                                        else
                                        {
                                            unescapedFromCount++;
                                            issues.Add(new ValidationIssue(
                                                Code: "VAL-ERR-UNESCAPEDFROM",
                                                Severity: "Error",
                                                Message: $"MBOX Violado: Linha 'From ' interna e sem escape na linha {lineNum} do arquivo '{Path.GetFileName(mboxFile)}'",
                                                ObjectId: Path.GetFileName(mboxFile)
                                            ));
                                        }
                                    }
                                    lastLineWasBlank = string.IsNullOrWhiteSpace(line);
                                }
                            }

                            exportedMessagesCount += fromDelimiters;
                            pathSafetyIssues += unescapedFromCount; // Escape violations are structural path/safety errors
                        }
                        catch (Exception ex)
                        {
                            issues.Add(new ValidationIssue(
                                Code: "VAL-ERR-MBOXPARSE",
                                Severity: "Error",
                                Message: $"Falha técnica ao auditar arquivo MBOX '{Path.GetFileName(mboxFile)}': {ex.Message}",
                                ObjectId: Path.GetFileName(mboxFile)
                            ));
                        }
                    }
                }

                // Check message count discrepancies
                if (manifest != null && exportedMessagesCount != exportedMessagesManifest)
                {
                    issues.Add(new ValidationIssue(
                        Code: "VAL-WARN-MBOXCOUNT",
                        Severity: "Warning",
                        Message: $"Divergência na contagem de mensagens do MBOX: indexado {exportedMessagesManifest} vs físico {exportedMessagesCount}",
                        ObjectId: null
                    ));
                }
            }

            // 5. Check physical attachments if requested
            if (checkAttachments)
            {
                // Scan physical folders for *-attachments
                string[] attachmentDirs = Directory.GetDirectories(resolvedExportDir, "*-attachments", SearchOption.AllDirectories);
                foreach (var attDir in attachmentDirs)
                {
                    // Check Path Traversal for attachment directory
                    if (!IsSafePath(attDir, resolvedExportDir))
                    {
                        pathSafetyIssues++;
                        issues.Add(new ValidationIssue(
                            Code: "VAL-ERR-TRAVERSAL",
                            Severity: "Error",
                            Message: $"Tentativa de Path Traversal no diretório de anexos: '{MaskPath(attDir)}'",
                            ObjectId: Path.GetFileName(attDir)
                        ));
                    }

                    string[] files = Directory.GetFiles(attDir);
                    foreach (var file in files)
                    {
                        if (!IsSafePath(file, resolvedExportDir))
                        {
                            pathSafetyIssues++;
                            issues.Add(new ValidationIssue(
                                Code: "VAL-ERR-TRAVERSAL",
                                Severity: "Error",
                                Message: $"Tentativa de Path Traversal em arquivo de anexo avulso: '{MaskPath(file)}'",
                                ObjectId: Path.GetFileName(file)
                            ));
                        }

                        // Validate file name is sanitised (no double dots or invalid characters)
                        string fileName = Path.GetFileName(file);
                        if (fileName.Contains("..") || fileName.Contains("/") || fileName.Contains("\\"))
                        {
                            issues.Add(new ValidationIssue(
                                Code: "VAL-WARN-ATTSANITISE",
                                Severity: "Warning",
                                Message: $"Nome de anexo físico não sanitizado de forma segura: '{fileName}'",
                                ObjectId: fileName
                            ));
                        }
                    }
                }
            }
        }
        else
        {
            // Export folder doesn't exist at all
            issues.Add(new ValidationIssue(
                Code: "VAL-ERR-NOEXPORTDIR",
                Severity: "Error",
                Message: $"Pasta de exportação de destino não foi localizada no disco físico: '{MaskPath(resolvedExportDir)}'",
                ObjectId: null
            ));
            missingExpectedFiles = selectedMessages;
        }

        // 6. Compare data per folder (Granular checks)
        foreach (var folder in allFolders)
        {
            int folderIndexed = folder.MessageCount ?? 0;
            int folderExported = 0;

            if (manifest != null)
            {
                folderExported = manifest.ExportedMessages
                    .Count(m => m.FolderPath == folder.FullPath && m.Status.Equals("Success", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // Approximate from physical EML files under folder subdir
                if (exportFormat.Equals("eml", StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedExportDir))
                {
                    string safeSubdir = string.Join("/", folder.FullPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
                    string folderPathPhysical = Path.Combine(resolvedExportDir, "eml", safeSubdir);
                    if (Directory.Exists(folderPathPhysical))
                    {
                        folderExported = Directory.GetFiles(folderPathPhysical, "*.eml").Length;
                    }
                }
            }

            int mismatch = Math.Abs(folderIndexed - folderExported);
            if (mismatch > 0 && manifest != null) // only warn if we did select everything and have manifest to verify
            {
                issues.Add(new ValidationIssue(
                    Code: "VAL-WARN-FOLDERMISMATCH",
                    Severity: "Warning",
                    Message: $"Divergência de contagem na pasta '{folder.FullPath}': indexado {folderIndexed} vs exportado {folderExported}",
                    ObjectId: folder.FullPath
                ));
            }

            folderResultsList.Add(new FolderValidationResult(
                FolderName: folder.FullPath,
                IndexedMessages: folderIndexed,
                ExportedMessages: folderExported,
                MismatchCount: mismatch
            ));
        }

        stopwatch.Stop();

        // 7. Summary metrics
        int warningCount = issues.Count(i => i.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
        int errorCount = issues.Count(i => i.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));

        // Determine Status (Gate 6)
        string status = "Passed";
        if (pathSafetyIssues > 0 || emptyExportedFiles > 0 || missingExpectedFiles > 0 || errorCount > 0)
        {
            status = "Failed";
        }
        else if (warningCount > 0)
        {
            status = strict ? "Failed" : "PassedWithWarnings";
        }

        var report = new ValidationReport(
            ValidationId: validationId,
            CaseId: caseInfo.CaseId,
            SourceFileMasked: MaskPath(caseInfo.SourceFile),
            SourceSha256: caseInfo.SourceSha256,
            AdapterName: caseInfo.AdapterName,
            AdapterVersion: caseInfo.AdapterVersion,
            ExportId: manifest?.ExportId ?? "N/A",
            ExportFormat: exportFormat,
            StartedAt: DateTimeOffset.Now.AddMilliseconds(-stopwatch.ElapsedMilliseconds),
            CompletedAt: DateTimeOffset.Now,
            DurationMs: stopwatch.ElapsedMilliseconds,
            IndexedMessages: indexedMessages,
            SelectedMessages: selectedMessages,
            ExportedMessages: exportedMessagesCount,
            FailedMessages: failedMessagesManifest,
            IndexedAttachments: indexedAttachments,
            ExportedAttachments: exportedAttachmentsCount,
            FailedAttachments: failedAttachmentsManifest + failedAttachmentsCount,
            EmptyExportedFiles: emptyExportedFiles,
            DuplicateOutputNames: duplicateOutputNames,
            MissingExpectedFiles: missingExpectedFiles,
            PathSafetyIssues: pathSafetyIssues,
            FoldersChecked: foldersCheckedList,
            FolderResults: folderResultsList,
            WarningCount: warningCount,
            ErrorCount: errorCount,
            Status: status,
            Issues: issues
        );

        // Save report to disk if outDir provided
        if (!string.IsNullOrEmpty(outDir))
        {
            if (!Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }
            string reportPath = Path.Combine(outDir, "validation-report.json");
            string reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(reportPath, reportJson, ct);
        }

        return report;
    }

    private void FlattenFolderHierarchy(IEnumerable<FolderNode> nodes, List<FolderNode> target)
    {
        foreach (var node in nodes)
        {
            target.Add(node);
            FlattenFolderHierarchy(node.Children, target);
        }
    }

    public static string MaskPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        string masked = path;
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrEmpty(userProfile))
        {
            masked = masked.Replace(userProfile, @"C:\Users\<USER>");
        }

        // Regex pattern to replace Windows username profiles generically
        masked = Regex.Replace(masked, @"(?i)[Cc]:\\Users\\[^\\]+", @"C:\Users\<USER>");
        return masked;
    }

    public static bool IsSafePath(string targetPath, string baseDir)
    {
        try
        {
            string fullTarget = Path.GetFullPath(targetPath);
            string fullBase = Path.GetFullPath(baseDir);
            return fullTarget.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

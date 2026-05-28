using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Domain;

namespace MailVault.Core;

public sealed class RecoveryExportRunner
{
    public int MessageTimeoutSeconds { get; set; } = 30;

    public async Task<RecoveryExportResult> ExportToEmlAsync(
        IMailStoreReader reader,
        IMessageExporter exporter,
        string sourcePath,
        string outputDir,
        string? targetFolderPath,
        IList<string>? messageIds,
        IProgress<RecoveryExportProgress>? progress,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<RecoveryExportIssue>();
        int totalFolders = 0, exportedMessages = 0, failedMessages = 0;
        int exportedAttachments = 0, failedAttachments = 0;
        var messageIdSet = messageIds != null
            ? new HashSet<string>(messageIds, StringComparer.OrdinalIgnoreCase)
            : null;

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        if (reader is ISessionAwareMailStoreReader sessionReader)
            await sessionReader.BeginReadSessionAsync(sourcePath, ct);

        try
        {
            var folders = await CollectAllFoldersAsync(reader, ct);
            var targetFolders = FilterFolders(folders, targetFolderPath);
            totalFolders = targetFolders.Count;

            int folderIndex = 0;
            foreach (var folder in targetFolders)
            {
                ct.ThrowIfCancellationRequested();
                folderIndex++;

                string folderDir = BuildFolderOutputDir(outputDir, folder.FullPath);
                Directory.CreateDirectory(folderDir);

                progress?.Report(new RecoveryExportProgress(
                    "ExportingEml", folder.FullPath, folderIndex, exportedMessages, failedMessages,
                    exportedAttachments, failedAttachments, $"Exportando pasta: {folder.FullPath}"));

                int seq = 0;
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                await foreach (var msg in reader.EnumerateMessagesAsync(folder.Id, ct))
                {
                    ct.ThrowIfCancellationRequested();

                    if (messageIdSet != null && !messageIdSet.Contains(msg.InternalId))
                        continue;

                    seq++;
                    string tmpPath = string.Empty;

                    using var msgCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    msgCts.CancelAfter(TimeSpan.FromSeconds(MessageTimeoutSeconds));

                    try
                    {
                        var readResult = await reader.GetMessageAsync(new MessageId(msg.InternalId), msgCts.Token);
                        var fullMsg = readResult.Success && readResult.Value != null ? readResult.Value : msg;

                        if (!readResult.Success)
                        {
                            foreach (var issue in readResult.Issues)
                                errors.Add(new RecoveryExportIssue(msg.InternalId, folder.FullPath,
                                    "MV-WARN-REC-GETMSG-PARTIAL", issue.Message, issue.TechnicalDetails));
                        }

                        string baseName = $"{seq:D6}_{SanitiseFilename(fullMsg.Subject ?? "(Sem Assunto)", "Email")}";
                        string uniqueFile = DeduplicateName(baseName + ".eml", usedNames);
                        usedNames.Add(uniqueFile);

                        string targetFile = TruncatePath(Path.Combine(folderDir, uniqueFile), outputDir);
                        tmpPath = targetFile + ".tmp";

                        var countingProvider = new CountingAttachmentProvider(reader, msg.InternalId, errors, folder.FullPath);

                        await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await exporter.ExportMessageAsync(fullMsg, countingProvider, fs, msgCts.Token);
                        }

                        if (File.Exists(targetFile)) File.Delete(targetFile);
                        File.Move(tmpPath, targetFile);
                        tmpPath = string.Empty;

                        int attFail = countingProvider.FailCount;
                        exportedMessages++;
                        exportedAttachments += fullMsg.Attachments.Count - attFail;
                        failedAttachments += attFail;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        failedMessages++;
                        errors.Add(new RecoveryExportIssue(msg.InternalId, folder.FullPath,
                            "MV-WARN-REC-TIMEOUT", $"Mensagem excedeu timeout de {MessageTimeoutSeconds}s."));
                        if (!string.IsNullOrEmpty(tmpPath)) CleanupFile(tmpPath);
                    }
                    catch (Exception ex)
                    {
                        failedMessages++;
                        errors.Add(new RecoveryExportIssue(msg.InternalId, folder.FullPath,
                            "MV-ERR-REC-EXPORT", ex.Message, ex.GetType().Name));
                        if (!string.IsNullOrEmpty(tmpPath)) CleanupFile(tmpPath);
                    }
                }
            }
        }
        finally
        {
            await EndSessionSafeAsync(reader);
        }

        return await FinalizeAsync(sourcePath, reader.ReaderName, outputDir, startedAt,
            totalFolders, exportedMessages, failedMessages, exportedAttachments, failedAttachments, errors);
    }

    public async Task<RecoveryExportResult> ExportToMboxAsync(
        IMailStoreReader reader,
        IMessageExporter mboxExporter,
        string sourcePath,
        string outputDir,
        string? targetFolderPath,
        IProgress<RecoveryExportProgress>? progress,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<RecoveryExportIssue>();
        int totalFolders = 0, exportedMessages = 0, failedMessages = 0;
        int exportedAttachments = 0, failedAttachments = 0;

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        if (reader is ISessionAwareMailStoreReader sessionReader)
            await sessionReader.BeginReadSessionAsync(sourcePath, ct);

        try
        {
            var folders = await CollectAllFoldersAsync(reader, ct);
            var targetFolders = FilterFolders(folders, targetFolderPath);
            totalFolders = targetFolders.Count;

            int folderIndex = 0;
            foreach (var folder in targetFolders)
            {
                ct.ThrowIfCancellationRequested();
                folderIndex++;

                string parentDir = BuildFolderOutputDir(outputDir, Path.GetDirectoryName(folder.FullPath) ?? "");
                Directory.CreateDirectory(parentDir);

                string mboxName = SanitiseFilename(Path.GetFileName(folder.FullPath) ?? folder.FullPath, "Folder") + ".mbox";
                string mboxPath = TruncatePath(Path.Combine(parentDir, mboxName), outputDir);

                progress?.Report(new RecoveryExportProgress(
                    "ExportingMbox", folder.FullPath, folderIndex, exportedMessages, failedMessages,
                    exportedAttachments, failedAttachments, $"Exportando pasta MBOX: {folder.FullPath}"));

                await using var mboxStream = new FileStream(mboxPath, FileMode.Create, FileAccess.Write, FileShare.None);

                await foreach (var msg in reader.EnumerateMessagesAsync(folder.Id, ct))
                {
                    ct.ThrowIfCancellationRequested();

                    using var msgCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    msgCts.CancelAfter(TimeSpan.FromSeconds(MessageTimeoutSeconds));

                    try
                    {
                        var readResult = await reader.GetMessageAsync(new MessageId(msg.InternalId), msgCts.Token);
                        var fullMsg = readResult.Success && readResult.Value != null ? readResult.Value : msg;

                        var countingProvider = new CountingAttachmentProvider(reader, msg.InternalId, errors, folder.FullPath);

                        await mboxExporter.ExportMessageAsync(fullMsg, countingProvider, mboxStream, msgCts.Token);

                        int attFail = countingProvider.FailCount;
                        exportedMessages++;
                        exportedAttachments += fullMsg.Attachments.Count - attFail;
                        failedAttachments += attFail;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        failedMessages++;
                        errors.Add(new RecoveryExportIssue(msg.InternalId, folder.FullPath,
                            "MV-WARN-REC-TIMEOUT", $"Mensagem excedeu timeout de {MessageTimeoutSeconds}s."));
                    }
                    catch (Exception ex)
                    {
                        failedMessages++;
                        errors.Add(new RecoveryExportIssue(msg.InternalId, folder.FullPath,
                            "MV-ERR-REC-MBOX", ex.Message, ex.GetType().Name));
                    }
                }
            }
        }
        finally
        {
            await EndSessionSafeAsync(reader);
        }

        return await FinalizeAsync(sourcePath, reader.ReaderName, outputDir, startedAt,
            totalFolders, exportedMessages, failedMessages, exportedAttachments, failedAttachments, errors);
    }

    private static async Task<List<FolderNode>> CollectAllFoldersAsync(IMailStoreReader reader, CancellationToken ct)
    {
        var result = new List<FolderNode>();
        await foreach (var folder in reader.EnumerateFoldersAsync(ct))
        {
            result.Add(folder);
            CollectFoldersRecursive(folder, result);
        }
        return result;
    }

    private static void CollectFoldersRecursive(FolderNode node, List<FolderNode> target)
    {
        foreach (var child in node.Children)
        {
            if (!target.Any(f => f.Id.Value == child.Id.Value))
            {
                target.Add(child);
                CollectFoldersRecursive(child, target);
            }
        }
    }

    private static List<FolderNode> FilterFolders(List<FolderNode> folders, string? targetFolderPath)
    {
        if (string.IsNullOrEmpty(targetFolderPath))
            return folders;

        return folders.Where(f =>
            f.FullPath.Equals(targetFolderPath, StringComparison.OrdinalIgnoreCase) ||
            f.FullPath.StartsWith(targetFolderPath + "/", StringComparison.OrdinalIgnoreCase) ||
            f.FullPath.StartsWith(targetFolderPath + "\\", StringComparison.OrdinalIgnoreCase)
        ).ToList();
    }

    private static string BuildFolderOutputDir(string outputDir, string folderPath)
    {
        var segments = folderPath
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => SanitiseFilename(s, "Folder"))
            .ToArray();

        return segments.Length > 0
            ? Path.Combine(new[] { outputDir }.Concat(segments).ToArray())
            : outputDir;
    }

    private static string TruncatePath(string path, string baseDir)
    {
        if (path.Length <= 240) return path;

        string dir = Path.GetDirectoryName(path) ?? baseDir;
        string ext = Path.GetExtension(path);
        string nameNoExt = Path.GetFileNameWithoutExtension(path);
        int maxName = 240 - dir.Length - ext.Length - 1;
        if (maxName < 5) maxName = 5;
        return Path.Combine(dir, (nameNoExt.Length > maxName ? nameNoExt.Substring(0, maxName) : nameNoExt) + ext);
    }

    private static string DeduplicateName(string name, HashSet<string> used)
    {
        if (!used.Contains(name)) return name;
        string ext = Path.GetExtension(name);
        string nameNoExt = Path.GetFileNameWithoutExtension(name);
        int i = 1;
        while (used.Contains($"{nameNoExt}_{i}{ext}")) i++;
        return $"{nameNoExt}_{i}{ext}";
    }

    public static string SanitiseFilename(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        string clean = name.Replace("../", "_").Replace("..\\", "_").Replace("..", "_");
        var invalid = Path.GetInvalidFileNameChars();
        clean = new string(clean.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (clean.Length > 80) clean = clean.Substring(0, 77) + "...";
        return clean.Trim();
    }

    private static void CleanupFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private static async Task EndSessionSafeAsync(IMailStoreReader reader)
    {
        if (reader is ISessionAwareMailStoreReader s)
        {
            try { await s.EndReadSessionAsync(CancellationToken.None); } catch { /* best-effort */ }
        }
    }

    private static async Task<RecoveryExportResult> FinalizeAsync(
        string sourcePath, string engine, string outputDir, DateTimeOffset startedAt,
        int totalFolders, int exportedMessages, int failedMessages,
        int exportedAttachments, int failedAttachments,
        List<RecoveryExportIssue> errors)
    {
        var finishedAt = DateTimeOffset.UtcNow;
        var result = new RecoveryExportResult(
            sourcePath, engine, startedAt, finishedAt, outputDir,
            totalFolders, exportedMessages + failedMessages,
            exportedMessages, failedMessages, exportedAttachments, failedAttachments, errors);

        await WriteReportAsync(result);
        return result;
    }

    private static async Task WriteReportAsync(RecoveryExportResult result)
    {
        try
        {
            var reportData = new
            {
                sourcePath = result.SourcePath,
                engine = result.Engine,
                startedAt = result.StartedAt.ToString("o"),
                finishedAt = result.FinishedAt.ToString("o"),
                totalMessages = result.TotalMessages,
                attemptedMessages = result.TotalMessages,
                exportedMessages = result.ExportedMessages,
                failedMessages = result.FailedMessages,
                exportedAttachments = result.ExportedAttachments,
                failedAttachments = result.FailedAttachments,
                outputPath = result.OutputDir,
                errorsSummary = result.Errors.Select(e => e.ErrorMessage).Distinct().ToList(),
                failedItems = result.Errors.Select(e => new
                {
                    id = e.MessageId,
                    folder = e.FolderPath,
                    code = e.ErrorCode,
                    error = e.ErrorMessage,
                    details = e.TechnicalDetails
                }).ToList()
            };

            string json = JsonSerializer.Serialize(reportData, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(result.OutputDir, "_mailvault-export-report.json"), json);

            var csv = new StringBuilder();
            csv.AppendLine("MessageId,FolderPath,ErrorCode,ErrorMessage,TechnicalDetails");
            foreach (var e in result.Errors)
                csv.AppendLine($"{EscapeCsv(e.MessageId)},{EscapeCsv(e.FolderPath)},{EscapeCsv(e.ErrorCode)},{EscapeCsv(e.ErrorMessage)},{EscapeCsv(e.TechnicalDetails)}");

            await File.WriteAllTextAsync(
                Path.Combine(result.OutputDir, "_mailvault-export-errors.csv"),
                csv.ToString(), Encoding.UTF8);
        }
        catch { /* best-effort — report failure is not critical */ }
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private sealed class CountingAttachmentProvider : IAttachmentContentProvider
    {
        private readonly IMailStoreReader _reader;
        private readonly string _messageId;
        private readonly List<RecoveryExportIssue> _errors;
        private readonly string _folderPath;

        public int FailCount { get; private set; }

        public CountingAttachmentProvider(
            IMailStoreReader reader, string messageId,
            List<RecoveryExportIssue> errors, string folderPath)
        {
            _reader = reader;
            _messageId = messageId;
            _errors = errors;
            _folderPath = folderPath;
        }

        public async Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct)
        {
            try
            {
                return await _reader.OpenAttachmentStreamAsync(messageId, attachmentId, ct);
            }
            catch (Exception ex)
            {
                FailCount++;
                _errors.Add(new RecoveryExportIssue(_messageId, _folderPath,
                    "MV-WARN-REC-ATTACH", ex.Message, ex.GetType().Name));
                throw;
            }
        }
    }
}

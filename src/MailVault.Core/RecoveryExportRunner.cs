using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    public Task<RecoveryExportResult> ExportToEmlAsync(
        IMailStoreReader reader,
        IMessageExporter exporter,
        string sourcePath,
        string outputDir,
        string? targetFolderPath,
        IList<string>? messageIds,
        IProgress<RecoveryExportProgress>? progress,
        CancellationToken ct,
        RecoveryExportOptions? options = null)
    {
        return RunAsync(Mode.Eml, reader, exporter, sourcePath, outputDir, targetFolderPath,
            messageIds, progress, ct, options ?? new RecoveryExportOptions());
    }

    public Task<RecoveryExportResult> ExportToMboxAsync(
        IMailStoreReader reader,
        IMessageExporter mboxExporter,
        string sourcePath,
        string outputDir,
        string? targetFolderPath,
        IProgress<RecoveryExportProgress>? progress,
        CancellationToken ct,
        RecoveryExportOptions? options = null)
    {
        return RunAsync(Mode.Mbox, reader, mboxExporter, sourcePath, outputDir, targetFolderPath,
            messageIds: null, progress, ct, options ?? new RecoveryExportOptions());
    }

    private enum Mode { Eml, Mbox }

    private async Task<RecoveryExportResult> RunAsync(
        Mode mode,
        IMailStoreReader reader,
        IMessageExporter exporter,
        string sourcePath,
        string outputDir,
        string? targetFolderPath,
        IList<string>? messageIds,
        IProgress<RecoveryExportProgress>? progress,
        CancellationToken userToken,
        RecoveryExportOptions options)
    {
        int perMsgTimeout = options.MessageTimeoutSeconds > 0 ? options.MessageTimeoutSeconds : MessageTimeoutSeconds;
        var startedAt = DateTimeOffset.UtcNow;
        var wall = Stopwatch.StartNew();
        var errors = new List<RecoveryExportIssue>();
        var metrics = new MetricsCollector();
        int totalFolders = 0, exportedMessages = 0, failedMessages = 0;
        int exportedAttachments = 0, failedAttachments = 0;
        bool stoppedByLimit = false;
        var status = RecoveryExportStatus.Completed;

        var messageIdSet = messageIds != null
            ? new HashSet<string>(messageIds, StringComparer.OrdinalIgnoreCase)
            : null;

        // Otimização medida: se o reader declara que entrega conteúdo completo na enumeração
        // (IMetadataOnlyAware + MetadataOnly=false), o GetMessageAsync por item é redundante
        // (ambos chamam o mesmo mapeamento) e custa uma busca O(N) na árvore por mensagem.
        bool skipReRead = !options.ForceFullMessageReRead
            && reader is IMetadataOnlyAware mo && !mo.MetadataOnly;

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        // Cancelamento: linka token do usuário + timeout opcional da sessão.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        if (options.TimeoutSeconds is double t && t > 0)
            runCts.CancelAfter(TimeSpan.FromSeconds(t));
        var token = runCts.Token;

        var checkpoint = new CheckpointState(options, outputDir, sourcePath, reader.ReaderName);

        try
        {
            // Dentro do try: se a abertura falhar (ex.: cabeçalho destruído), finaliza com
            // status=Failed e relatório, em vez de lançar exceção não controlada.
            if (reader is ISessionAwareMailStoreReader sessionReader)
                await sessionReader.BeginReadSessionAsync(sourcePath, token);

            var folders = await CollectAllFoldersAsync(reader, token);
            var targetFolders = FilterFolders(folders, targetFolderPath);
            totalFolders = targetFolders.Count;

            int folderIndex = 0;
            foreach (var folder in targetFolders)
            {
                token.ThrowIfCancellationRequested();
                folderIndex++;
                var folderSw = Stopwatch.StartNew();

                string folderDir = BuildFolderOutputDir(outputDir, folder.FullPath);
                FileStream? mboxStream = null;
                if (mode == Mode.Eml)
                {
                    Directory.CreateDirectory(folderDir);
                }
                else
                {
                    string parentDir = BuildFolderOutputDir(outputDir, Path.GetDirectoryName(folder.FullPath) ?? "");
                    Directory.CreateDirectory(parentDir);
                    string mboxName = SanitiseFilename(Path.GetFileName(folder.FullPath) ?? folder.FullPath, "Folder") + ".mbox";
                    string mboxPath = TruncatePath(Path.Combine(parentDir, mboxName), outputDir);
                    mboxStream = new FileStream(mboxPath, FileMode.Create, FileAccess.Write, FileShare.None);
                }

                progress?.Report(new RecoveryExportProgress(
                    mode == Mode.Eml ? "ExportingEml" : "ExportingMbox", folder.FullPath, folderIndex,
                    exportedMessages, failedMessages, exportedAttachments, failedAttachments,
                    $"Exportando pasta: {folder.FullPath}"));

                int seq = 0, folderMsgCount = 0;
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    await foreach (var msg in reader.EnumerateMessagesAsync(folder.Id, token))
                    {
                        token.ThrowIfCancellationRequested();

                        if (messageIdSet != null && !messageIdSet.Contains(msg.InternalId))
                            continue;
                        if (options.MaxFolderMessages is int fmax && folderMsgCount >= fmax)
                            break;
                        if (options.MaxMessages is int gmax && (exportedMessages + failedMessages) >= gmax)
                        {
                            stoppedByLimit = true;
                            break;
                        }

                        seq++; folderMsgCount++;
                        string tmpPath = string.Empty;

                        using var msgCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        msgCts.CancelAfter(TimeSpan.FromSeconds(perMsgTimeout));

                        try
                        {
                            MailItem fullMsg;
                            if (skipReRead)
                            {
                                fullMsg = msg;
                            }
                            else
                            {
                                var swGet = Stopwatch.StartNew();
                                var readResult = await reader.GetMessageAsync(new MessageId(msg.InternalId), msgCts.Token);
                                swGet.Stop();
                                metrics.GetMessageMs += swGet.Elapsed.TotalMilliseconds;
                                fullMsg = readResult.Success && readResult.Value != null ? readResult.Value : msg;
                                if (!readResult.Success)
                                    foreach (var issue in readResult.Issues)
                                        errors.Add(new RecoveryExportIssue(msg.InternalId, folder.FullPath,
                                            "MV-WARN-REC-GETMSG-PARTIAL", issue.Message, issue.TechnicalDetails));
                            }

                            var provider = new CountingAttachmentProvider(reader, msg.InternalId, errors, folder.FullPath, metrics);
                            long bytesBefore;
                            string itemName;

                            if (mode == Mode.Eml)
                            {
                                string baseName = $"{seq:D6}_{SanitiseFilename(fullMsg.Subject ?? "(Sem Assunto)", "Email")}";
                                string uniqueFile = DeduplicateName(baseName + ".eml", usedNames);
                                usedNames.Add(uniqueFile);
                                itemName = uniqueFile;
                                string targetFile = TruncatePath(Path.Combine(folderDir, uniqueFile), outputDir);
                                tmpPath = targetFile + ".tmp";

                                var swExp = Stopwatch.StartNew();
                                await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                                    await exporter.ExportMessageAsync(fullMsg, provider, fs, msgCts.Token);
                                swExp.Stop();
                                metrics.SerializeWriteMs += Math.Max(0, swExp.Elapsed.TotalMilliseconds - provider.AttachmentMs);

                                if (File.Exists(targetFile)) File.Delete(targetFile);
                                File.Move(tmpPath, targetFile);
                                tmpPath = string.Empty;
                                long fsize = SafeFileLength(targetFile);
                                metrics.BytesWritten += fsize;
                                metrics.NoteMessage(fsize, uniqueFile);
                            }
                            else
                            {
                                bytesBefore = mboxStream!.Position;
                                itemName = fullMsg.Subject ?? "(Sem Assunto)";
                                var swExp = Stopwatch.StartNew();
                                await exporter.ExportMessageAsync(fullMsg, provider, mboxStream, msgCts.Token);
                                swExp.Stop();
                                metrics.SerializeWriteMs += Math.Max(0, swExp.Elapsed.TotalMilliseconds - provider.AttachmentMs);
                                long delta = mboxStream.Position - bytesBefore;
                                metrics.BytesWritten += delta;
                                metrics.NoteMessage(delta, itemName);
                            }

                            metrics.AttachmentMs += provider.AttachmentMs;
                            int attFail = provider.FailCount;
                            exportedMessages++;
                            exportedAttachments += fullMsg.Attachments.Count - attFail;
                            failedAttachments += attFail;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            if (!string.IsNullOrEmpty(tmpPath)) CleanupFile(tmpPath);
                            throw; // cancelamento/timeout da sessão — propaga para finalizar com status
                        }
                        catch (OperationCanceledException)
                        {
                            failedMessages++;
                            errors.Add(new RecoveryExportIssue(msg.InternalId, folder.FullPath,
                                "MV-WARN-REC-TIMEOUT", $"Mensagem excedeu timeout de {perMsgTimeout}s."));
                            if (!string.IsNullOrEmpty(tmpPath)) CleanupFile(tmpPath);
                        }
                        catch (Exception ex)
                        {
                            failedMessages++;
                            errors.Add(new RecoveryExportIssue(msg.InternalId, folder.FullPath,
                                mode == Mode.Eml ? "MV-ERR-REC-EXPORT" : "MV-ERR-REC-MBOX", ex.Message, ex.GetType().Name));
                            if (!string.IsNullOrEmpty(tmpPath)) CleanupFile(tmpPath);
                        }

                        // Checkpoint incremental (por contagem ou tempo).
                        if (checkpoint.ShouldWrite(exportedMessages + failedMessages))
                            await checkpoint.WriteAsync(BuildSnapshot(sourcePath, reader.ReaderName, startedAt,
                                wall, totalFolders, exportedMessages, failedMessages, exportedAttachments,
                                failedAttachments, errors, metrics, "InProgress", folder.FullPath, folderIndex));
                    }
                }
                finally
                {
                    if (mboxStream != null) await mboxStream.DisposeAsync();
                }

                folderSw.Stop();
                metrics.NoteFolder(folder.FullPath, folderSw.Elapsed.TotalSeconds);
                // Checkpoint na troca de pasta.
                await checkpoint.WriteAsync(BuildSnapshot(sourcePath, reader.ReaderName, startedAt, wall,
                    totalFolders, exportedMessages, failedMessages, exportedAttachments, failedAttachments,
                    errors, metrics, "InProgress", folder.FullPath, folderIndex), force: true);

                if (stoppedByLimit) break;
            }
        }
        catch (OperationCanceledException)
        {
            status = userToken.IsCancellationRequested ? RecoveryExportStatus.CancelledByUser
                                                       : RecoveryExportStatus.CancelledByTimeout;
        }
        catch (Exception ex)
        {
            status = RecoveryExportStatus.Failed;
            errors.Add(new RecoveryExportIssue(null, null, "MV-ERR-REC-FATAL", ex.Message, ex.GetType().Name));
        }
        finally
        {
            await EndSessionSafeAsync(reader);
        }

        if (status == RecoveryExportStatus.Completed)
        {
            if (stoppedByLimit || failedMessages > 0)
                status = RecoveryExportStatus.PartialCompleted;
        }

        wall.Stop();
        var built = metrics.Build(wall.Elapsed.TotalSeconds, exportedMessages);
        var result = new RecoveryExportResult(
            sourcePath, reader.ReaderName, startedAt, DateTimeOffset.UtcNow, outputDir,
            totalFolders, exportedMessages + failedMessages, exportedMessages, failedMessages,
            exportedAttachments, failedAttachments, errors, status, built);

        await WriteReportAsync(result);
        checkpoint.CleanupPartials();
        return result;
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static ReportSnapshot BuildSnapshot(
        string sourcePath, string engine, DateTimeOffset startedAt, Stopwatch wall,
        int totalFolders, int exported, int failed, int exportedAtt, int failedAtt,
        List<RecoveryExportIssue> errors, MetricsCollector metrics, string status,
        string currentFolder, int foldersProcessed)
    {
        var snapshotResult = new RecoveryExportResult(
            sourcePath, engine, startedAt, DateTimeOffset.UtcNow, "",
            totalFolders, exported + failed, exported, failed, exportedAtt, failedAtt,
            errors.ToList(), RecoveryExportStatus.PartialCompleted,
            metrics.Build(wall.Elapsed.TotalSeconds, exported));
        return new ReportSnapshot(snapshotResult, status, currentFolder, foldersProcessed,
            wall.Elapsed.TotalSeconds);
    }

    // ---- coleta / filtro de pastas ----

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

    // ---- classificação / relatórios ----

    public static string ClassifyResult(RecoveryExportResult r)
    {
        switch (r.Status)
        {
            case RecoveryExportStatus.CancelledByUser: return "Cancelado pelo usuário";
            case RecoveryExportStatus.CancelledByTimeout: return "Cancelado por timeout";
            case RecoveryExportStatus.Failed: return "Falha";
        }
        if (r.ExportedMessages == 0)
            return r.TotalMessages == 0 ? "Inconclusivo (nenhuma mensagem localizada)" : "Inconclusivo (nada exportado)";
        if (r.FailedMessages > 0 || r.FailedAttachments > 0 || r.Status == RecoveryExportStatus.PartialCompleted)
            return "Parcial (há falhas ou limite atingido)";
        return "Completo (sem falhas registradas)";
    }

    private static async Task WriteReportAsync(RecoveryExportResult result)
    {
        try
        {
            await File.WriteAllTextAsync(Path.Combine(result.OutputDir, "_mailvault-export-report.json"),
                BuildJsonReport(result, "Final"));

            var csv = new StringBuilder();
            csv.AppendLine("MessageId,FolderPath,ErrorCode,ErrorMessage,TechnicalDetails");
            foreach (var e in result.Errors)
                csv.AppendLine($"{EscapeCsv(e.MessageId)},{EscapeCsv(e.FolderPath)},{EscapeCsv(e.ErrorCode)},{EscapeCsv(e.ErrorMessage)},{EscapeCsv(e.TechnicalDetails)}");
            await File.WriteAllTextAsync(Path.Combine(result.OutputDir, "_mailvault-export-errors.csv"), csv.ToString(), Encoding.UTF8);

            await File.WriteAllTextAsync(Path.Combine(result.OutputDir, "_mailvault-export-report.md"),
                BuildMarkdownReport(result, "Final"), Encoding.UTF8);
        }
        catch { /* best-effort — falha de relatório não é crítica */ }
    }

    private static string BuildJsonReport(RecoveryExportResult r, string phase)
    {
        var m = r.Metrics;
        var data = new
        {
            phase,
            status = r.Status.ToString(),
            classification = ClassifyResult(r),
            sourcePath = r.SourcePath,
            engine = r.Engine,
            startedAt = r.StartedAt.ToString("o"),
            finishedAt = r.FinishedAt.ToString("o"),
            totalFolders = r.TotalFolders,
            attemptedMessages = r.TotalMessages,
            exportedMessages = r.ExportedMessages,
            failedMessages = r.FailedMessages,
            exportedAttachments = r.ExportedAttachments,
            failedAttachments = r.FailedAttachments,
            outputPath = r.OutputDir,
            metrics = m == null ? null : new
            {
                wallClockSeconds = Math.Round(m.WallClockSeconds, 2),
                messagesPerSecond = Math.Round(m.MessagesPerSecond, 3),
                avgMillisecondsPerMessage = Math.Round(m.AvgMillisecondsPerMessage, 1),
                megabytesPerMinute = Math.Round(m.MegabytesPerMinute, 2),
                bytesWritten = m.BytesWritten,
                largestMessageBytes = m.LargestMessageBytes,
                largestMessageName = m.LargestMessageName,
                largestAttachmentBytes = m.LargestAttachmentBytes,
                largestAttachmentName = m.LargestAttachmentName,
                slowestFolder = m.SlowestFolder,
                slowestFolderSeconds = Math.Round(m.SlowestFolderSeconds, 2),
                stageGetMessageMs = Math.Round(m.GetMessageMs, 1),
                stageSerializeWriteMs = Math.Round(m.SerializeWriteMs, 1),
                stageAttachmentMs = Math.Round(m.AttachmentMs, 1),
                slowestStage = m.SlowestStage
            },
            errorsSummary = r.Errors.Select(e => e.ErrorMessage).Distinct().ToList(),
            failedItems = r.Errors.Select(e => new { id = e.MessageId, folder = e.FolderPath, code = e.ErrorCode, error = e.ErrorMessage, details = e.TechnicalDetails }).ToList()
        };
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static string BuildMarkdownReport(RecoveryExportResult r, string phase)
    {
        var m = r.Metrics;
        var byCode = r.Errors.GroupBy(e => e.ErrorCode).OrderByDescending(g => g.Count())
            .Select(g => (Code: g.Key, Count: g.Count(), Sample: g.First().ErrorMessage)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Relatório de Recuperação — MailVault ({phase})");
        sb.AppendLine();
        sb.AppendLine($"- **Arquivo analisado:** `{r.SourcePath}`");
        sb.AppendLine($"- **Motor de leitura:** {r.Engine}");
        sb.AppendLine($"- **Status:** {r.Status}");
        sb.AppendLine($"- **Classificação:** {ClassifyResult(r)}");
        sb.AppendLine($"- **Início:** {r.StartedAt:o}");
        sb.AppendLine($"- **Fim:** {r.FinishedAt:o}");
        sb.AppendLine($"- **Saída:** `{r.OutputDir}`");
        sb.AppendLine();
        sb.AppendLine("## Totais");
        sb.AppendLine();
        sb.AppendLine("| Métrica | Valor |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine($"| Pastas processadas | {r.TotalFolders} |");
        sb.AppendLine($"| Mensagens tentadas | {r.TotalMessages} |");
        sb.AppendLine($"| Mensagens exportadas | {r.ExportedMessages} |");
        sb.AppendLine($"| Mensagens com falha | {r.FailedMessages} |");
        sb.AppendLine($"| Anexos exportados | {r.ExportedAttachments} |");
        sb.AppendLine($"| Anexos com falha | {r.FailedAttachments} |");
        if (m != null)
        {
            sb.AppendLine();
            sb.AppendLine("## Performance");
            sb.AppendLine();
            sb.AppendLine("| Métrica | Valor |");
            sb.AppendLine("|---|---:|");
            sb.AppendLine($"| Tempo total (s) | {m.WallClockSeconds:F2} |");
            sb.AppendLine($"| Mensagens/segundo | {m.MessagesPerSecond:F3} |");
            sb.AppendLine($"| Tempo médio/mensagem (ms) | {m.AvgMillisecondsPerMessage:F1} |");
            sb.AppendLine($"| MB/minuto | {m.MegabytesPerMinute:F2} |");
            sb.AppendLine($"| Bytes escritos | {m.BytesWritten:N0} |");
            sb.AppendLine($"| Maior mensagem | {m.LargestMessageBytes:N0} bytes ({m.LargestMessageName}) |");
            sb.AppendLine($"| Maior anexo | {m.LargestAttachmentBytes:N0} bytes ({m.LargestAttachmentName}) |");
            sb.AppendLine($"| Pasta mais lenta | {m.SlowestFolder} ({m.SlowestFolderSeconds:F2}s) |");
            sb.AppendLine($"| Etapa: GetMessage (ms) | {m.GetMessageMs:F1} |");
            sb.AppendLine($"| Etapa: Serialização+Escrita (ms) | {m.SerializeWriteMs:F1} |");
            sb.AppendLine($"| Etapa: Anexos (ms) | {m.AttachmentMs:F1} |");
            sb.AppendLine($"| **Etapa mais lenta** | **{m.SlowestStage}** |");
        }
        sb.AppendLine();
        sb.AppendLine("## Erros por etapa/código");
        sb.AppendLine();
        if (byCode.Count == 0) sb.AppendLine("_Nenhum erro registrado._");
        else
        {
            sb.AppendLine("| Código | Ocorrências | Exemplo |");
            sb.AppendLine("|---|---:|---|");
            foreach (var c in byCode)
                sb.AppendLine($"| {c.Code} | {c.Count} | {(c.Sample ?? "").Replace("|", "/").Replace("\n", " ")} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Limitações conhecidas");
        sb.AppendLine();
        sb.AppendLine("- Recuperação por **carving** de blocos/itens órfãos NÃO está implementada nesta build.");
        sb.AppendLine("- OST Cached Exchange \"somente cabeçalho\": corpo/anexos só no servidor (irrecuperáveis offline).");
        sb.AppendLine("- Geração de **PST limpo** não suportada (ver `recover-pst`); use EML/MBOX.");
        sb.AppendLine();
        sb.AppendLine("_Arquivos correlatos: `_mailvault-export-report.json`, `_mailvault-export-errors.csv`._");
        return sb.ToString();
    }

    // ---- métricas ----

    private sealed class MetricsCollector
    {
        public long BytesWritten;
        public long LargestMessageBytes;
        public string? LargestMessageName;
        public long LargestAttachmentBytes;
        public string? LargestAttachmentName;
        public double GetMessageMs, SerializeWriteMs, AttachmentMs;
        public string? SlowestFolder;
        public double SlowestFolderSeconds;

        public void NoteMessage(long bytes, string? name)
        {
            if (bytes > LargestMessageBytes) { LargestMessageBytes = bytes; LargestMessageName = name; }
        }

        public void NoteAttachment(long bytes, string? name)
        {
            if (bytes > LargestAttachmentBytes) { LargestAttachmentBytes = bytes; LargestAttachmentName = name; }
        }

        public void NoteFolder(string path, double seconds)
        {
            if (seconds > SlowestFolderSeconds) { SlowestFolderSeconds = seconds; SlowestFolder = path; }
        }

        public RecoveryExportMetrics Build(double wallSeconds, int exported)
        {
            double mps = wallSeconds > 0 ? exported / wallSeconds : 0;
            double avgMs = exported > 0 ? (wallSeconds * 1000.0) / exported : 0;
            double mbpm = wallSeconds > 0 ? (BytesWritten / 1048576.0) / (wallSeconds / 60.0) : 0;
            var stages = new (string Name, double Ms)[]
            {
                ("GetMessage", GetMessageMs),
                ("SerializeWrite", SerializeWriteMs),
                ("Attachments", AttachmentMs)
            };
            string slowest = stages.OrderByDescending(s => s.Ms).First().Name;
            return new RecoveryExportMetrics(
                Math.Round(wallSeconds, 2), mps, avgMs, mbpm, BytesWritten,
                LargestMessageBytes, LargestMessageName, LargestAttachmentBytes, LargestAttachmentName,
                SlowestFolder, SlowestFolderSeconds, GetMessageMs, SerializeWriteMs, AttachmentMs, slowest);
        }
    }

    private sealed record ReportSnapshot(
        RecoveryExportResult Result, string Phase, string CurrentFolder,
        int FoldersProcessed, double ElapsedSeconds);

    // ---- checkpoint incremental ----

    private sealed class CheckpointState
    {
        private readonly RecoveryExportOptions _opts;
        private readonly string _outputDir;
        private readonly string _sourcePath;
        private readonly string _engine;
        private readonly Stopwatch _sinceLast = Stopwatch.StartNew();
        private int _lastCount;

        public CheckpointState(RecoveryExportOptions opts, string outputDir, string sourcePath, string engine)
        {
            _opts = opts; _outputDir = outputDir; _sourcePath = sourcePath; _engine = engine;
        }

        public bool ShouldWrite(int processed)
        {
            bool byCount = _opts.CheckpointEveryMessages > 0 && (processed - _lastCount) >= _opts.CheckpointEveryMessages;
            bool byTime = _opts.CheckpointIntervalSeconds > 0 && _sinceLast.Elapsed.TotalSeconds >= _opts.CheckpointIntervalSeconds;
            return byCount || byTime;
        }

        public async Task WriteAsync(ReportSnapshot snap, bool force = false)
        {
            if (!force && !ShouldWrite(snap.Result.ExportedMessages + snap.Result.FailedMessages))
                return;
            _lastCount = snap.Result.ExportedMessages + snap.Result.FailedMessages;
            _sinceLast.Restart();

            try
            {
                // Relatório parcial reaproveita o mesmo resultado mas em OutputDir real.
                var r = snap.Result with { OutputDir = _outputDir };
                await File.WriteAllTextAsync(Path.Combine(_outputDir, "_mailvault-export-report.partial.json"),
                    BuildJsonReport(r, "Partial"));
                await File.WriteAllTextAsync(Path.Combine(_outputDir, "_mailvault-export-report.partial.md"),
                    BuildMarkdownReport(r, "Partial"), Encoding.UTF8);

                var progress = new
                {
                    status = snap.Phase,
                    currentFolder = snap.CurrentFolder,
                    foldersProcessed = snap.FoldersProcessed,
                    exportedMessages = r.ExportedMessages,
                    failedMessages = r.FailedMessages,
                    exportedAttachments = r.ExportedAttachments,
                    failedAttachments = r.FailedAttachments,
                    elapsedSeconds = Math.Round(snap.ElapsedSeconds, 1),
                    messagesPerSecond = r.Metrics == null ? 0 : Math.Round(r.Metrics.MessagesPerSecond, 3),
                    updatedAt = DateTimeOffset.UtcNow.ToString("o")
                };
                string progressJson = JsonSerializer.Serialize(progress, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(_outputDir, "progress.json"), progressJson);
                if (!string.IsNullOrEmpty(_opts.ProgressJsonPath))
                    await File.WriteAllTextAsync(_opts.ProgressJsonPath, progressJson);
            }
            catch { /* best-effort */ }
        }

        public void CleanupPartials()
        {
            foreach (var name in new[] { "_mailvault-export-report.partial.json", "_mailvault-export-report.partial.md" })
            {
                try
                {
                    var p = Path.Combine(_outputDir, name);
                    if (File.Exists(p)) File.Delete(p);
                }
                catch { /* best-effort */ }
            }
        }
    }

    private sealed class CountingAttachmentProvider : IAttachmentContentProvider
    {
        private readonly IMailStoreReader _reader;
        private readonly string _messageId;
        private readonly List<RecoveryExportIssue> _errors;
        private readonly string _folderPath;
        private readonly MetricsCollector _metrics;

        public int FailCount { get; private set; }
        public double AttachmentMs { get; private set; }

        public CountingAttachmentProvider(
            IMailStoreReader reader, string messageId,
            List<RecoveryExportIssue> errors, string folderPath, MetricsCollector metrics)
        {
            _reader = reader; _messageId = messageId; _errors = errors; _folderPath = folderPath; _metrics = metrics;
        }

        public async Task<Stream> OpenAttachmentStreamAsync(MessageId messageId, AttachmentId attachmentId, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var s = await _reader.OpenAttachmentStreamAsync(messageId, attachmentId, ct);
                sw.Stop();
                AttachmentMs += sw.Elapsed.TotalMilliseconds;
                try { if (s.CanSeek) _metrics.NoteAttachment(s.Length, attachmentId.Value); } catch { }
                return s;
            }
            catch (Exception ex)
            {
                sw.Stop();
                AttachmentMs += sw.Elapsed.TotalMilliseconds;
                FailCount++;
                _errors.Add(new RecoveryExportIssue(_messageId, _folderPath,
                    "MV-WARN-REC-ATTACH", ex.Message, ex.GetType().Name));
                throw;
            }
        }
    }
}

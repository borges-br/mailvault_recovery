using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Audit;
using MailVault.Core;
using MailVault.Domain;

namespace MailVault.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Configure Console encoding for special characters
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var rootCommand = new RootCommand("MailVault Recovery CLI — Ferramenta local e offline de recuperação forense.");

        var inspectCommand = new Command("inspect", "Inspeciona um arquivo .ost/.pst de origem, calcula seu hash SHA-256 e gera o manifesto.");
        
        var fileArgument = new Argument<FileInfo>("file")
        {
            Description = "O caminho do arquivo .ost/.pst a ser inspecionado.",
            Arity = ArgumentArity.ExactlyOne
        };
        inspectCommand.AddArgument(fileArgument);

        var outOption = new Option<DirectoryInfo>("--out", "O diretório de saída base para salvar a pasta do caso e o manifesto.")
        {
            IsRequired = false
        };
        outOption.SetDefaultValue(new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "mailvault-cases")));
        inspectCommand.AddOption(outOption);

        inspectCommand.SetHandler(async (FileInfo file, DirectoryInfo outDir) =>
        {
            await HandleInspectAsync(file, outDir);
        }, fileArgument, outOption);

        rootCommand.AddCommand(inspectCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task HandleInspectAsync(FileInfo file, DirectoryInfo outDir)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                  MailVault Recovery — Inspeção Técnica de Mídia                ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        if (!file.Exists)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] O arquivo especificado não existe: '{file.FullName}'");
            Console.ResetColor();
            Environment.Exit(1);
        }

        var startedAt = DateTimeOffset.Now;
        var caseId = ManifestService.GenerateCaseId(startedAt);
        string caseFolderPath = Path.Combine(outDir.FullName, caseId);
        string auditLogFilePath = Path.Combine(caseFolderPath, "audit.log");

        Console.WriteLine($"[*] Caso Inicializado: {caseId}");
        Console.WriteLine($"[*] Operador: {Environment.UserName}");
        Console.WriteLine($"[*] Arquivo: {file.FullName}");
        Console.WriteLine($"[*] Tamanho: {file.Length:N0} bytes");
        Console.WriteLine();

        var progressReporter = new ConsoleProgressReporter();
        var hashService = new HashService();
        var auditWriter = new FileAuditTrailWriter(auditLogFilePath);

        Console.WriteLine("[*] Iniciando cálculo de hash de integridade (SHA-256 por streaming)...");
        string sha256 = string.Empty;
        try
        {
            sha256 = await hashService.CalculateSha256Async(file.FullName, progressReporter, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] Falha ao calcular hash SHA-256: {ex.Message}");
            Console.ResetColor();
            Environment.Exit(2);
        }

        Console.WriteLine();

        // Perform preliminary status assessment and warning checks
        string extension = file.Extension.ToLowerInvariant();
        string preliminaryStatus = "Aprovado para Processamento";
        var warnings = new List<ExtractionIssue>();

        if (file.Length == 0)
        {
            preliminaryStatus = "Atenção: Arquivo Vazio";
            warnings.Add(new ExtractionIssue(
                Code: "MV-WARN-001",
                Severity: "Warning",
                Message: "O arquivo de origem possui tamanho de zero bytes.",
                ObjectId: file.Name,
                TechnicalDetails: "Tamanho de arquivo é 0."
            ));
        }

        if (extension != ".ost" && extension != ".pst")
        {
            preliminaryStatus = "Atenção: Extensão Não Padrão";
            warnings.Add(new ExtractionIssue(
                Code: "MV-WARN-002",
                Severity: "Warning",
                Message: $"A extensão do arquivo '{extension}' não é a padrão .ost ou .pst.",
                ObjectId: file.Name,
                TechnicalDetails: $"Extensão '{extension}' não reconhecida nativamente."
            ));
        }

        // Save manifest.json
        var manifest = new RecoveryManifest(
            CaseId: caseId,
            SourceFile: file.FullName,
            SourceSizeBytes: file.Length,
            SourceSha256: sha256,
            OperatorName: Environment.UserName,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.Now,
            ToolVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0",
            Actions: new List<string> { $"Inspected file: {file.Name}", "Generated integrity SHA-256 hash" },
            Warnings: warnings
        );

        string manifestPath = string.Empty;
        try
        {
            manifestPath = await ManifestService.SaveManifestAsync(outDir.FullName, manifest, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] Falha ao salvar manifest.json: {ex.Message}");
            Console.ResetColor();
            Environment.Exit(3);
        }

        // Write Audit Event
        var auditEvent = new AuditEvent(
            EventId: Guid.NewGuid().ToString(),
            Timestamp: DateTimeOffset.Now,
            Action: "FILE_INSPECTED",
            OperatorName: Environment.UserName,
            Details: $"Arquivo inspecionado com sucesso. Hash gerado: {sha256}. Pasta do caso criada.",
            FilePath: file.FullName,
            FileHash: sha256
        );

        try
        {
            await auditWriter.WriteEventAsync(auditEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[AVISO] Falha ao gravar trilha de auditoria: {ex.Message}");
            Console.ResetColor();
        }

        // Print required inspection details report
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                       RELATÓRIO TÉCNICO DE INSPEÇÃO                            ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();
        Console.WriteLine($"Caminho do arquivo  : {file.FullName}");
        Console.WriteLine($"Nome do arquivo     : {file.Name}");
        Console.WriteLine($"Extensão            : {extension}");
        Console.WriteLine($"Tamanho (bytes)     : {file.Length:N0}");
        Console.WriteLine($"SHA-256 (streaming) : {sha256}");
        Console.WriteLine($"Data/Hora Inspeção  : {startedAt:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine($"Status Preliminar   : {preliminaryStatus}");

        if (warnings.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Avisos:");
            foreach (var warn in warnings)
            {
                Console.WriteLine($"  - [{warn.Code}] [{warn.Severity}] {warn.Message}");
            }
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine("Avisos              : Nenhum aviso ou problema detectado.");
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"[*] Manifesto salvo com sucesso em:");
        Console.WriteLine($"    {manifestPath}");
        Console.WriteLine($"[*] Trilha de auditoria salva em:");
        Console.WriteLine($"    {auditLogFilePath}");
        Console.WriteLine("================================================================================");
        Console.ResetColor();
    }

    private sealed class ConsoleProgressReporter : IProgressReporter
    {
        private int _lastPercentageInt = -1;

        public void ReportProgress(double percentage, string status)
        {
            int pctInt = (int)Math.Round(percentage);
            // Throttle progress updates to avoid console flooding (only print when integer percentage changes)
            if (pctInt != _lastPercentageInt)
            {
                _lastPercentageInt = pctInt;

                int width = 80;
                try
                {
                    width = Console.WindowWidth;
                }
                catch
                {
                    // Fallback if stdout is redirected (CI, pipes, non-interactive shells)
                }

                string progressStr = $"[*] Progress: {percentage:F1}% - {status}";
                if (progressStr.Length < width - 1)
                {
                    progressStr = progressStr.PadRight(width - 1);
                }
                else
                {
                    progressStr = progressStr.Substring(0, width - 1);
                }

                Console.Write($"\r{progressStr}");
                if (pctInt >= 100)
                {
                    Console.WriteLine();
                }
            }
        }
    }
}

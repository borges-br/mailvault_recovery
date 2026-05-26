using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MailVault.Desktop.Services;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class ValidationPanelViewModel : ViewModelBase
{
    private readonly DesktopValidationService _validationService;
    private string _caseFolderPath = "";
    private string _validationStatus = "Aguardando execução da validação.";
    private string _reportStatus = "N/A"; 
    private string _reportMetrics = "";
    private string _reportWarningsErrors = "";

    public string ValidationStatus
    {
        get => _validationStatus;
        set => this.RaiseAndSetIfChanged(ref _validationStatus, value);
    }

    public string ReportStatus
    {
        get => _reportStatus;
        set => this.RaiseAndSetIfChanged(ref _reportStatus, value);
    }

    public string ReportMetrics
    {
        get => _reportMetrics;
        set => this.RaiseAndSetIfChanged(ref _reportMetrics, value);
    }

    public string ReportWarningsErrors
    {
        get => _reportWarningsErrors;
        set => this.RaiseAndSetIfChanged(ref _reportWarningsErrors, value);
    }

    public ICommand RunValidationCommand { get; }

    public ValidationPanelViewModel() : this(new DesktopValidationService()) { }

    public ValidationPanelViewModel(DesktopValidationService validationService)
    {
        _validationService = validationService;
        RunValidationCommand = ReactiveCommand.CreateFromTask(OnRunValidationAsync);
    }

    public void SetCaseFolder(string caseFolderPath)
    {
        _caseFolderPath = caseFolderPath;
    }

    private async Task OnRunValidationAsync()
    {
        if (string.IsNullOrEmpty(_caseFolderPath))
        {
            ValidationStatus = "Erro: Nenhum caso ativo carregado no workspace.";
            return;
        }

        ValidationStatus = "Executando validação forense...";
        ReportStatus = "Rodando...";
        ReportMetrics = "";
        ReportWarningsErrors = "";

        try
        {
            var report = await Task.Run(() => _validationService.ValidateExportAsync(
                _caseFolderPath,
                exportFolderPath: null, // validates active case export path
                format: "eml",
                strict: true,
                checkEml: true,
                checkMbox: false,
                checkAtt: true,
                sampleSize: null,
                outDir: _caseFolderPath,
                CancellationToken.None
            ));

            ReportStatus = report.Status;
            
            if (report.Status == "Passed")
            {
                ValidationStatus = "✓ Validação concluída com sucesso! Nenhuma divergência estrutural.";
            }
            else if (report.Status == "PassedWithWarnings")
            {
                ValidationStatus = "⚠ Validação concluída com alertas ou avisos na estrutura.";
            }
            else
            {
                ValidationStatus = "❌ Validação concluída com falhas de integridade.";
            }

            ReportMetrics = $"Mensagens no cofre (SQLite): {report.IndexedMessages}\n" +
                            $"Mensagens exportadas fisicamente: {report.ExportedMessages}\n" +
                            $"Anexos indexados: {report.IndexedAttachments}\n" +
                            $"Anexos exportados fisicamente: {report.ExportedAttachments}";

            var sb = new StringBuilder();
            if (report.Issues.Count > 0)
            {
                bool hasWarnings = false;
                foreach (var issue in report.Issues)
                {
                    if (issue.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!hasWarnings)
                        {
                            sb.AppendLine("=== ALERTAS E AVISOS FORENSES ===");
                            hasWarnings = true;
                        }
                        sb.AppendLine($"[{issue.Severity}] ({issue.Code}): {issue.Message}");
                    }
                }

                bool hasErrors = false;
                foreach (var issue in report.Issues)
                {
                    if (issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!hasErrors)
                        {
                            if (sb.Length > 0) sb.AppendLine();
                            sb.AppendLine("=== ERROS E DIVERGÊNCIAS DE INTEGRIDADE ===");
                            hasErrors = true;
                        }
                        sb.AppendLine($"[{issue.Severity}] ({issue.Code}): {issue.Message}");
                    }
                }
            }

            if (sb.Length == 0)
            {
                sb.AppendLine("Nenhuma discrepância ou problema foi localizado.");
            }

            ReportWarningsErrors = sb.ToString();
        }
        catch (Exception ex)
        {
            ValidationStatus = $"❌ Falha na validação: {ex.Message}";
            ReportStatus = "Failed";
        }
    }
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MailVault.Core;
using MailVault.Desktop.Services;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class ExportPanelViewModel : ViewModelBase
{
    private readonly DesktopExportService _exportService;
    private string _caseFolderPath = "";
    private string _exportFormat = "eml";
    private string _exportPath = "";
    private string _exportStatus = "Selecione o formato e pasta de destino para iniciar a exportação forense.";
    private bool _includeAttachments = true;
    private bool _dryRun;
    
    // Outputs
    private int _messagesSelected;
    private int _messagesExported;
    private long _bytesExported;

    public string ExportFormat
    {
        get => _exportFormat;
        set => this.RaiseAndSetIfChanged(ref _exportFormat, value);
    }

    public string ExportPath
    {
        get => _exportPath;
        set => this.RaiseAndSetIfChanged(ref _exportPath, value);
    }

    public string ExportStatus
    {
        get => _exportStatus;
        set => this.RaiseAndSetIfChanged(ref _exportStatus, value);
    }

    public bool IncludeAttachments
    {
        get => _includeAttachments;
        set => this.RaiseAndSetIfChanged(ref _includeAttachments, value);
    }

    public bool DryRun
    {
        get => _dryRun;
        set => this.RaiseAndSetIfChanged(ref _dryRun, value);
    }

    public int MessagesSelected
    {
        get => _messagesSelected;
        set => this.RaiseAndSetIfChanged(ref _messagesSelected, value);
    }

    public int MessagesExported
    {
        get => _messagesExported;
        set => this.RaiseAndSetIfChanged(ref _messagesExported, value);
    }

    public long BytesExported
    {
        get => _bytesExported;
        set => this.RaiseAndSetIfChanged(ref _bytesExported, value);
    }

    public ICommand RunExportCommand { get; }

    public ExportPanelViewModel() : this(new DesktopExportService()) { }

    public ExportPanelViewModel(DesktopExportService exportService)
    {
        _exportService = exportService;
        var runExportCmd = ReactiveCommand.CreateFromTask(OnRunExportAsync);
        runExportCmd.ThrownExceptions.Subscribe(ex =>
        {
            ExportStatus = $"❌ Falha inesperada no comando de exportação: {ex.Message}";
        });
        RunExportCommand = runExportCmd;
    }

    public void SetCaseFolder(string caseFolderPath)
    {
        _caseFolderPath = caseFolderPath;
        if (string.IsNullOrWhiteSpace(ExportPath))
        {
            ExportPath = Path.Combine(caseFolderPath, "exports");
        }
    }

    private async Task OnRunExportAsync()
    {
        if (string.IsNullOrEmpty(_caseFolderPath))
        {
            ExportStatus = "Erro: Nenhum caso ativo carregado no workspace.";
            return;
        }

        if (string.IsNullOrEmpty(ExportPath))
        {
            ExportStatus = "Erro: Por favor, especifique uma pasta de destino válida.";
            return;
        }

        ExportStatus = DryRun ? "Executando análise prévia (Dry Run)..." : "Iniciando exportação forense...";
        
        try
        {
            var progress = new SimpleProgressReporter();
            var result = await Task.Run(() => _exportService.RunExportAsync(
                _caseFolderPath,
                ExportFormat,
                ExportPath,
                folder: null,
                limit: null,
                offset: null,
                includeAttachments: IncludeAttachments,
                extractAttachments: IncludeAttachments,
                overwrite: true,
                dryRun: DryRun,
                progress,
                CancellationToken.None
            ));

            MessagesSelected = result.MessagesSelected;
            MessagesExported = result.MessagesExported;
            BytesExported = 0;

            if (DryRun)
            {
                ExportStatus = $"[DRY RUN] Análise concluída. E-mails selecionados: {result.MessagesSelected}. Nenhum arquivo físico foi gravado.";
            }
            else
            {
                ExportStatus = $"✓ Exportação concluída com sucesso! E-mails gravados: {result.MessagesExported}/{result.MessagesSelected} em {ExportPath}.";
            }
        }
        catch (Exception ex)
        {
            ExportStatus = $"❌ Falha na exportação: {ex.Message}";
        }
    }

    private sealed class SimpleProgressReporter : IProgressReporter
    {
        public void ReportProgress(double percentage, string status) { }
    }
}

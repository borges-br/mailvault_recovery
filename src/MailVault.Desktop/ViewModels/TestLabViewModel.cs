using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MailVault.Desktop.Services;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public sealed class TestLabViewModel : ViewModelBase
{
    private readonly DesktopTestLabService _testLabService;
    private readonly LocalSettingsService _settingsService;
    private CancellationTokenSource? _pipelineCts;

    private string _corpusFolderPath = "";
    private bool _isCorpusValid;
    private ObservableCollection<CorpusFileRecord> _availableFiles = new();
    private CorpusFileRecord? _selectedFile;

    private bool _isPipelineRunning;
    private double _pipelinePercentage;
    private string _pipelineStatusText = "Pronto para iniciar.";
    private string _pipelineLogsText = "";
    private PipelineSummary? _pipelineSummaryResult;

    public string CorpusFolderPath
    {
        get => _corpusFolderPath;
        set
        {
            this.RaiseAndSetIfChanged(ref _corpusFolderPath, value);
            IsCorpusValid = _testLabService.VerifyCorpusStructure(value);
        }
    }

    public bool IsCorpusValid
    {
        get => _isCorpusValid;
        private set => this.RaiseAndSetIfChanged(ref _isCorpusValid, value);
    }

    public ObservableCollection<CorpusFileRecord> AvailableFiles
    {
        get => _availableFiles;
        private set => this.RaiseAndSetIfChanged(ref _availableFiles, value);
    }

    public CorpusFileRecord? SelectedFile
    {
        get => _selectedFile;
        set => this.RaiseAndSetIfChanged(ref _selectedFile, value);
    }

    public bool IsPipelineRunning
    {
        get => _isPipelineRunning;
        private set => this.RaiseAndSetIfChanged(ref _isPipelineRunning, value);
    }

    public double PipelinePercentage
    {
        get => _pipelinePercentage;
        private set => this.RaiseAndSetIfChanged(ref _pipelinePercentage, value);
    }

    public string PipelineStatusText
    {
        get => _pipelineStatusText;
        private set => this.RaiseAndSetIfChanged(ref _pipelineStatusText, value);
    }

    public string PipelineLogsText
    {
        get => _pipelineLogsText;
        private set => this.RaiseAndSetIfChanged(ref _pipelineLogsText, value);
    }

    public PipelineSummary? PipelineSummaryResult
    {
        get => _pipelineSummaryResult;
        private set => this.RaiseAndSetIfChanged(ref _pipelineSummaryResult, value);
    }

    public ICommand SetupStructureCommand { get; }
    public ICommand ScanCorpusCommand { get; }
    public ICommand RunPipelineCommand { get; }
    public ICommand CancelPipelineCommand { get; }

    public event Action<string>? CaseCreated;

    public TestLabViewModel() 
        : this(new DesktopTestLabService(new DesktopCaseCreationService(), new DesktopExportService(), new DesktopValidationService()), new LocalSettingsService()) { }

    public TestLabViewModel(DesktopTestLabService testLabService, LocalSettingsService settingsService)
    {
        _testLabService = testLabService;
        _settingsService = settingsService;

        // Default from local settings
        var settings = _settingsService.Load();
        CorpusFolderPath = settings.DefaultCorpusPath;

        SetupStructureCommand = ReactiveCommand.Create(SetupDefaultStructure);
        ScanCorpusCommand = ReactiveCommand.CreateFromTask(ScanCorpusAsync);
        RunPipelineCommand = ReactiveCommand.CreateFromTask(RunPipelineAsync);
        CancelPipelineCommand = ReactiveCommand.Create(CancelPipeline);
    }

    private void SetupDefaultStructure()
    {
        if (string.IsNullOrWhiteSpace(CorpusFolderPath))
        {
            CorpusFolderPath = Path.Combine(Directory.GetCurrentDirectory(), ".local-corpus");
        }

        _testLabService.CreateDefaultStructure(CorpusFolderPath);
        IsCorpusValid = true;
        AppendLog($"[ESTRUTURA] Estrutura padrão de laboratório criada em: {CorpusFolderPath}");
        ScanCorpusCommand.Execute(null);
    }

    private async Task ScanCorpusAsync()
    {
        if (!IsCorpusValid)
        {
            AppendLog("[AVISO] A pasta especificada não possui a estrutura do Test Lab. Clique em 'Criar estrutura padrão' primeiro.");
            return;
        }

        AppendLog("[CORPUS] Escaneando evidências...");
        try
        {
            var files = await _testLabService.ScanCorpusAsync(CorpusFolderPath, CancellationToken.None);
            AvailableFiles.Clear();
            foreach (var file in files)
            {
                AvailableFiles.Add(file);
            }
            AppendLog($"[CORPUS] Varredura completa. Localizados {AvailableFiles.Count} arquivos de teste.");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERRO] Falha ao escanear corpus: {ex.Message}");
        }
    }

    private async Task RunPipelineAsync()
    {
        if (SelectedFile == null)
        {
            AppendLog("[ERRO] Selecione um arquivo de corpus da lista para executar.");
            return;
        }

        _pipelineCts = new CancellationTokenSource();
        IsPipelineRunning = true;
        PipelinePercentage = 0;
        PipelineLogsText = "";
        PipelineSummaryResult = null;

        AppendLog($"[PIPELINE] Iniciando processamento de: {SelectedFile.FileName}");

        try
        {
            var summary = await _testLabService.RunPipelineAsync(
                CorpusFolderPath,
                SelectedFile,
                (stepMsg, percentage) =>
                {
                    PipelinePercentage = percentage;
                    PipelineStatusText = stepMsg;
                    AppendLog($"[{percentage:F0}%] {stepMsg}");
                },
                _pipelineCts.Token
            );

            PipelineSummaryResult = summary;
            IsPipelineRunning = false;

            if (summary.Status == "Failed")
            {
                AppendLog($"[FALHA] Pipeline terminou com falha técnica.");
            }
            else
            {
                AppendLog($"[SUCESSO] Pipeline finalizado! Status final: {summary.Status}.");
            }
        }
        catch (OperationCanceledException)
        {
            PipelineStatusText = "Operação cancelada.";
            IsPipelineRunning = false;
            AppendLog("[CANCELADO] Pipeline abortado pelo operador.");
        }
        catch (Exception ex)
        {
            PipelineStatusText = "Falha crítica.";
            IsPipelineRunning = false;
            var report = SafeDiagnosticsFormatter.Format(ex, "Test Lab");
            AppendLog($"[ERRO CRÍTICO] {report.ProbableCause} {report.SanitizedDetails}");
        }
    }

    private void CancelPipeline()
    {
        if (_pipelineCts != null && !_pipelineCts.IsCancellationRequested)
        {
            AppendLog("[CANCELANDO] Solicitando parada do Test Lab...");
            _pipelineCts.Cancel();
        }
    }

    public void ViewActiveTestFolder()
    {
        if (PipelineSummaryResult != null)
        {
            string folder = Path.Combine(CorpusFolderPath, "cases", PipelineSummaryResult.CaseId);
            CaseCreated?.Invoke(folder);
        }
    }

    private void AppendLog(string message)
    {
        PipelineLogsText += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
    }
}

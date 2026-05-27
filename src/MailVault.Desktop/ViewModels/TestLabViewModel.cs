using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MailVault.Desktop.Services;
using MailVault.Indexing;
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

    // Milestone 6.2.3 Diagnostic Fields
    private bool _xstAvailable;
    private string _xstVersion = "";
    private string? _xstWarning;
    private bool _pffAvailable;
    private string _pffVersion = "";
    private string? _pffWarning;
    private bool _readPstAvailable;
    private string _readPstVersion = "";
    private string? _readPstWarning;

    // Milestone 6.2.3.1 CLI Diagnostics Fields
    private bool _cliAvailable;
    private string _cliStatusText = "Não verificado";
    private string _cliLaunchMode = "N/A";
    private string _cliResolvedPath = "N/A";
    private string _cliWorkingDirectory = "N/A";
    private string _cliAppBaseDir = AppContext.BaseDirectory;
    private string _cliCurrentDir = Environment.CurrentDirectory;
    private ObservableCollection<string> _cliProbedPaths = new();
    private string? _cliWarning;
    private string? _cliError;

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

    // Milestone 6.2.3 Diagnostic Getters
    public bool XstAvailable
    {
        get => _xstAvailable;
        private set => this.RaiseAndSetIfChanged(ref _xstAvailable, value);
    }
    public string XstVersion
    {
        get => _xstVersion;
        private set => this.RaiseAndSetIfChanged(ref _xstVersion, value);
    }
    public string? XstWarning
    {
        get => _xstWarning;
        private set => this.RaiseAndSetIfChanged(ref _xstWarning, value);
    }

    public bool PffAvailable
    {
        get => _pffAvailable;
        private set => this.RaiseAndSetIfChanged(ref _pffAvailable, value);
    }
    public string PffVersion
    {
        get => _pffVersion;
        private set => this.RaiseAndSetIfChanged(ref _pffVersion, value);
    }
    public string? PffWarning
    {
        get => _pffWarning;
        private set => this.RaiseAndSetIfChanged(ref _pffWarning, value);
    }

    public bool ReadPstAvailable
    {
        get => _readPstAvailable;
        private set => this.RaiseAndSetIfChanged(ref _readPstAvailable, value);
    }
    public string ReadPstVersion
    {
        get => _readPstVersion;
        private set => this.RaiseAndSetIfChanged(ref _readPstVersion, value);
    }
    public string? ReadPstWarning
    {
        get => _readPstWarning;
        private set => this.RaiseAndSetIfChanged(ref _readPstWarning, value);
    }

    // Milestone 6.2.3.1 CLI Diagnostics Getters
    public bool CliAvailable
    {
        get => _cliAvailable;
        private set => this.RaiseAndSetIfChanged(ref _cliAvailable, value);
    }
    public string CliStatusText
    {
        get => _cliStatusText;
        private set => this.RaiseAndSetIfChanged(ref _cliStatusText, value);
    }
    public string CliLaunchMode
    {
        get => _cliLaunchMode;
        private set => this.RaiseAndSetIfChanged(ref _cliLaunchMode, value);
    }
    public string CliResolvedPath
    {
        get => _cliResolvedPath;
        private set => this.RaiseAndSetIfChanged(ref _cliResolvedPath, value);
    }
    public string CliWorkingDirectory
    {
        get => _cliWorkingDirectory;
        private set => this.RaiseAndSetIfChanged(ref _cliWorkingDirectory, value);
    }
    public string CliAppBaseDir
    {
        get => _cliAppBaseDir;
        private set => this.RaiseAndSetIfChanged(ref _cliAppBaseDir, value);
    }
    public string CliCurrentDir
    {
        get => _cliCurrentDir;
        private set => this.RaiseAndSetIfChanged(ref _cliCurrentDir, value);
    }
    public ObservableCollection<string> ProbedPaths
    {
        get => _cliProbedPaths;
        private set => this.RaiseAndSetIfChanged(ref _cliProbedPaths, value);
    }
    public string? CliWarning
    {
        get => _cliWarning;
        private set => this.RaiseAndSetIfChanged(ref _cliWarning, value);
    }
    public string? CliError
    {
        get => _cliError;
        private set => this.RaiseAndSetIfChanged(ref _cliError, value);
    }

    public ICommand SetupStructureCommand { get; }
    public ICommand ScanCorpusCommand { get; }
    public ICommand RunPipelineCommand { get; }
    public ICommand CancelPipelineCommand { get; }
    public ICommand RefreshEnginesCommand { get; }

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
        RefreshEnginesCommand = ReactiveCommand.CreateFromTask(RefreshEnginesAsync);

        _ = RefreshEnginesAsync();
    }

    public async Task RefreshEnginesAsync()
    {
        // 1. XstReader
        var xstEngine = new XstReaderEngine();
        var xstCap = await xstEngine.CheckCapabilityAsync(CancellationToken.None);
        XstAvailable = xstCap.IsAvailable;
        XstVersion = xstCap.Version ?? "1.0.6";
        XstWarning = xstCap.LicenseWarning;

        // 2. Libpff / pffexport
        var pffCap = await ExternalToolDetector.DetectToolAsync("pffexport", CancellationToken.None);
        PffAvailable = pffCap.IsAvailable;
        PffVersion = pffCap.Version ?? "Desconhecido";
        PffWarning = pffCap.LicenseWarning;

        // 3. readpst
        var readPstCap = await ExternalToolDetector.DetectToolAsync("readpst", CancellationToken.None);
        ReadPstAvailable = readPstCap.IsAvailable;
        ReadPstVersion = readPstCap.Version ?? "Desconhecido";
        ReadPstWarning = readPstCap.LicenseWarning;

        // 4. CLI Worker resolution check
        var resolver = new WorkerExecutableResolver();
        _cliProbedPaths.Clear();
        try
        {
            var info = resolver.Resolve();
            CliAvailable = true;
            CliStatusText = "Disponível";
            CliLaunchMode = info.LaunchMode.ToString();
            CliResolvedPath = info.FileName;
            CliWorkingDirectory = info.WorkingDirectory;
            CliError = null;

            foreach (var path in info.ProbedPaths)
            {
                _cliProbedPaths.Add(path);
            }

            if (info.LaunchMode == WorkerLaunchMode.DotnetRunProject)
            {
                CliWarning = "Aviso: Usando fallback de desenvolvimento (dotnet run). A performance e a portabilidade podem ser afetadas.";
            }
            else
            {
                CliWarning = null;
            }
        }
        catch (WorkerLaunchResolutionException ex)
        {
            CliAvailable = false;
            CliStatusText = "Indisponível";
            CliLaunchMode = "N/A";
            CliResolvedPath = "N/A";
            CliWorkingDirectory = "N/A";
            CliError = ex.Message;
            CliWarning = null;

            foreach (var path in ex.ProbedPaths)
            {
                _cliProbedPaths.Add(path);
            }
        }
        catch (Exception ex)
        {
            CliAvailable = false;
            CliStatusText = "Erro";
            CliLaunchMode = "N/A";
            CliResolvedPath = "N/A";
            CliWorkingDirectory = "N/A";
            CliError = $"Falha desconhecida na resolução: {ex.Message}";
            CliWarning = null;
        }
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

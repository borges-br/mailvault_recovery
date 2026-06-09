using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using MailVault.Desktop.Services;
using MailVault.Domain;
using MailVault.Indexing;
using MailVault.Core;
using MailVault.Audit;
using ReactiveUI;
using System.Text.Json;

namespace MailVault.Desktop.ViewModels;

public sealed class LogLineViewModel
{
    public string Message { get; }
    public LogLineViewModel(string message) => Message = message;
}

public sealed class NewCaseWizardViewModel : ViewModelBase
{
    private readonly DesktopCaseCreationService _caseCreationService;
    private readonly IDesktopFileDialogService _fileDialogService;
    private readonly IndexingJobLedgerService _jobLedger = new();
    private CancellationTokenSource? _indexingCts;
    private WorkerProcessOrchestrator? _orchestrator;
    private string? _currentJobId;
    private DateTimeOffset _jobStartedAt;

    private int _currentStep = 1;
    private string _sourcePath = "";
    private string _maskedSourcePath = "";
    private string _sourceExtension = "";
    private long _sourceSize;
    private string _destinationPath = "";
    private string _caseId = "";
    private bool _disclaimerAccepted;

    private bool _isIndexing;
    private string _progressText = "Pronto para iniciar.";
    private double _progressPercentage;
    private int _foldersIndexed;
    private int _messagesIndexed;
    private int _attachmentsIndexed;
    private int _issuesDetected;
    private string _indexingStatus = "";
    private string? _indexingError;

    // Telemetry fields
    private string _currentOperationText = "Pronto para iniciar.";
    private double _progressPercent;
    private string _progressDetailText = "";
    private string _throughputText = "";
    private string _etaText = "";
    private string _elapsedText = "";
    private bool _canCancel = true;
    private bool _isBusy;
    private string? _lastLogMessage;
    private readonly List<string> _logLinesList = new();
    private DateTime _lastProgressTimestamp;

    // Elapsed timer fields
    private DateTime _indexingStartTime;
    private System.Timers.Timer? _elapsedTimer;

    // Milestone 6.2.3 Fields
    private string _selectedReaderEngine = "XstReader";
    private int _heartbeatAge;
    private bool _showWatchdogWarning;
    private bool _canForceStop;

    // Milestone 6.2.3.1 Fields
    private string? _workerLaunchDiagnostics;
    private int? _workerPid;
    private string _workerEngine = "";
    private string _workerJobKind = "";
    private string _workerPhase = "";
    private string _workerLastEventTime = "";

    // Milestone 6.2.4.2 Fields
    private bool _isNativeToolFailure;
    private string _pffInfoRecognized = "Não";
    private string _pffInfoFileType = "Desconhecido";
    private int _pffExportExitCode;
    private int _pffExportFileCount;
    private string _nativeFailureCause = "Desconhecida";
    private string _nativeFailureRecommendation = "Use XstReader para indexação.";
    private string _deepRecoveryMode = "items";
    private bool _showDeepRecoveryWarning;

    public bool IsNativeToolFailure
    {
        get => _isNativeToolFailure;
        set
        {
            this.RaiseAndSetIfChanged(ref _isNativeToolFailure, value);
            this.RaisePropertyChanged(nameof(ShowStandardFailureButtons));
        }
    }

    public string PffInfoRecognized
    {
        get => _pffInfoRecognized;
        set => this.RaiseAndSetIfChanged(ref _pffInfoRecognized, value);
    }

    public string PffInfoFileType
    {
        get => _pffInfoFileType;
        set => this.RaiseAndSetIfChanged(ref _pffInfoFileType, value);
    }

    public int PffExportExitCode
    {
        get => _pffExportExitCode;
        set => this.RaiseAndSetIfChanged(ref _pffExportExitCode, value);
    }

    public int PffExportFileCount
    {
        get => _pffExportFileCount;
        set => this.RaiseAndSetIfChanged(ref _pffExportFileCount, value);
    }

    public string NativeFailureCause
    {
        get => _nativeFailureCause;
        set => this.RaiseAndSetIfChanged(ref _nativeFailureCause, value);
    }

    public string NativeFailureRecommendation
    {
        get => _nativeFailureRecommendation;
        set => this.RaiseAndSetIfChanged(ref _nativeFailureRecommendation, value);
    }

    public string DeepRecoveryMode
    {
        get => _deepRecoveryMode;
        set => this.RaiseAndSetIfChanged(ref _deepRecoveryMode, value);
    }

    public bool ShowDeepRecoveryWarning
    {
        get => _showDeepRecoveryWarning;
        set => this.RaiseAndSetIfChanged(ref _showDeepRecoveryWarning, value);
    }

    public bool ShowStandardFailureButtons => IndexingError != null && !IsNativeToolFailure;

    public string SelectedReaderEngineDisplayName => SelectedReaderEngine switch
    {
        "XstReader" => "XstReader: Leitor C# Nativo OST/PST",
        "LibpffExternal" => "LibpffExternal: Recuperação Nativa Experimental OST/PST",
        "ThunderbirdMbox" => "Thunderbird/MBOX: Leitor Mozilla Thunderbird e MBOX",
        "Maildir" => "Maildir: Recuperador de Diretórios Maildir",
        "EmlFolder" => "EmlFolder: Indexador de Pastas com .EML",
        _ => SelectedReaderEngine
    };

    public string SelectedReaderEngineDescription => SelectedReaderEngine switch
    {
        "XstReader" => "Parser 100% C# gerenciado para indexação rápida e isolada de arquivos OST/PST de forma extremamente robusta.",
        "LibpffExternal" => "Executa pffinfo/pffexport fora do processo para diagnóstico e recuperação experimental de OST/PST.",
        "ThunderbirdMbox" => "Indexa e recupera perfis estruturados do Mozilla Thunderbird ou arquivos MBOX Unix em streaming de baixa memória.",
        "Maildir" => "Recupera e-mails de diretórios Maildir lendo as subpastas cur/ e new/ e preservando status flags dos nomes dos arquivos.",
        "EmlFolder" => "Indexa e reconstrói de forma recursiva diretórios contendo arquivos no padrão RFC822 (.eml) em disco.",
        _ => "Sem descrição disponível."
    };

    public string? WorkerLaunchDiagnostics
    {
        get => _workerLaunchDiagnostics;
        set => this.RaiseAndSetIfChanged(ref _workerLaunchDiagnostics, value);
    }

    public int? WorkerPid
    {
        get => _workerPid;
        set => this.RaiseAndSetIfChanged(ref _workerPid, value);
    }

    public string WorkerEngine
    {
        get => _workerEngine;
        set => this.RaiseAndSetIfChanged(ref _workerEngine, value);
    }

    public string WorkerJobKind
    {
        get => _workerJobKind;
        set => this.RaiseAndSetIfChanged(ref _workerJobKind, value);
    }

    public string WorkerPhase
    {
        get => _workerPhase;
        set => this.RaiseAndSetIfChanged(ref _workerPhase, value);
    }

    public string WorkerLastEventTime
    {
        get => _workerLastEventTime;
        set => this.RaiseAndSetIfChanged(ref _workerLastEventTime, value);
    }

    public int CurrentStep
    {
        get => _currentStep;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentStep, value);
            this.RaisePropertyChanged(nameof(IsStep1));
            this.RaisePropertyChanged(nameof(IsStep2));
            this.RaisePropertyChanged(nameof(IsStep3));
            this.RaisePropertyChanged(nameof(IsStep4));
            this.RaisePropertyChanged(nameof(IsStep5));
        }
    }

    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool IsStep4 => CurrentStep == 4;
    public bool IsStep5 => CurrentStep == 5;

    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            this.RaiseAndSetIfChanged(ref _sourcePath, value);
            MaskedSourcePath = MaskPath(value);
            if (File.Exists(value))
            {
                var fileInfo = new FileInfo(value);
                SourceExtension = fileInfo.Extension.ToLowerInvariant();
                SourceSize = fileInfo.Length;
                
                // Auto-generate CaseId based on file name + timestamp
                string safeName = Path.GetFileNameWithoutExtension(value).Replace(" ", "_");
                safeName = Regex.Replace(safeName, @"[^a-zA-Z0-9_\-]", "");
                CaseId = $"CASE-{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}";
            }
            else if (Directory.Exists(value))
            {
                SourceExtension = "folder";
                SourceSize = 0;
                
                // Auto-generate CaseId based on folder name + timestamp
                string safeName = Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Replace(" ", "_");
                safeName = Regex.Replace(safeName, @"[^a-zA-Z0-9_\-]", "");
                CaseId = $"CASE-{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}";
            }
            else
            {
                SourceExtension = "";
                SourceSize = 0;
            }
            this.RaisePropertyChanged(nameof(CanProceedStep1));
        }
    }

    public string MaskedSourcePath
    {
        get => _maskedSourcePath;
        private set => this.RaiseAndSetIfChanged(ref _maskedSourcePath, value);
    }

    public string SourceExtension
    {
        get => _sourceExtension;
        private set => this.RaiseAndSetIfChanged(ref _sourceExtension, value);
    }

    public long SourceSize
    {
        get => _sourceSize;
        private set => this.RaiseAndSetIfChanged(ref _sourceSize, value);
    }

    public string DestinationPath
    {
        get => _destinationPath;
        set
        {
            this.RaiseAndSetIfChanged(ref _destinationPath, value);
            this.RaisePropertyChanged(nameof(CanProceedStep2));
        }
    }

    public string CaseId
    {
        get => _caseId;
        set
        {
            // Sanitise CaseID on input
            string sanitized = Regex.Replace(value, @"[^a-zA-Z0-9_\-]", "");
            this.RaiseAndSetIfChanged(ref _caseId, sanitized);
            this.RaisePropertyChanged(nameof(CanProceedStep2));
        }
    }

    public bool DisclaimerAccepted
    {
        get => _disclaimerAccepted;
        set
        {
            this.RaiseAndSetIfChanged(ref _disclaimerAccepted, value);
            this.RaisePropertyChanged(nameof(CanProceedStep3));
        }
    }

    public bool IsIndexing
    {
        get => _isIndexing;
        private set => this.RaiseAndSetIfChanged(ref _isIndexing, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => this.RaiseAndSetIfChanged(ref _progressText, value);
    }

    public double ProgressPercentage
    {
        get => _progressPercentage;
        private set => this.RaiseAndSetIfChanged(ref _progressPercentage, value);
    }

    public int FoldersIndexed
    {
        get => _foldersIndexed;
        private set => this.RaiseAndSetIfChanged(ref _foldersIndexed, value);
    }

    public int MessagesIndexed
    {
        get => _messagesIndexed;
        private set => this.RaiseAndSetIfChanged(ref _messagesIndexed, value);
    }

    public int AttachmentsIndexed
    {
        get => _attachmentsIndexed;
        private set => this.RaiseAndSetIfChanged(ref _attachmentsIndexed, value);
    }

    public int IssuesDetected
    {
        get => _issuesDetected;
        private set => this.RaiseAndSetIfChanged(ref _issuesDetected, value);
    }

    // Circular Log collection as the single source of truth for the UI
    public ObservableCollection<LogLineViewModel> LogLines { get; } = new();

    // Compatibility bridge read-only derived property
    public string LogsText => string.Join("\n", _logLinesList) + "\n";

    public string IndexingStatus
    {
        get => _indexingStatus;
        private set => this.RaiseAndSetIfChanged(ref _indexingStatus, value);
    }

    public string? IndexingError
    {
        get => _indexingError;
        private set => this.RaiseAndSetIfChanged(ref _indexingError, value);
    }

    // Telemetry public Properties
    public string CurrentOperationText
    {
        get => _currentOperationText;
        private set => this.RaiseAndSetIfChanged(ref _currentOperationText, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => this.RaiseAndSetIfChanged(ref _progressPercent, value);
    }

    public string ProgressDetailText
    {
        get => _progressDetailText;
        private set => this.RaiseAndSetIfChanged(ref _progressDetailText, value);
    }

    public string ThroughputText
    {
        get => _throughputText;
        private set => this.RaiseAndSetIfChanged(ref _throughputText, value);
    }

    public string EtaText
    {
        get => _etaText;
        private set => this.RaiseAndSetIfChanged(ref _etaText, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => this.RaiseAndSetIfChanged(ref _elapsedText, value);
    }

    public bool CanCancel
    {
        get => _canCancel;
        private set => this.RaiseAndSetIfChanged(ref _canCancel, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public void ReleaseBusyState()
    {
        IsBusy = false;
        IsIndexing = false;
        CanCancel = true;
    }

    // Milestone 6.2.3 Properties
    public string SelectedReaderEngine
    {
        get => _selectedReaderEngine;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedReaderEngine, value);
            this.RaisePropertyChanged(nameof(SelectedReaderEngineDisplayName));
            this.RaisePropertyChanged(nameof(SelectedReaderEngineDescription));
        }
    }

    public int HeartbeatAge
    {
        get => _heartbeatAge;
        set => this.RaiseAndSetIfChanged(ref _heartbeatAge, value);
    }

    public bool ShowWatchdogWarning
    {
        get => _showWatchdogWarning;
        set => this.RaiseAndSetIfChanged(ref _showWatchdogWarning, value);
    }

    public bool CanForceStop
    {
        get => _canForceStop;
        set => this.RaiseAndSetIfChanged(ref _canForceStop, value);
    }

    public List<string> AvailableReaderEngines { get; } = new() { "XstReader", "ThunderbirdMbox", "Maildir", "EmlFolder", "LibpffExternal" };

    public bool CanProceedStep1
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SourcePath)) return false;
            if (Directory.Exists(SourcePath)) return true;
            if (File.Exists(SourcePath))
            {
                var ext = Path.GetExtension(SourcePath).ToLowerInvariant();
                return ext == ".ost" || ext == ".pst" || ext == ".mbox" || ext == ".eml" || string.IsNullOrEmpty(ext);
            }
            return false;
        }
    }
    public bool CanProceedStep2 => !string.IsNullOrWhiteSpace(CaseId) && !string.IsNullOrWhiteSpace(DestinationPath);
    public bool CanProceedStep3 => DisclaimerAccepted;

    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand CancelIndexingCommand { get; }
    public ICommand StartIndexingCommand { get; }
    public ICommand ForceStopCommand { get; }
    public ICommand SelectSourceFileCommand { get; }
    public ICommand SelectSourceFolderCommand { get; }
    public ICommand SelectDestinationFolderCommand { get; }

    // Milestone 6.2.3.1 Commands
    public ICommand RetryIndexingCommand { get; }
    public ICommand StartNewCaseCommand { get; }
    public ICommand BackToFirstStepCommand { get; }
    public ICommand CopyTechnicalErrorCommand { get; }
    public ICommand OpenCaseFolderCommand { get; }

    // Milestone 6.2.4.2 Commands
    public ICommand OpenNativeReportCommand { get; }
    public ICommand TryXstReaderCommand { get; }
    public ICommand TryDeepRecoveryCommand { get; }
    public ICommand TriggerDeepRecoveryWarningCommand { get; }
    public ICommand ConfirmDeepRecoveryCommand { get; }
    public ICommand CancelDeepRecoveryWarningCommand { get; }

    public event Action<string>? IndexingCompleted;

    public NewCaseWizardViewModel() : this(new DesktopCaseCreationService(), new DesktopFileDialogService()) { }

    public NewCaseWizardViewModel(DesktopCaseCreationService caseCreationService) : this(caseCreationService, new DesktopFileDialogService()) { }

    public NewCaseWizardViewModel(DesktopCaseCreationService caseCreationService, IDesktopFileDialogService fileDialogService)
    {
        _caseCreationService = caseCreationService;
        _fileDialogService = fileDialogService ?? new DesktopFileDialogService();

        DestinationPath = ResolveDefaultDestination();

        var nextCmd = ReactiveCommand.Create(OnNext);
        nextCmd.ThrownExceptions.Subscribe(HandleCommandException);
        NextCommand = nextCmd;

        var backCmd = ReactiveCommand.Create(OnBack);
        backCmd.ThrownExceptions.Subscribe(HandleCommandException);
        BackCommand = backCmd;

        var cancelIndexingCmd = ReactiveCommand.Create(CancelIndexing);
        cancelIndexingCmd.ThrownExceptions.Subscribe(HandleCommandException);
        CancelIndexingCommand = cancelIndexingCmd;

        var startIndexingCmd = ReactiveCommand.CreateFromTask(StartIndexingAsync);
        startIndexingCmd.ThrownExceptions.Subscribe(HandleCommandException);
        StartIndexingCommand = startIndexingCmd;

        var forceStopCmd = ReactiveCommand.CreateFromTask(ForceStopAsync);
        forceStopCmd.ThrownExceptions.Subscribe(HandleCommandException);
        ForceStopCommand = forceStopCmd;

        var selectSourceFileCmd = ReactiveCommand.CreateFromTask(OnSelectSourceFileAsync);
        selectSourceFileCmd.ThrownExceptions.Subscribe(HandleCommandException);
        SelectSourceFileCommand = selectSourceFileCmd;

        var selectSourceFolderCmd = ReactiveCommand.CreateFromTask(OnSelectSourceFolderAsync);
        selectSourceFolderCmd.ThrownExceptions.Subscribe(HandleCommandException);
        SelectSourceFolderCommand = selectSourceFolderCmd;

        var selectDestinationFolderCmd = ReactiveCommand.CreateFromTask(OnSelectDestinationFolderAsync);
        selectDestinationFolderCmd.ThrownExceptions.Subscribe(HandleCommandException);
        SelectDestinationFolderCommand = selectDestinationFolderCmd;

        // Milestone 6.2.3.1 Commands
        var retryIndexingCmd = ReactiveCommand.CreateFromTask(RetryIndexingAsync);
        retryIndexingCmd.ThrownExceptions.Subscribe(HandleCommandException);
        RetryIndexingCommand = retryIndexingCmd;

        var startNewCaseCmd = ReactiveCommand.Create(StartNewCase);
        startNewCaseCmd.ThrownExceptions.Subscribe(HandleCommandException);
        StartNewCaseCommand = startNewCaseCmd;

        var backToFirstStepCmd = ReactiveCommand.Create(BackToFirstStep);
        backToFirstStepCmd.ThrownExceptions.Subscribe(HandleCommandException);
        BackToFirstStepCommand = backToFirstStepCmd;

        var copyTechnicalErrorCmd = ReactiveCommand.Create(CopyTechnicalError);
        copyTechnicalErrorCmd.ThrownExceptions.Subscribe(HandleCommandException);
        CopyTechnicalErrorCommand = copyTechnicalErrorCmd;

        var openCaseFolderCmd = ReactiveCommand.Create(OpenCaseFolder);
        openCaseFolderCmd.ThrownExceptions.Subscribe(HandleCommandException);
        OpenCaseFolderCommand = openCaseFolderCmd;

        // Milestone 6.2.4.2 Commands initialization
        var openNativeReportCmd = ReactiveCommand.Create(OpenNativeReport);
        openNativeReportCmd.ThrownExceptions.Subscribe(HandleCommandException);
        OpenNativeReportCommand = openNativeReportCmd;

        var tryXstReaderCmd = ReactiveCommand.CreateFromTask(TryXstReaderAsync);
        tryXstReaderCmd.ThrownExceptions.Subscribe(HandleCommandException);
        TryXstReaderCommand = tryXstReaderCmd;

        var tryDeepRecoveryCmd = ReactiveCommand.CreateFromTask(TryDeepRecoveryAsync);
        tryDeepRecoveryCmd.ThrownExceptions.Subscribe(HandleCommandException);
        TryDeepRecoveryCommand = tryDeepRecoveryCmd;

        var triggerDeepRecoveryWarningCmd = ReactiveCommand.Create(() => { ShowDeepRecoveryWarning = true; });
        triggerDeepRecoveryWarningCmd.ThrownExceptions.Subscribe(HandleCommandException);
        TriggerDeepRecoveryWarningCommand = triggerDeepRecoveryWarningCmd;

        var confirmDeepRecoveryCmd = ReactiveCommand.CreateFromTask(ConfirmDeepRecoveryAsync);
        confirmDeepRecoveryCmd.ThrownExceptions.Subscribe(HandleCommandException);
        ConfirmDeepRecoveryCommand = confirmDeepRecoveryCmd;

        var cancelDeepRecoveryWarningCmd = ReactiveCommand.Create(() => { ShowDeepRecoveryWarning = false; });
        cancelDeepRecoveryWarningCmd.ThrownExceptions.Subscribe(HandleCommandException);
        CancelDeepRecoveryWarningCommand = cancelDeepRecoveryWarningCmd;
    }

    private void HandleCommandException(Exception ex)
    {
        IsBusy = false;
        IsIndexing = false;
        CanCancel = true;
        
        var report = SafeDiagnosticsFormatter.Format(ex, "Comando do Assistente");
        IndexingError = $"{report.ProbableCause} {report.SanitizedDetails}";
        AppendLog($"[ERRO DE COMANDO] {ex.Message}");
    }

    private async Task OnSelectSourceFileAsync()
    {
        string? path = await _fileDialogService.OpenEvidenceFileAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            SourcePath = path;
        }
    }

    private async Task OnSelectSourceFolderAsync()
    {
        string? path = await _fileDialogService.OpenFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            SourcePath = path;
        }
    }

    private async Task OnSelectDestinationFolderAsync()
    {
        string? path = await _fileDialogService.OpenFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            DestinationPath = path;
        }
    }

    private void OnNext()
    {
        if (CurrentStep == 1 && CanProceedStep1)
        {
            if (Directory.Exists(SourcePath))
            {
                if (SelectedReaderEngine == "XstReader" || SelectedReaderEngine == "LibpffExternal")
                {
                    try
                    {
                        // Check if it looks like Thunderbird profiles or has Thunderbird-like profiles config
                        if (File.Exists(Path.Combine(SourcePath, "profiles.ini")) || 
                            Directory.Exists(Path.Combine(SourcePath, "Profiles")))
                        {
                            SelectedReaderEngine = "ThunderbirdMbox";
                        }
                        // Check for cur/new structure which represents Maildir
                        else if (Directory.GetDirectories(SourcePath, "cur", SearchOption.AllDirectories).Any())
                        {
                            SelectedReaderEngine = "Maildir";
                        }
                        else
                        {
                            SelectedReaderEngine = "EmlFolder";
                        }
                    }
                    catch
                    {
                        SelectedReaderEngine = "EmlFolder";
                    }
                }
            }
            else if (File.Exists(SourcePath))
            {
                var ext = Path.GetExtension(SourcePath).ToLowerInvariant();
                if (ext == ".pst" || ext == ".ost")
                {
                    if (SelectedReaderEngine != "XstReader" && SelectedReaderEngine != "LibpffExternal")
                    {
                        SelectedReaderEngine = "XstReader";
                    }
                }
            }
            CurrentStep = 2;
        }
        else if (CurrentStep == 2 && CanProceedStep2)
            CurrentStep = 3;
        else if (CurrentStep == 3 && CanProceedStep3)
            OnConfirmStep3();
    }

    private void OnConfirmStep3()
    {
        CurrentStep = 4;
        StartIndexingCommand.Execute(null);
    }

    private void OnBack()
    {
        if (CurrentStep == 2)
            CurrentStep = 1;
        else if (CurrentStep == 3)
            CurrentStep = 2;
    }

    private async Task StartIndexingAsync()
    {
        _indexingCts = new CancellationTokenSource();
        IsIndexing = true;
        IsBusy = true;
        CanCancel = true;
        CanForceStop = false;
        ShowWatchdogWarning = false;
        HeartbeatAge = 0;
        ElapsedText = "";

        _logLinesList.Clear();
        LogLines.Clear();
        this.RaisePropertyChanged(nameof(LogsText));

        StartElapsedTimer();

        AppendLog($"Preparando caso {CaseId}...");
        ProgressPercentage = 0;
        ProgressPercent = 0;
        FoldersIndexed = 0;
        MessagesIndexed = 0;
        AttachmentsIndexed = 0;
        IssuesDetected = 0;
        IndexingStatus = "Running";
        IndexingError = null;
        _lastProgressTimestamp = DateTime.Now;

        bool useLegacyInProcess = _caseCreationService.GetType() != typeof(DesktopCaseCreationService);
        if (useLegacyInProcess)
        {
            var reporter = new ProgressReporter(this);
            var pollingCts = new CancellationTokenSource();
            int isPolling = 0;

            var pollingTask = Task.Run(async () =>
            {
                string caseFolder = Path.Combine(DestinationPath, CaseId);
                string dbPath = Path.Combine(caseFolder, "case.db");

                while (IsIndexing && !pollingCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(100, pollingCts.Token);
                        if (!IsIndexing || pollingCts.Token.IsCancellationRequested) break;

                        if (Interlocked.CompareExchange(ref isPolling, 1, 0) != 0)
                        {
                            continue;
                        }

                        try
                        {
                            if (ProgressDetailText.Contains("Calculando hash") || !File.Exists(dbPath))
                            {
                                continue;
                            }

                            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                            await conn.OpenAsync(pollingCts.Token);

                            int folders = 0;
                            int messages = 0;
                            int attachments = 0;
                            int issues = 0;

                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "SELECT COUNT(*) FROM folders";
                                folders = Convert.ToInt32(await cmd.ExecuteScalarAsync(pollingCts.Token));
                            }

                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "SELECT COUNT(*) FROM messages";
                                messages = Convert.ToInt32(await cmd.ExecuteScalarAsync(pollingCts.Token));
                            }

                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "SELECT COUNT(*) FROM attachments";
                                attachments = Convert.ToInt32(await cmd.ExecuteScalarAsync(pollingCts.Token));
                            }

                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "SELECT COUNT(*) FROM issues";
                                issues = Convert.ToInt32(await cmd.ExecuteScalarAsync(pollingCts.Token));
                            }

                            RunOnUIThread(() =>
                            {
                                if (folders > FoldersIndexed) FoldersIndexed = folders;
                                if (messages > MessagesIndexed) MessagesIndexed = messages;
                                if (attachments > AttachmentsIndexed) AttachmentsIndexed = attachments;
                                if (issues > IssuesDetected) IssuesDetected = issues;
                            });
                        }
                        finally
                        {
                            Interlocked.Exchange(ref isPolling, 0);
                        }
                    }
                    catch
                    {
                        // Safe bypass
                    }
                }
            }, pollingCts.Token);

            try
            {
                var result = await Task.Run(() => _caseCreationService.CreateAndIndexCaseAsync(
                    SourcePath,
                    DestinationPath,
                    CaseId,
                    cachePreview: true,
                    limit: null,
                    reporter,
                    _indexingCts.Token
                ), _indexingCts.Token);

                reporter.Flush();

                IndexingStatus = result.Status;
                IsIndexing = false;
                IsBusy = false;
                CurrentStep = 5;

                if (result.Status == "Failed")
                {
                    IndexingError = result.ErrorMessage ?? "Indexação falhou sem mensagem explícita.";
                    AppendLog($"[ERRO] Indexação falhou: {IndexingError}");
                }
                else
                {
                    AppendLog($"[SUCESSO] Indexação finalizada com status: {result.Status}!");
                }
            }
            catch (OperationCanceledException)
            {
                reporter.Flush();
                IndexingStatus = "Cancelled";
                IsIndexing = false;
                IsBusy = false;
                CurrentStep = 5;
                IndexingError = "Operação cancelada pelo operador.";
                AppendLog("[CANCELADO] Indexação cancelada.");
            }
            catch (Exception ex)
            {
                reporter.Flush();
                IndexingStatus = "Failed";
                IsIndexing = false;
                IsBusy = false;
                CurrentStep = 5;
                var report = SafeDiagnosticsFormatter.Format(ex, "Indexador");
                IndexingError = $"{report.ProbableCause} {report.SanitizedDetails}";
                AppendLog($"[ERRO CRÍTICO] {IndexingError}");
            }
            finally
            {
                StopElapsedTimer();
                pollingCts.Cancel();
                pollingCts.Dispose();
                reporter.Dispose();
            }
            return;
        }

        string caseFolderPath = Path.Combine(DestinationPath, CaseId);
        Directory.CreateDirectory(caseFolderPath);

        // Ledger durável: registra o job como "Running" para que, se o app fechar/
        // travar durante a indexação, a próxima abertura saiba que havia um job em curso.
        _currentJobId = Guid.NewGuid().ToString("N");
        _jobStartedAt = DateTimeOffset.Now;
        PersistJobRecord("Running", null);

        string auditLogFilePath = Path.Combine(caseFolderPath, "audit.log");
        var auditWriter = new FileAuditTrailWriter(auditLogFilePath);

        CancellationTokenSource? watchdogCts = null;
        Task? watchdogTask = null;

        try
        {
            // Step 1: Calculate SHA-256 (in-process, keeping responsive progress from Milestone 6.2.1)
            AppendLog("Iniciando cálculo de hash SHA-256 (por streaming)...");
            var hashService = new HashService();
            var hashReporter = new HashProgressWrapper(this);
            string sha256 = await hashService.CalculateSha256Async(SourcePath, hashReporter, _indexingCts.Token);
            AppendLog($"SHA-256 da evidência original calculado: {sha256}");
            
            await auditWriter.WriteEventAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString(),
                Timestamp: DateTimeOffset.Now,
                Action: "HashCalculated",
                OperatorName: Environment.UserName,
                Details: $"Hash da evidência original calculado: {sha256}."
            ), _indexingCts.Token);

            // Step 2: Initialize Worker process orchestration
            AppendLog($"Iniciando worker de indexação isolado utilizando motor: {SelectedReaderEngine}...");
            
            var resolver = new WorkerExecutableResolver();
            try
            {
                var launchInfo = resolver.Resolve();
                WorkerLaunchDiagnostics = $"Resolved CLI Path: {launchInfo.FileName}\nLaunch Mode: {launchInfo.LaunchMode}\nWorking Directory: {launchInfo.WorkingDirectory}\nDiagnostics: {launchInfo.DiagnosticDescription}";
            }
            catch (WorkerLaunchResolutionException ex)
            {
                WorkerLaunchDiagnostics = $"{ex.Message}\nProbed Paths:\n" + string.Join("\n", ex.ProbedPaths) + $"\nApp Base Directory: {ex.AppBaseDirectory}\nCurrent Directory: {ex.CurrentDirectory}\n{ex.RemediationDetails}";
            }
            catch (Exception ex)
            {
                WorkerLaunchDiagnostics = $"Falha geral ao resolver executável do worker CLI: {ex.Message}";
            }

            _orchestrator = new WorkerProcessOrchestrator();

            // Run watchdog loop in background
            watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(_indexingCts.Token);
            watchdogTask = Task.Run(async () =>
            {
                while (IsIndexing && !watchdogCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000, watchdogCts.Token);
                        if (!IsIndexing || watchdogCts.Token.IsCancellationRequested) break;

                        double elapsed = (DateTime.Now - _lastProgressTimestamp).TotalSeconds;
                        RunOnUIThread(() =>
                        {
                            HeartbeatAge = (int)elapsed;
                            if (elapsed >= 15.0)
                            {
                                ShowWatchdogWarning = true;
                            }
                            else
                            {
                                ShowWatchdogWarning = false;
                            }
                        });
                    }
                    catch
                    {
                        // Ignore cancellation/delay exceptions
                    }
                }
            }, watchdogCts.Token);

            var jobConfig = new WorkerJobConfig(
                EvidencePath: SourcePath,
                CasePath: caseFolderPath,
                CaseId: CaseId,
                OperatorId: Environment.UserName,
                EvidenceSha256: sha256,
                EvidenceSize: SourceSize,
                SelectedReaderEngine: SelectedReaderEngine,
                NoBodyLogging: true
            )
            {
                DeepRecoveryMode = DeepRecoveryMode
            };

            DateTime lastUiUpdateTime = DateTime.MinValue;

            var orchestrationResult = await _orchestrator.RunJobAsync(
                jobConfig,
                p =>
                {
                    _lastProgressTimestamp = DateTime.Now;
                    
                    DateTime now = DateTime.Now;
                    bool isTerminal = p.Type == "completed" || p.Type == "failed" || p.Type == "cancelled";
                    if (isTerminal || (now - lastUiUpdateTime).TotalMilliseconds >= 150.0)
                    {
                        lastUiUpdateTime = now;
                        RunOnUIThread(() =>
                        {
                            WorkerPid = _orchestrator?.ActiveWorkerPid;
                            WorkerEngine = p.Engine ?? "";
                            WorkerJobKind = "IndexMetadata";
                            WorkerPhase = p.Phase ?? "";
                            WorkerLastEventTime = p.TimestampUtc ?? "";

                            if (p.FoldersProcessed > FoldersIndexed) FoldersIndexed = p.FoldersProcessed;
                            if (p.MessagesProcessed > MessagesIndexed) MessagesIndexed = p.MessagesProcessed;
                            if (p.AttachmentsProcessed > AttachmentsIndexed) AttachmentsIndexed = p.AttachmentsProcessed;
                            if (p.IssuesCount > IssuesDetected) IssuesDetected = p.IssuesCount;

                            double pct = 25.0 + (p.FoldersProcessed / 500.0) * 5.0 + 35.0;
                            if (pct > 95.0) pct = 95.0;
                            UpdateProgressFromStep(p.Message, pct);
                            AppendLog($"[Worker] {p.Phase}: {p.Message} (Pastas: {p.FoldersProcessed}, E-mails: {p.MessagesProcessed}, Avisos: {p.IssuesCount})");
                        });
                    }
                },
                _indexingCts.Token
            );

            // Cancel watchdog loop
            watchdogCts.Cancel();
            try { await watchdogTask; } catch { }

            AppendLog($"Worker finalizado com status: {orchestrationResult.Status}");

            // Step 3: Finalization/Repair
            if (orchestrationResult.Status == "Success" || orchestrationResult.Status == "NativeExportSuccess" || orchestrationResult.Status == "NativeExportSuccessWithWarnings")
            {
                // Successful completion, let's write audit log and manifest
                var warnings = new List<ExtractionIssue>();
                if (orchestrationResult.IssuesDetected > 0)
                {
                    warnings.Add(new ExtractionIssue(
                        Code: "MV-WARN-WORKER-ISSUES",
                        Severity: "Warning",
                        Message: $"Indexação concluída com {orchestrationResult.IssuesDetected} avisos técnicos detectados.",
                        ObjectId: CaseId,
                        TechnicalDetails: $"Avisos acumulados no case.db durante o processamento do worker."
                    ));
                }

                string actionText = SelectedReaderEngine == "LibpffExternal"
                    ? $"Executed out-of-process native recovery and structural diagnostics (NativeRecoveryDiagnostic: LibpffExternal)"
                    : $"Executed out-of-process indexer ({SelectedReaderEngine})";

                var manifest = new RecoveryManifest(
                    CaseId: CaseId,
                    SourceFile: SourcePath,
                    SourceSizeBytes: SourceSize,
                    SourceSha256: sha256,
                    OperatorName: Environment.UserName,
                    StartedAt: DateTimeOffset.Now,
                    CompletedAt: DateTimeOffset.Now,
                    ToolVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0",
                    Actions: new List<string> { actionText },
                    Warnings: warnings
                );

                await ManifestService.SaveManifestAsync(DestinationPath, manifest, _indexingCts.Token);

                string auditAction = SelectedReaderEngine == "LibpffExternal" ? "NativeRecoveryDiagnosticCompleted" : "CaseClosedByUI";
                string auditDetails = SelectedReaderEngine == "LibpffExternal"
                    ? "Recuperação nativa realizada via pffinfo/pffexport. Relatório native-fallback-report.json gerado."
                    : $"Indexação concluída com sucesso via worker process. Pastas: {orchestrationResult.FoldersIndexed}, E-mails: {orchestrationResult.MessagesIndexed}.";

                await auditWriter.WriteEventAsync(new AuditEvent(
                    EventId: Guid.NewGuid().ToString(),
                    Timestamp: DateTimeOffset.Now,
                    Action: auditAction,
                    OperatorName: Environment.UserName,
                    Details: auditDetails
                ), _indexingCts.Token);

                IndexingStatus = orchestrationResult.Status;
                ProgressPercentage = 100;
                ProgressPercent = 100;
                ProgressDetailText = "Processamento concluído com sucesso!";
                IsNativeToolFailure = false;
            }
            else
            {
                // Incomplete or failed worker, trigger repair
                await _orchestrator.IdempotentRepairCaseDbAsync(
                    caseFolderPath,
                    CaseId,
                    orchestrationResult.Status,
                    orchestrationResult.ErrorMessage
                );

                IndexingStatus = orchestrationResult.Status;
                string status = orchestrationResult.Status;

                if (status == "NativeToolFailed" || status == "NativeToolUnsupportedStructure" || status == "NativeDiagnosticFailed" || status == "NativeToolTimeout" || status == "PartialNativeRecovery")
                {
                    IsNativeToolFailure = true;

                    // Try to load native-fallback-report.json from the case folder
                    string reportPath = Path.Combine(caseFolderPath, "native-fallback-report.json");
                    if (File.Exists(reportPath))
                    {
                        try
                        {
                            string json = await File.ReadAllTextAsync(reportPath);
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;

                            PffInfoRecognized = root.GetProperty("pffinfo").GetProperty("exitCode").GetInt32() == 0 ? "Sim" : "Não";

                            string stdout = root.GetProperty("pffinfo").GetProperty("stdoutSummary").GetString() ?? "";
                            if (stdout.Contains("File content type:"))
                            {
                                PffInfoFileType = "OST/PST (libpff)";
                                var match = System.Text.RegularExpressions.Regex.Match(stdout, @"File content type:\s*(.*)");
                                if (match.Success) PffInfoFileType = match.Groups[1].Value.Trim();
                            }
                            else
                            {
                                PffInfoFileType = "Desconhecido";
                            }

                            PffExportExitCode = root.GetProperty("pffexport").GetProperty("exitCode").GetInt32();
                            PffExportFileCount = root.GetProperty("pffexport").GetProperty("outputFileCount").GetInt32();

                            NativeFailureCause = root.GetProperty("finalStatus").GetString() switch
                            {
                                "NativeToolUnsupportedStructure" => "Falha crítica em descritores locais ou estruturas de anexos (página 4k).",
                                "NativeToolTimeout" => "O tempo limite de processamento de ferramentas nativas foi excedido.",
                                "NativeDiagnosticFailed" => "O arquivo de evidência está corrompido ou o cabeçalho não pôde ser lido.",
                                "PartialNativeRecovery" => "Extração nativa incompleta (alguns itens corrompidos).",
                                _ => "Erro geral na extração da ferramenta nativa pffexport."
                            };

                            NativeFailureRecommendation = "Recomendado: Utilize o motor XstReader com indexação apenas de metadados (MetadataOnly) para contornar a corrupção nativa.";
                        }
                        catch
                        {
                            PffInfoRecognized = "Não";
                            PffInfoFileType = "Desconhecido";
                            PffExportExitCode = 1;
                            PffExportFileCount = 0;
                            NativeFailureCause = "Falha em descritores locais/anexos dentro do OST.";
                            NativeFailureRecommendation = "Use XstReader MetadataOnly para indexação. Use recuperação profunda apenas como tentativa experimental.";
                        }
                    }
                    else
                    {
                        PffInfoRecognized = "Não";
                        PffInfoFileType = "Desconhecido";
                        PffExportExitCode = 1;
                        PffExportFileCount = 0;
                        NativeFailureCause = "Falha em descritores locais/anexos dentro do OST.";
                        NativeFailureRecommendation = "Use XstReader MetadataOnly para indexação. Use recuperação profunda apenas como tentativa experimental.";
                    }

                    IndexingError = $"A recuperação nativa falhou ou está incompleta: {NativeFailureCause}";
                }
                else
                {
                    IsNativeToolFailure = false;
                    IndexingError = orchestrationResult.ErrorMessage ?? "Indexação falhou ou foi abortada via worker.";
                }

                AppendLog($"[ERRO] Processamento falhou: {IndexingError}");
            }

            IsIndexing = false;
            IsBusy = false;
            CurrentStep = 5;
        }
        catch (OperationCanceledException)
        {
            if (_orchestrator != null)
            {
                await _orchestrator.IdempotentRepairCaseDbAsync(
                    caseFolderPath,
                    CaseId,
                    "Cancelled",
                    "Operação cancelada pelo operador."
                );
            }

            IndexingStatus = "Cancelled";
            IsIndexing = false;
            IsBusy = false;
            CurrentStep = 5;
            IndexingError = "Operação cancelada pelo operador.";
            AppendLog("[CANCELADO] Indexação cancelada.");
        }
        catch (Exception ex)
        {
            if (_orchestrator != null)
            {
                await _orchestrator.IdempotentRepairCaseDbAsync(
                    caseFolderPath,
                    CaseId,
                    "Failed",
                    ex.Message
                );
            }

            IndexingStatus = "Failed";
            IsIndexing = false;
            IsBusy = false;
            CurrentStep = 5;
            var report = SafeDiagnosticsFormatter.Format(ex, "Indexador");
            IndexingError = $"{report.ProbableCause} {report.SanitizedDetails}";
            AppendLog($"[ERRO CRÍTICO] {IndexingError}");
        }
        finally
        {
            // Estado terminal (sucesso/falha/cancelado/exceção) converge aqui — registra no ledger.
            PersistJobRecord(IndexingStatus, IndexingError);
            StopElapsedTimer();
            if (watchdogCts != null)
            {
                watchdogCts.Cancel();
                watchdogCts.Dispose();
            }
            _orchestrator = null;
        }
    }

    private void PersistJobRecord(string status, string? error)
    {
        if (_currentJobId == null) return;
        try
        {
            _jobLedger.Upsert(new IndexingJobRecord
            {
                JobId = _currentJobId,
                CaseId = CaseId,
                CaseFolderPath = Path.Combine(DestinationPath, CaseId),
                Engine = SelectedReaderEngine,
                Status = status,
                FoldersIndexed = FoldersIndexed,
                MessagesIndexed = MessagesIndexed,
                ProgressPercent = ProgressPercent,
                StartedAt = _jobStartedAt,
                UpdatedAt = DateTimeOffset.Now,
                CompletedAt = string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase) ? null : DateTimeOffset.Now,
                ErrorMessage = error
            });
        }
        catch
        {
            // best-effort — o ledger não deve derrubar a indexação.
        }
    }

    private void CancelIndexing()
    {
        if (_indexingCts != null && !_indexingCts.IsCancellationRequested)
        {
            AppendLog("[CANCELANDO] Solicitando cancelamento amigável...");
            _indexingCts.Cancel();
            CanCancel = false; // Disable button/show cancelling state
            ProgressText = "Cancelamento solicitado, finalizando estado seguro...";
            ProgressDetailText = "Cancelamento solicitado, finalizando estado seguro...";

            // Start a 5-second timer to enable ForceStop if the worker does not exit
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                if (IsIndexing)
                {
                    RunOnUIThread(() =>
                    {
                        CanForceStop = true;
                        ProgressDetailText = "⚠️ Cancelamento amigável demorando muito. Você pode forçar o encerramento do leitor.";
                        AppendLog("[AVISO] O cancelamento cooperativo está demorando. Botão de encerramento forçado ativado.");
                    });
                }
            });
        }
    }

    private async Task ForceStopAsync()
    {
        AppendLog("[WATCHDOG] Encerrando leitor por força...");
        CanForceStop = false;
        
        if (_orchestrator != null)
        {
            await _orchestrator.ForceKillWorkerAsync();
        }
        
        string caseFolder = Path.Combine(DestinationPath, CaseId);
        var repairOrchestrator = _orchestrator ?? new WorkerProcessOrchestrator();
        await repairOrchestrator.IdempotentRepairCaseDbAsync(
            caseFolder,
            CaseId,
            "ReaderKilled",
            "O leitor parou de responder e foi forçadamente encerrado pelo operador."
        );
        
        IsIndexing = false;
        IsBusy = false;
        CurrentStep = 5;
        IndexingStatus = "Failed";
        IndexingError = "O leitor foi encerrado sob força pelo operador.";
        AppendLog("[ERRO] O leitor foi encerrado sob força pelo operador.");
        PersistJobRecord("Failed", IndexingError);
    }

    private void OpenNativeReport()
    {
        string caseFolderPath = Path.Combine(DestinationPath, CaseId);
        string reportPath = Path.Combine(caseFolderPath, "native-fallback-report.json");
        if (File.Exists(reportPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = reportPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                AppendLog($"[ERRO] Não foi possível abrir o relatório nativo: {ex.Message}");
            }
        }
        else
        {
            AppendLog("[ERRO] Arquivo de relatório nativo não encontrado.");
        }
    }

    private async Task TryXstReaderAsync()
    {
        SelectedReaderEngine = "XstReader";
        IsNativeToolFailure = false;
        IndexingError = null;
        CurrentStep = 4;
        await StartIndexingAsync();
    }

    private async Task TryDeepRecoveryAsync()
    {
        DeepRecoveryMode = "recovered";
        SelectedReaderEngine = "LibpffExternal";
        IsNativeToolFailure = false;
        IndexingError = null;
        CurrentStep = 4;
        await StartIndexingAsync();
    }

    private async Task ConfirmDeepRecoveryAsync()
    {
        ShowDeepRecoveryWarning = false;
        DeepRecoveryMode = "all";
        SelectedReaderEngine = "LibpffExternal";
        IsNativeToolFailure = false;
        IndexingError = null;
        CurrentStep = 4;
        await StartIndexingAsync();
    }

    private sealed class HashProgressWrapper : IProgressReporter
    {
        private readonly NewCaseWizardViewModel _vm;
        public HashProgressWrapper(NewCaseWizardViewModel vm) => _vm = vm;
        public void ReportProgress(double percentage, string status)
        {
            double overallPercent = 5 + (percentage * 0.20);
            _vm.RunOnUIThread(() =>
            {
                _vm.UpdateProgressFromStep($"Calculando hash (SHA-256): {status}", overallPercent);
            });
        }
    }

    private async Task RetryIndexingAsync()
    {
        if (_indexingCts != null)
        {
            _indexingCts.Cancel();
            _indexingCts.Dispose();
        }

        IndexingError = null;
        WorkerLaunchDiagnostics = null;
        IndexingStatus = "Running";
        FoldersIndexed = 0;
        MessagesIndexed = 0;
        AttachmentsIndexed = 0;
        IssuesDetected = 0;
        ProgressPercent = 0;
        ProgressPercentage = 0;

        _logLinesList.Clear();
        LogLines.Clear();
        this.RaisePropertyChanged(nameof(LogsText));

        CurrentStep = 4;
        await StartIndexingAsync();
    }

    private void StartNewCase()
    {
        if (_indexingCts != null)
        {
            _indexingCts.Cancel();
            _indexingCts.Dispose();
            _indexingCts = null;
        }
        _orchestrator = null;

        SourcePath = "";
        DestinationPath = ResolveDefaultDestination();
        CaseId = "";
        DisclaimerAccepted = false;

        IndexingError = null;
        WorkerLaunchDiagnostics = null;
        IndexingStatus = "";
        FoldersIndexed = 0;
        MessagesIndexed = 0;
        AttachmentsIndexed = 0;
        IssuesDetected = 0;
        ProgressPercent = 0;
        ProgressPercentage = 0;
        IsIndexing = false;
        IsBusy = false;

        _logLinesList.Clear();
        LogLines.Clear();
        this.RaisePropertyChanged(nameof(LogsText));

        CurrentStep = 1;
    }

    private void BackToFirstStep()
    {
        CurrentStep = 1;
        IndexingError = null;
        WorkerLaunchDiagnostics = null;
        IsBusy = false;
        IsIndexing = false;
    }

    private void CopyTechnicalError()
    {
        string textToCopy = string.Empty;
        if (!string.IsNullOrEmpty(IndexingError))
        {
            textToCopy += $"[Erro de Indexação]\n{IndexingError}\n\n";
        }
        if (!string.IsNullOrEmpty(WorkerLaunchDiagnostics))
        {
            textToCopy += $"[Diagnóstico de Inicialização]\n{WorkerLaunchDiagnostics}\n\n";
        }

        if (string.IsNullOrEmpty(textToCopy))
        {
            textToCopy = "Nenhum erro técnico registrado.";
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow != null)
        {
            desktop.MainWindow.Clipboard?.SetTextAsync(textToCopy);
            AppendLog("[INFO] Detalhes técnicos copiados para a área de transferência.");
        }
    }

    private void OpenCaseFolder()
    {
        string caseFolderPath = Path.Combine(DestinationPath, CaseId);
        if (Directory.Exists(caseFolderPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = caseFolderPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                AppendLog($"[ERRO] Não foi possível abrir a pasta do caso: {ex.Message}");
            }
        }
        else
        {
            AppendLog("[ERRO] Pasta do caso não encontrada ou ainda não foi criada.");
        }
    }

    public void OpenCreatedCase()
    {
        string caseFolder = Path.Combine(DestinationPath, CaseId);
        IndexingCompleted?.Invoke(caseFolder);
    }

    private void RunOnUIThread(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess() ||
            AppDomain.CurrentDomain.FriendlyName.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) ||
            AppDomain.CurrentDomain.FriendlyName.StartsWith("Microsoft.TestPlatform", StringComparison.OrdinalIgnoreCase) ||
            AppDomain.CurrentDomain.FriendlyName.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            action();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
    }

    public void AppendLog(string message)
    {
        if (message == _lastLogMessage)
        {
            return;
        }
        _lastLogMessage = message;

        RunOnUIThread(() =>
        {
            string logLine = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logLinesList.Add(logLine);
            LogLines.Add(new LogLineViewModel(logLine));

            while (_logLinesList.Count > 500)
            {
                _logLinesList.RemoveAt(0);
                LogLines.RemoveAt(0);
            }

            this.RaisePropertyChanged(nameof(LogsText));
        });
    }

    public void UpdateProgressFromStep(string step, double percentage)
    {
        _lastProgressTimestamp = DateTime.Now;
        ProgressPercent = percentage;
        ProgressPercentage = percentage;
        CurrentOperationText = step;
        ProgressText = step;

        if (step.Contains("Calculando hash (SHA-256)"))
        {
            IsBusy = true;
            CanCancel = true;

            var speedMatch = Regex.Match(step, @"Speed:\s*([\d\.]+)\s*MB/s");
            if (speedMatch.Success)
            {
                ThroughputText = $"{speedMatch.Groups[1].Value} MB/s";
            }
            else
            {
                ThroughputText = "";
            }

            var etaMatch = Regex.Match(step, @"ETA:\s*([\d\:]+)");
            if (etaMatch.Success)
            {
                EtaText = etaMatch.Groups[1].Value;
            }
            else
            {
                EtaText = "";
            }

            var detailMatch = Regex.Match(step, @"Calculando hash \(SHA-256\):\s*(.*)");
            if (detailMatch.Success)
            {
                string detail = detailMatch.Groups[1].Value;
                detail = Regex.Replace(detail, @"Speed:\s*[\d\.]+\s*MB/s\.?", "").Trim();
                detail = Regex.Replace(detail, @"ETA:\s*[\d\:]+\.?", "").Trim();
                ProgressDetailText = detail;
            }
            else
            {
                ProgressDetailText = step;
            }
        }
        else
        {
            ProgressDetailText = step;
            ThroughputText = "";
            EtaText = "";
        }
    }

    private void StartElapsedTimer()
    {
        _indexingStartTime = DateTime.Now;
        ElapsedText = "00:00:00";

        try
        {
            _elapsedTimer?.Stop();
            _elapsedTimer?.Dispose();
            _elapsedTimer = new System.Timers.Timer(1000) { AutoReset = true };
            _elapsedTimer.Elapsed += OnElapsedTimerTick;
            _elapsedTimer.Start();
        }
        catch
        {
            // Best-effort — timer is cosmetic only
        }
    }

    private void StopElapsedTimer()
    {
        try
        {
            _elapsedTimer?.Stop();
            _elapsedTimer?.Dispose();
            _elapsedTimer = null;
        }
        catch { }
    }

    private void OnElapsedTimerTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!IsIndexing)
        {
            StopElapsedTimer();
            return;
        }

        var elapsed = DateTime.Now - _indexingStartTime;
        RunOnUIThread(() =>
        {
            ElapsedText = elapsed.ToString(@"hh\:mm\:ss");

            // Show email throughput once indexing has started (SHA-256 phase uses its own MB/s display)
            if (MessagesIndexed > 10 && elapsed.TotalSeconds > 5)
            {
                double msgPerSec = MessagesIndexed / elapsed.TotalSeconds;
                ThroughputText = $"{msgPerSec:F1} emails/s";
            }
        });
    }

    private static string ResolveDefaultDestination()
    {
        try
        {
            var settings = new LocalSettingsService().Load();
            if (!string.IsNullOrWhiteSpace(settings.DefaultCaseFolder))
            {
                return settings.DefaultCaseFolder;
            }
        }
        catch
        {
            // Fallback abaixo se as settings não puderem ser lidas.
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "mailvault-cases");
    }

    private static string MaskPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return Regex.Replace(path, @"(?i)([a-z]:\\users\\)[^\\]+", "$1<USER>");
    }

    private sealed class ProgressReporter : DesktopCaseCreationService.IIndexingProgressReporter, IDisposable
    {
        private readonly NewCaseWizardViewModel _vm;
        private readonly ProgressThrottler<DesktopIndexingProgress> _throttler;

        public ProgressReporter(NewCaseWizardViewModel vm)
        {
            _vm = vm;
            _throttler = new ProgressThrottler<DesktopIndexingProgress>(p =>
            {
                _vm.RunOnUIThread(() =>
                {
                    if (p.FoldersIndexed > _vm.FoldersIndexed) _vm.FoldersIndexed = p.FoldersIndexed;
                    if (p.MessagesIndexed > _vm.MessagesIndexed) _vm.MessagesIndexed = p.MessagesIndexed;
                    if (p.AttachmentsIndexed > _vm.AttachmentsIndexed) _vm.AttachmentsIndexed = p.AttachmentsIndexed;
                    if (p.IssuesDetected > _vm.IssuesDetected) _vm.IssuesDetected = p.IssuesDetected;
                    
                    _vm.UpdateProgressFromStep(p.CurrentStep, p.Percentage);
                    _vm.AppendLog($"{p.CurrentStep} (Mapeado: {p.FoldersIndexed} pastas, {p.MessagesIndexed} emails, {p.IssuesDetected} warnings)");
                });
            }, TimeSpan.FromMilliseconds(250));
        }

        public void Report(DesktopIndexingProgress progress)
        {
            if (AppDomain.CurrentDomain.FriendlyName.Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                _vm.RunOnUIThread(() =>
                {
                    if (progress.FoldersIndexed > _vm.FoldersIndexed) _vm.FoldersIndexed = progress.FoldersIndexed;
                    if (progress.MessagesIndexed > _vm.MessagesIndexed) _vm.MessagesIndexed = progress.MessagesIndexed;
                    if (progress.AttachmentsIndexed > _vm.AttachmentsIndexed) _vm.AttachmentsIndexed = progress.AttachmentsIndexed;
                    if (progress.IssuesDetected > _vm.IssuesDetected) _vm.IssuesDetected = progress.IssuesDetected;
                    
                    _vm.UpdateProgressFromStep(progress.CurrentStep, progress.Percentage);
                    _vm.AppendLog($"{progress.CurrentStep} (Mapeado: {progress.FoldersIndexed} pastas, {progress.MessagesIndexed} emails, {progress.IssuesDetected} warnings)");
                });
            }
            else
            {
                _throttler.Report(progress);
            }
        }

        public void Flush()
        {
            _throttler.Flush();
        }

        public void Dispose()
        {
            _throttler.Dispose();
        }
    }
}

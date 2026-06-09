using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MailVault.Core;
using MailVault.Desktop.Services;
using MailVault.Indexing;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private static readonly TimeSpan CaseLoadTimeout = TimeSpan.FromSeconds(15);

    private string _windowTitle = "MailVault Recovery";
    private bool _isCaseLoaded;
    private string? _caseFolderPath;
    private string? _warningBanner;
    private bool _hasWarningBanner;
    private string _caseStatusText = "Nenhum case";
    private string _statusBarText = "Nenhum case aberto.";

    // Global Unhandled Error Banner
    private bool _hasGlobalError;
    private string? _globalErrorText;
    private string? _globalErrorDetails;

    // Sidebar Workspace Card
    private string _caseId = "";
    private string _healthStatus = "Sem Caso";
    private int _warningsCount;

    // App footer indicators
    private string _sqliteStatus = "Desconectado";
    private string _manifestStatus = "Ausente";
    private string _auditStatus = "Ausente";
    private string _schemaVersion = "N/A";
    private string _lastActionTime = "N/A";

    private ViewModelBase? _currentView;

    // Indicador global de indexação (visível em qualquer página enquanto o wizard indexa)
    private bool _isIndexingRunningIndicator;
    private bool _isIndexingTerminalIndicator;
    private string _indexingIndicatorText = "";
    private bool _indexingTerminalPending;
    private bool _wasWizardIndexing;

    // Modo diagnóstico (AdvancedMode): revela telas avançadas/diagnóstico (Test Lab, Validação, Relatórios).
    private bool _isDiagnosticModeEnabled;

    private readonly HomeViewModel _homeViewModel;
    private readonly CaseOverviewViewModel _caseOverviewViewModel;
    private readonly FolderTreeViewModel _folderTreeViewModel;
    private readonly MessageListViewModel _messageListViewModel;
    private readonly MessagePreviewViewModel _messagePreviewViewModel;
    private readonly SearchViewModel _searchViewModel;
    private readonly ExportPanelViewModel _exportPanelViewModel;
    private readonly ValidationPanelViewModel _validationPanelViewModel;
    private readonly MessageBrowserViewModel _messageBrowserViewModel;

    // Milestone 6.2 ViewModels
    private readonly NewCaseWizardViewModel _newCaseWizardViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly AuditManifestViewModel _auditManifestViewModel;
    private readonly TestLabViewModel _testLabViewModel;

    // Quick Recovery
    private readonly QuickRecoveryViewModel _quickRecoveryViewModel;

    private readonly CaseWorkspaceDiagnosticService _diagnosticService;
    private readonly CaseWorkspaceService _workspaceService;
    private readonly RecentCasesService _recentCasesService;
    private readonly LocalSettingsService _settingsService;
    private readonly IDesktopFileDialogService _fileDialogService;
    private readonly IndexingJobLedgerService _jobLedger = new();

    // Leitor ao vivo da evidência (preview de conteúdo completo sob demanda).
    private DesktopMessagePreviewService? _previewService;

    private SqliteCaseIndexStore? _store;
    private ICaseIndexReader? _reader;
    private CancellationTokenSource? _caseLoadCts;

    public string WindowTitle
    {
        get => _windowTitle;
        set => this.RaiseAndSetIfChanged(ref _windowTitle, value);
    }

    public bool IsCaseLoaded
    {
        get => _isCaseLoaded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isCaseLoaded, value);
            this.RaisePropertyChanged(nameof(SystemStateText));
        }
    }

    public string SystemStateText => IsCaseLoaded ? "Caso aberto" : "Nenhum caso aberto";

    /// <summary>Versão exibida na UI (ex.: "v1.1.0"), centralizada via AppInfo/Directory.Build.props.</summary>
    public string AppVersion => AppInfo.DisplayVersion;

    public bool IsDiagnosticModeEnabled
    {
        get => _isDiagnosticModeEnabled;
        set => this.RaiseAndSetIfChanged(ref _isDiagnosticModeEnabled, value);
    }

    public string? WarningBanner
    {
        get => _warningBanner;
        set
        {
            this.RaiseAndSetIfChanged(ref _warningBanner, value);
            HasWarningBanner = !string.IsNullOrWhiteSpace(value);
        }
    }

    public bool HasWarningBanner
    {
        get => _hasWarningBanner;
        private set => this.RaiseAndSetIfChanged(ref _hasWarningBanner, value);
    }

    public bool HasGlobalError
    {
        get => _hasGlobalError;
        set => this.RaiseAndSetIfChanged(ref _hasGlobalError, value);
    }

    public string? GlobalErrorText
    {
        get => _globalErrorText;
        set => this.RaiseAndSetIfChanged(ref _globalErrorText, value);
    }

    public string? GlobalErrorDetails
    {
        get => _globalErrorDetails;
        set => this.RaiseAndSetIfChanged(ref _globalErrorDetails, value);
    }

    public string CaseStatusText
    {
        get => _caseStatusText;
        set => this.RaiseAndSetIfChanged(ref _caseStatusText, value);
    }

    public string StatusBarText
    {
        get => _statusBarText;
        set => this.RaiseAndSetIfChanged(ref _statusBarText, value);
    }

    // Sidebar Workspace Card
    public string CaseId
    {
        get => _caseId;
        set
        {
            this.RaiseAndSetIfChanged(ref _caseId, value);
            this.RaisePropertyChanged(nameof(Breadcrumb));
        }
    }

    public string HealthStatus
    {
        get => _healthStatus;
        set => this.RaiseAndSetIfChanged(ref _healthStatus, value);
    }

    public int WarningsCount
    {
        get => _warningsCount;
        set => this.RaiseAndSetIfChanged(ref _warningsCount, value);
    }

    // App footer indicators
    public string SqliteStatus
    {
        get => _sqliteStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _sqliteStatus, value);
            this.RaisePropertyChanged(nameof(SqliteOk));
        }
    }

    public string ManifestStatus
    {
        get => _manifestStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _manifestStatus, value);
            this.RaisePropertyChanged(nameof(ManifestOk));
        }
    }

    public string AuditStatus
    {
        get => _auditStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _auditStatus, value);
            this.RaisePropertyChanged(nameof(AuditOk));
        }
    }

    // Reflete o estado real ao lado do texto do rodapé/status bar (verde só quando OK de verdade).
    public bool SqliteOk => !string.IsNullOrEmpty(_sqliteStatus) && _sqliteStatus != "Desconectado";
    public bool ManifestOk => string.Equals(_manifestStatus, "OK", StringComparison.OrdinalIgnoreCase);
    public bool AuditOk => string.Equals(_auditStatus, "OK", StringComparison.OrdinalIgnoreCase);

    public bool IsIndexingRunningIndicator
    {
        get => _isIndexingRunningIndicator;
        private set => this.RaiseAndSetIfChanged(ref _isIndexingRunningIndicator, value);
    }

    public bool IsIndexingTerminalIndicator
    {
        get => _isIndexingTerminalIndicator;
        private set => this.RaiseAndSetIfChanged(ref _isIndexingTerminalIndicator, value);
    }

    public string IndexingIndicatorText
    {
        get => _indexingIndicatorText;
        private set => this.RaiseAndSetIfChanged(ref _indexingIndicatorText, value);
    }

    public string SchemaVersion
    {
        get => _schemaVersion;
        set => this.RaiseAndSetIfChanged(ref _schemaVersion, value);
    }

    public string LastActionTime
    {
        get => _lastActionTime;
        set => this.RaiseAndSetIfChanged(ref _lastActionTime, value);
    }

    public ViewModelBase? CurrentView
    {
        get => _currentView;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentView, value);
            this.RaisePropertyChanged(nameof(Breadcrumb));
        }
    }

    public string Breadcrumb
    {
        get
        {
            if (CurrentView is HomeViewModel) return "Home";
            string currentModule = CurrentView switch
            {
                CaseOverviewViewModel => "Dashboard do Caso",
                MessageBrowserViewModel => "Navegador de E-mails",
                SearchViewModel => "Busca",
                ExportPanelViewModel => "Exportar E-mails",
                ValidationPanelViewModel => "Validação",
                AuditManifestViewModel => "Relatórios do Caso",
                TestLabViewModel => "Test Lab",
                SettingsViewModel => "Configurações",
                NewCaseWizardViewModel => "Novo Caso",
                ViewModels.QuickRecoveryViewModel => "Recuperacao Rapida",
                _ => "Módulo"
            };
            return string.IsNullOrWhiteSpace(CaseId) ? $"Home > {currentModule}" : $"Home > Caso: {CaseId} > {currentModule}";
        }
    }

    public HomeViewModel HomeVm => _homeViewModel;
    public CaseOverviewViewModel OverviewVm => _caseOverviewViewModel;
    public FolderTreeViewModel FolderTreeVm => _folderTreeViewModel;
    public MessageListViewModel MessageListVm => _messageListViewModel;
    public MessagePreviewViewModel MessagePreviewVm => _messagePreviewViewModel;
    public SearchViewModel SearchVm => _searchViewModel;
    public ExportPanelViewModel ExportVm => _exportPanelViewModel;
    public ValidationPanelViewModel ValidationVm => _validationPanelViewModel;
    public MessageBrowserViewModel MessageBrowserVm => _messageBrowserViewModel;

    // Milestone 6.2 ViewModels
    public NewCaseWizardViewModel NewCaseWizardVm => _newCaseWizardViewModel;
    public SettingsViewModel SettingsVm => _settingsViewModel;
    public AuditManifestViewModel AuditManifestVm => _auditManifestViewModel;
    public TestLabViewModel TestLabVm => _testLabViewModel;

    // Quick Recovery
    public QuickRecoveryViewModel QuickRecoveryViewModel => _quickRecoveryViewModel;

    public ICommand CloseCaseCommand { get; }
    public ICommand ShowOverviewCommand { get; }
    public ICommand ShowBrowserCommand { get; }
    public ICommand ShowSearchCommand { get; }
    public ICommand ShowExportCommand { get; }
    public ICommand ShowValidationCommand { get; }

    // Milestone 6.2 commands
    public ICommand ShowNewCaseWizardCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand ShowAuditManifestCommand { get; }
    public ICommand ShowTestLabCommand { get; }
    public ICommand ShowQuickRecoveryCommand { get; }
    public ICommand ShowIndexingProgressCommand { get; }
    public ICommand OpenCaseFromTopbarCommand { get; }
    public ICommand OpenCaseFolderInExplorerCommand { get; }

    // Global Error Commands
    public ICommand CopyGlobalErrorDetailsCommand { get; }
    public ICommand DismissGlobalErrorCommand { get; }

    public MainWindowViewModel()
        : this(new CaseWorkspaceDiagnosticService())
    {
    }

    public MainWindowViewModel(
        CaseWorkspaceDiagnosticService diagnosticService,
        CaseWorkspaceService? workspaceService = null,
        RecentCasesService? recentCasesService = null,
        LocalSettingsService? settingsService = null,
        IDesktopFileDialogService? fileDialogService = null)
    {
        _diagnosticService = diagnosticService;
        _workspaceService = workspaceService ?? new CaseWorkspaceService(_diagnosticService);
        _recentCasesService = recentCasesService ?? new RecentCasesService();
        _settingsService = settingsService ?? new LocalSettingsService();
        _fileDialogService = fileDialogService ?? new DesktopFileDialogService();

        _homeViewModel = new HomeViewModel(_diagnosticService, _fileDialogService);
        _homeViewModel.CaseSelected += async path => await LoadCaseAsync(path);
        _homeViewModel.RecentCaseRemovalRequested += RemoveRecentCase;
        _homeViewModel.InterruptedJobDismissRequested += DismissInterruptedJob;

        _caseOverviewViewModel = new CaseOverviewViewModel();
        _folderTreeViewModel = new FolderTreeViewModel();
        _messageListViewModel = new MessageListViewModel();
        _messagePreviewViewModel = new MessagePreviewViewModel();
        _searchViewModel = new SearchViewModel();
        _exportPanelViewModel = new ExportPanelViewModel();
        _validationPanelViewModel = new ValidationPanelViewModel();
        _messageBrowserViewModel = new MessageBrowserViewModel(_folderTreeViewModel, _messageListViewModel, _messagePreviewViewModel);

        // Milestone 6.2 VM Init
        _newCaseWizardViewModel = new NewCaseWizardViewModel(new DesktopCaseCreationService(), _fileDialogService);
        _newCaseWizardViewModel.IndexingCompleted += async path => await LoadCaseAsync(path);

        // Espelha o estado da indexação para o indicador global, que continua visível
        // mesmo quando o usuário navega para fora do wizard.
        _newCaseWizardViewModel.WhenAnyValue(x => x.IsIndexing).Subscribe(OnWizardIsIndexingChanged);
        _newCaseWizardViewModel.WhenAnyValue(x => x.ProgressPercent).Subscribe(_ => RefreshIndexingIndicator());
        _newCaseWizardViewModel.WhenAnyValue(x => x.MessagesIndexed).Subscribe(_ => RefreshIndexingIndicator());

        _settingsViewModel = new SettingsViewModel();
        IsDiagnosticModeEnabled = _settingsViewModel.AdvancedMode;
        _settingsViewModel.DiagnosticModeChanged += enabled => IsDiagnosticModeEnabled = enabled;
        _auditManifestViewModel = new AuditManifestViewModel();
        
        _testLabViewModel = new TestLabViewModel();
        _testLabViewModel.CaseCreated += async path => await LoadCaseAsync(path);

        _quickRecoveryViewModel = new QuickRecoveryViewModel();

        _folderTreeViewModel.FolderSelected += async fId =>
        {
            if (_reader != null)
            {
                await _messageListViewModel.SetFolderAsync(fId, _reader, CancellationToken.None);
            }
            // Alimenta o ExportPanel com a pasta selecionada → habilita "Exportar pasta selecionada"
            _exportPanelViewModel.SelectedFolderPath = fId.Value;
        };

        _messageListViewModel.MessageSelected += msg =>
        {
            _messagePreviewViewModel.SetMessage(msg);
        };

        _searchViewModel.MessageSelected += msg =>
        {
            _messagePreviewViewModel.SetMessage(msg);
        };

        CloseCaseCommand = ReactiveCommand.Create(CloseCase);
        ShowOverviewCommand = ReactiveCommand.Create(() => CurrentView = _caseOverviewViewModel);
        ShowBrowserCommand = ReactiveCommand.Create(() => CurrentView = _messageBrowserViewModel);
        ShowSearchCommand = ReactiveCommand.Create(() => CurrentView = _searchViewModel);
        ShowExportCommand = ReactiveCommand.Create(() => CurrentView = _exportPanelViewModel);
        ShowValidationCommand = ReactiveCommand.Create(() => CurrentView = _validationPanelViewModel);

        ShowNewCaseWizardCommand = ReactiveCommand.Create(() => 
        {
            _newCaseWizardViewModel.StartNewCaseCommand.Execute(null);
            CurrentView = _newCaseWizardViewModel;
        });
        ShowSettingsCommand = ReactiveCommand.Create(() => CurrentView = _settingsViewModel);
        ShowAuditManifestCommand = ReactiveCommand.Create(() => CurrentView = _auditManifestViewModel);
        ShowTestLabCommand = ReactiveCommand.Create(() => CurrentView = _testLabViewModel);
        ShowQuickRecoveryCommand = ReactiveCommand.Create(() => CurrentView = _quickRecoveryViewModel);
        ShowIndexingProgressCommand = ReactiveCommand.Create(ShowIndexingProgress);

        OpenCaseFromTopbarCommand = ReactiveCommand.CreateFromTask(OnOpenCaseFromTopbarAsync);
        OpenCaseFolderInExplorerCommand = ReactiveCommand.CreateFromTask(OnOpenCaseFolderInExplorerAsync);

        CopyGlobalErrorDetailsCommand = ReactiveCommand.Create(CopyGlobalErrorDetails);
        DismissGlobalErrorCommand = ReactiveCommand.Create(DismissGlobalError);

        RefreshRecentCases();
        CurrentView = _homeViewModel;
    }

    private async Task OnOpenCaseFromTopbarAsync()
    {
        string? path = await _fileDialogService.OpenFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await LoadCaseAsync(path);
        }
    }

    private async Task OnOpenCaseFolderInExplorerAsync()
    {
        if (!string.IsNullOrWhiteSpace(_caseFolderPath))
        {
            await _fileDialogService.OpenFolderInExplorerAsync(_caseFolderPath);
        }
    }

    public async Task LoadCaseAsync(string casePath)
    {
        CancelCurrentCaseLoad();
        _caseLoadCts = new CancellationTokenSource();

        try
        {
            ClearActiveCaseResources();
            _caseFolderPath = casePath;
            StatusBarText = "Abrindo case e lendo case.db...";
            _homeViewModel.StatusText = "Abrindo workspace do case...";

            using var timeoutCts = new CancellationTokenSource(CaseLoadTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_caseLoadCts.Token, timeoutCts.Token);

            var result = await _workspaceService.OpenExistingCaseAsync(casePath, linkedCts.Token);
            if (result == null)
            {
                _homeViewModel.ShowError("Não foi possível abrir o caso.", "Confira se a pasta contém um case.db válido.");
                CurrentView = _homeViewModel;
                return;
            }

            _store = result.Store;
            _reader = _store.CreateReader();

            // Abre o leitor ao vivo em background (não bloqueia a abertura do caso).
            // Enquanto não estiver pronto, o preview mostra os metadados do índice.
            _ = OpenLivePreviewAsync(casePath);

            WarningBanner = result.WarningMessage;

            await _caseOverviewViewModel.LoadFromWorkspaceAsync(result, linkedCts.Token);
            await _folderTreeViewModel.LoadFoldersAsync(_reader, linkedCts.Token);
            _searchViewModel.SetReader(_reader);

            IsCaseLoaded = true;
            CaseStatusText = _caseOverviewViewModel.HealthStatus;
            
            // Sidebar Workspace Card
            CaseId = string.IsNullOrWhiteSpace(_caseOverviewViewModel.CaseId)
                ? Path.GetFileName(casePath)
                : _caseOverviewViewModel.CaseId;
            HealthStatus = _caseOverviewViewModel.HealthStatus;
            WarningsCount = result.Warnings.Count;

            // Footer indicators
            SqliteStatus = "ReadOnly";
            ManifestStatus = result.Diagnostic.ManifestExists ? "OK" : "Missing";
            AuditStatus = File.Exists(Path.Combine(casePath, "audit.log")) ? "OK" : "Missing";
            SchemaVersion = result.Diagnostic.SchemaVersion.ToString();
            LastActionTime = DateTime.Now.ToString("HH:mm:ss");

            _auditManifestViewModel.SetCaseFolder(casePath);
            _exportPanelViewModel.SetCaseFolder(casePath);
            _validationPanelViewModel.SetCaseFolder(casePath);

            StatusBarText = $"Case {CaseId}: {HealthStatus}";
            WindowTitle = $"MailVault Recovery — Caso: {Path.GetFileName(casePath)}";
            CurrentView = _caseOverviewViewModel;
            ClearIndexingIndicator();

            // Abrir o caso resolve qualquer job interrompido apontando para esta pasta.
            _jobLedger.RemoveByCaseFolder(casePath);
            RefreshInterruptedJobs();

            _recentCasesService.AddOrUpdate(new RecentCaseEntry
            {
                CaseId = CaseId,
                CaseFolderPath = casePath,
                OpenMode = result.OpenMode.ToString(),
                LastOpenedAt = DateTimeOffset.UtcNow,
                SchemaVersion = result.Diagnostic.SchemaVersion
            });
            RefreshRecentCases();
        }
        catch (OperationCanceledException)
        {
            ClearActiveCaseResources();
            _homeViewModel.ShowError("A abertura do case excedeu o tempo limite de 15 segundos.", "Tente abrir novamente ou verifique se o case.db está bloqueado.");
            StatusBarText = "Erro ao abrir case: timeout.";
            CurrentView = _homeViewModel;
        }
        catch (Exception ex)
        {
            ClearActiveCaseResources();
            var report = SafeDiagnosticsFormatter.Format(ex, "Abertura de Caso");
            _homeViewModel.ShowError($"Erro: {report.Title}. {report.ProbableCause}", report.RecommendedAction);
            StatusBarText = "Erro ao abrir case.";
            CurrentView = _homeViewModel;
        }
        finally
        {
            CancelCurrentCaseLoad();
        }
    }

    public void CloseCase()
    {
        CancelCurrentCaseLoad();
        ClearActiveCaseResources();
        _caseFolderPath = null;
        WindowTitle = "MailVault Recovery";
        StatusBarText = "Nenhum case aberto.";
        CaseStatusText = "Nenhum case";
        ClearIndexingIndicator();
        RefreshRecentCases();
        CurrentView = _homeViewModel;
    }

    private void ClearActiveCaseResources()
    {
        _reader?.Dispose();
        _reader = null;
        _store?.Dispose();
        _store = null;
        _workspaceService.CloseActiveCase();

        IsCaseLoaded = false;
        WarningBanner = null;

        // Reset workspace card & footers
        CaseId = "";
        HealthStatus = "Sem Caso";
        WarningsCount = 0;
        SqliteStatus = "Desconectado";
        ManifestStatus = "Ausente";
        AuditStatus = "Ausente";
        SchemaVersion = "N/A";
        LastActionTime = "N/A";

        _messagePreviewViewModel.AttachPreviewService(null);
        _messagePreviewViewModel.SetMessage(null);
        var previewToClose = _previewService;
        _previewService = null;
        if (previewToClose != null)
        {
            _ = previewToClose.CloseAsync();
        }
        _messageListViewModel.ResetMessages();
        _folderTreeViewModel.ResetFolders();
        _searchViewModel.ClearReader();
        _auditManifestViewModel.ClearData();
        _exportPanelViewModel.SetCaseFolder("");
        _validationPanelViewModel.SetCaseFolder("");
    }

    private async Task OpenLivePreviewAsync(string casePath)
    {
        // Detacha qualquer leitor anterior enquanto abre o novo.
        _messagePreviewViewModel.AttachPreviewService(null);

        var service = new DesktopMessagePreviewService();
        bool ok;
        try
        {
            string? sourcePath = ReadCaseSourceFile(casePath);
            ok = await service.OpenAsync(sourcePath, CancellationToken.None);
        }
        catch
        {
            ok = false;
        }

        // O usuário pode ter fechado/trocado o caso durante a abertura (que leva ~1-2s).
        if (!ok || _caseFolderPath != casePath)
        {
            await service.CloseAsync();
            service.Dispose();
            return;
        }

        var previous = _previewService;
        _previewService = service;
        _messagePreviewViewModel.AttachPreviewService(service);
        if (previous != null)
        {
            await previous.CloseAsync();
        }
    }

    private static string? ReadCaseSourceFile(string casePath)
    {
        string dbPath = Path.Combine(casePath, "case.db");
        if (!File.Exists(dbPath))
        {
            return null;
        }

        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT source_file FROM case_info LIMIT 1";
            return cmd.ExecuteScalar()?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private void CancelCurrentCaseLoad()
    {
        if (_caseLoadCts != null && !_caseLoadCts.IsCancellationRequested)
        {
            _caseLoadCts.Cancel();
        }

        _caseLoadCts?.Dispose();
        _caseLoadCts = null;
    }

    private void RefreshRecentCases()
    {
        var recent = _recentCasesService.Load();
        _homeViewModel.LoadRecentCases(recent);
    }

    private void RemoveRecentCase(string caseFolderPath)
    {
        _recentCasesService.Remove(caseFolderPath);
        RefreshRecentCases();
        _homeViewModel.StatusText = "Case recente removido.";
    }

    private void OnWizardIsIndexingChanged(bool isIndexing)
    {
        if (isIndexing)
        {
            _indexingTerminalPending = false;
        }
        else if (_wasWizardIndexing)
        {
            // A indexação acabou de terminar. Se o usuário NÃO está no wizard, mantém
            // um aviso clicável para que ele possa voltar e ver o resultado.
            _indexingTerminalPending = !ReferenceEquals(CurrentView, _newCaseWizardViewModel)
                                       && IsTerminalIndexingStatus(_newCaseWizardViewModel.IndexingStatus);
        }

        _wasWizardIndexing = isIndexing;
        RefreshIndexingIndicator();
    }

    private static bool IsTerminalIndexingStatus(string? status) =>
        !string.IsNullOrEmpty(status) && status != "Running";

    private void RefreshIndexingIndicator()
    {
        var w = _newCaseWizardViewModel;
        if (w.IsIndexing)
        {
            int pct = (int)Math.Round(w.ProgressPercent);
            pct = Math.Clamp(pct, 0, 100);
            IndexingIndicatorText = $"Indexando… {w.MessagesIndexed:N0} e-mails • {pct}%";
            IsIndexingTerminalIndicator = false;
            IsIndexingRunningIndicator = true;
        }
        else if (_indexingTerminalPending)
        {
            IndexingIndicatorText = w.IndexingStatus switch
            {
                "Cancelled" => "Indexação cancelada — clique para ver",
                "Success" or "NativeExportSuccess" or "NativeExportSuccessWithWarnings"
                    => "✓ Indexação concluída — clique para abrir",
                _ => "⚠ Indexação falhou — clique para ver",
            };
            IsIndexingRunningIndicator = false;
            IsIndexingTerminalIndicator = true;
        }
        else
        {
            IsIndexingRunningIndicator = false;
            IsIndexingTerminalIndicator = false;
        }
    }

    private void ShowIndexingProgress()
    {
        // Volta ao wizard SEM reiniciar nada (diferente de "Novo Caso").
        _indexingTerminalPending = false;
        CurrentView = _newCaseWizardViewModel;
        RefreshIndexingIndicator();
    }

    private void ClearIndexingIndicator()
    {
        _indexingTerminalPending = false;
        RefreshIndexingIndicator();
    }

    /// <summary>
    /// Chamado uma vez quando a janela abre. Detecta jobs deixados em "Running" por um
    /// fechamento/travamento anterior, liquida o case.db (lendo os contadores reais) e os
    /// expõe na Home para o usuário abrir o que foi recuperado ou descartar.
    /// </summary>
    public async Task RecoverInterruptedJobsAsync()
    {
        List<IndexingJobRecord> jobs;
        try { jobs = _jobLedger.Load(); }
        catch { return; }

        var orchestrator = new WorkerProcessOrchestrator();
        foreach (var job in jobs)
        {
            // Caso a pasta do caso tenha sumido do disco, descarta o registro órfão.
            if (string.IsNullOrWhiteSpace(job.CaseFolderPath) || !Directory.Exists(job.CaseFolderPath))
            {
                _jobLedger.Remove(job.JobId);
                continue;
            }

            if (string.Equals(job.Status, "Running", StringComparison.OrdinalIgnoreCase))
            {
                // O app foi fechado durante a indexação: o worker filho morreu junto.
                // Liquida o case.db a partir dos contadores reais e marca como interrompido.
                try
                {
                    await orchestrator.IdempotentRepairCaseDbAsync(
                        job.CaseFolderPath, job.CaseId, "ReaderKilled",
                        "Aplicativo encerrado durante a indexação.");
                }
                catch { /* best-effort */ }

                _jobLedger.Upsert(job with
                {
                    Status = "Interrupted",
                    CompletedAt = DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now
                });
            }
        }

        RefreshInterruptedJobs();
    }

    private void RefreshInterruptedJobs()
    {
        List<IndexingJobRecord> interrupted;
        try
        {
            interrupted = _jobLedger.Load()
                .Where(j => string.Equals(j.Status, "Interrupted", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(j.CaseFolderPath)
                            && Directory.Exists(j.CaseFolderPath))
                .ToList();
        }
        catch
        {
            interrupted = new List<IndexingJobRecord>();
        }

        RunOnUIThread(() => _homeViewModel.LoadInterruptedJobs(interrupted));
    }

    private void DismissInterruptedJob(string jobId)
    {
        _jobLedger.Remove(jobId);
        RefreshInterruptedJobs();
    }

    private static void RunOnUIThread(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
    }

    public void ShowGlobalError(Exception ex)
    {
        GlobalErrorText = $"Ocorreu um erro crítico inesperado no sistema: {ex.Message}";
        GlobalErrorDetails = ex.ToString();
        HasGlobalError = true;

        // Release current operations from IsBusy locks
        _newCaseWizardViewModel.ReleaseBusyState();
        _newCaseWizardViewModel.AppendLog($"[ERRO GLOBAL CRÍTICO] {ex.Message}");
    }

    private void CopyGlobalErrorDetails()
    {
        if (string.IsNullOrEmpty(GlobalErrorDetails)) return;

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow != null)
        {
            desktop.MainWindow.Clipboard?.SetTextAsync(GlobalErrorDetails);
        }
    }

    private void DismissGlobalError()
    {
        HasGlobalError = false;
        GlobalErrorText = null;
        GlobalErrorDetails = null;
    }
}

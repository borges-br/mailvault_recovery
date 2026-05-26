using System;
using System.IO;
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

    private string _windowTitle = "MailVault Recovery — Visual Inspection Hub";
    private bool _isCaseLoaded;
    private string? _caseFolderPath;
    private string? _warningBanner;
    private bool _hasWarningBanner;
    private string _caseStatusText = "Nenhum case";
    private string _statusBarText = "Nenhum case aberto.";

    private ViewModelBase? _currentView;

    private readonly HomeViewModel _homeViewModel;
    private readonly CaseOverviewViewModel _caseOverviewViewModel;
    private readonly FolderTreeViewModel _folderTreeViewModel;
    private readonly MessageListViewModel _messageListViewModel;
    private readonly MessagePreviewViewModel _messagePreviewViewModel;
    private readonly SearchViewModel _searchViewModel;
    private readonly ExportPanelViewModel _exportPanelViewModel;
    private readonly ValidationPanelViewModel _validationPanelViewModel;
    private readonly MessageBrowserViewModel _messageBrowserViewModel;

    private readonly CaseWorkspaceDiagnosticService _diagnosticService;
    private readonly CaseWorkspaceService _workspaceService;
    private readonly RecentCasesService _recentCasesService;

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
        set => this.RaiseAndSetIfChanged(ref _isCaseLoaded, value);
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

    public ViewModelBase? CurrentView
    {
        get => _currentView;
        set => this.RaiseAndSetIfChanged(ref _currentView, value);
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

    public ICommand CloseCaseCommand { get; }
    public ICommand ShowOverviewCommand { get; }
    public ICommand ShowBrowserCommand { get; }
    public ICommand ShowSearchCommand { get; }
    public ICommand ShowExportCommand { get; }
    public ICommand ShowValidationCommand { get; }

    public MainWindowViewModel()
        : this(new CaseWorkspaceDiagnosticService())
    {
    }

    public MainWindowViewModel(
        CaseWorkspaceDiagnosticService diagnosticService,
        CaseWorkspaceService? workspaceService = null,
        RecentCasesService? recentCasesService = null)
    {
        _diagnosticService = diagnosticService;
        _workspaceService = workspaceService ?? new CaseWorkspaceService(_diagnosticService);
        _recentCasesService = recentCasesService ?? new RecentCasesService();

        _homeViewModel = new HomeViewModel(_diagnosticService);
        _homeViewModel.CaseSelected += async path => await LoadCaseAsync(path);
        _homeViewModel.RecentCaseRemovalRequested += RemoveRecentCase;

        _caseOverviewViewModel = new CaseOverviewViewModel();
        _folderTreeViewModel = new FolderTreeViewModel();
        _messageListViewModel = new MessageListViewModel();
        _messagePreviewViewModel = new MessagePreviewViewModel();
        _searchViewModel = new SearchViewModel();
        _exportPanelViewModel = new ExportPanelViewModel();
        _validationPanelViewModel = new ValidationPanelViewModel();
        _messageBrowserViewModel = new MessageBrowserViewModel(_folderTreeViewModel, _messageListViewModel, _messagePreviewViewModel);

        _folderTreeViewModel.FolderSelected += async fId =>
        {
            if (_reader != null)
            {
                await _messageListViewModel.SetFolderAsync(fId, _reader, CancellationToken.None);
            }
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

        RefreshRecentCases();
        CurrentView = _homeViewModel;
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

            WarningBanner = result.WarningMessage;

            await _caseOverviewViewModel.LoadFromWorkspaceAsync(result, linkedCts.Token);
            await _folderTreeViewModel.LoadFoldersAsync(_reader, linkedCts.Token);
            _searchViewModel.SetReader(_reader);

            IsCaseLoaded = true;
            CaseStatusText = _caseOverviewViewModel.HealthStatus;
            StatusBarText = $"Case {_caseOverviewViewModel.CaseId}: {_caseOverviewViewModel.HealthStatus}";
            WindowTitle = $"MailVault Recovery — Caso: {Path.GetFileName(casePath)}";
            CurrentView = _caseOverviewViewModel;

            _recentCasesService.AddOrUpdate(new RecentCaseEntry
            {
                CaseId = string.IsNullOrWhiteSpace(_caseOverviewViewModel.CaseId)
                    ? Path.GetFileName(casePath)
                    : _caseOverviewViewModel.CaseId,
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
            _homeViewModel.ShowError($"Erro ao carregar caso: {ex.Message}", "Confira o schema do case.db e o audit.log do case.");
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
        WindowTitle = "MailVault Recovery — Visual Inspection Hub";
        StatusBarText = "Nenhum case aberto.";
        CaseStatusText = "Nenhum case";
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

        _messagePreviewViewModel.SetMessage(null);
        _messageListViewModel.ResetMessages();
        _folderTreeViewModel.ResetFolders();
        _searchViewModel.ClearReader();
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
}

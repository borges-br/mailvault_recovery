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
    private string _windowTitle = "MailVault Recovery — Visual Inspection Hub";
    private bool _isCaseLoaded;
    private string? _caseFolderPath;
    private string? _warningBanner;
    private bool _hasWarningBanner;

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
            HasWarningBanner = !string.IsNullOrEmpty(value);
        }
    }

    public bool HasWarningBanner
    {
        get => _hasWarningBanner;
        private set => this.RaiseAndSetIfChanged(ref _hasWarningBanner, value);
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
    {
        _diagnosticService = new CaseWorkspaceDiagnosticService();
        _workspaceService = new CaseWorkspaceService(_diagnosticService);
        _recentCasesService = new RecentCasesService();

        _homeViewModel = new HomeViewModel(_diagnosticService);
        _homeViewModel.CaseSelected += async (path) => await LoadCaseAsync(path);

        _caseOverviewViewModel = new CaseOverviewViewModel();
        _folderTreeViewModel = new FolderTreeViewModel();
        _messageListViewModel = new MessageListViewModel();
        _messagePreviewViewModel = new MessagePreviewViewModel();
        _searchViewModel = new SearchViewModel();
        _exportPanelViewModel = new ExportPanelViewModel();
        _validationPanelViewModel = new ValidationPanelViewModel();
        _messageBrowserViewModel = new MessageBrowserViewModel(_folderTreeViewModel, _messageListViewModel, _messagePreviewViewModel);

        _folderTreeViewModel.FolderSelected += async (fId) =>
        {
            if (_reader != null)
            {
                await _messageListViewModel.SetFolderAsync(fId, _reader, CancellationToken.None);
            }
        };

        _messageListViewModel.MessageSelected += (msg) =>
        {
            _messagePreviewViewModel.SetMessage(msg);
        };

        _searchViewModel.MessageSelected += (msg) =>
        {
            _messagePreviewViewModel.SetMessage(msg);
        };

        CloseCaseCommand = ReactiveCommand.Create(CloseCase);
        ShowOverviewCommand = ReactiveCommand.Create(() => CurrentView = _caseOverviewViewModel);
        ShowBrowserCommand = ReactiveCommand.Create(() => CurrentView = _messageBrowserViewModel);
        ShowSearchCommand = ReactiveCommand.Create(() => CurrentView = _searchViewModel);
        ShowExportCommand = ReactiveCommand.Create(() => CurrentView = _exportPanelViewModel);
        ShowValidationCommand = ReactiveCommand.Create(() => CurrentView = _validationPanelViewModel);

        // Load recent cases history
        var recent = _recentCasesService.Load();
        _homeViewModel.LoadRecentCases(recent);

        CurrentView = _homeViewModel;
    }

    public async Task LoadCaseAsync(string casePath)
    {
        try
        {
            CloseCase();
            _caseFolderPath = casePath;

            var result = await _workspaceService.OpenExistingCaseAsync(casePath, CancellationToken.None);
            if (result == null)
            {
                _homeViewModel.StatusText = "Não foi possível abrir o caso.";
                CurrentView = _homeViewModel;
                return;
            }

            _store = result.Store;
            _reader = _store.CreateReader();

            // Show warning banner if limited mode
            WarningBanner = result.WarningMessage;

            await _caseOverviewViewModel.LoadFromReaderAsync(_reader, CancellationToken.None);
            await _folderTreeViewModel.LoadFoldersAsync(_reader, CancellationToken.None);
            _searchViewModel.SetReader(_reader);

            IsCaseLoaded = true;
            WindowTitle = $"MailVault Recovery — Caso: {Path.GetFileName(casePath)}";
            CurrentView = _caseOverviewViewModel;

            // Get case ID for recent cases record
            string caseId = _caseOverviewViewModel.CaseId;
            if (string.IsNullOrEmpty(caseId))
                caseId = Path.GetFileName(casePath);

            // Record in recent cases (de-identified)
            _recentCasesService.AddOrUpdate(new RecentCaseEntry
            {
                CaseId = caseId,
                CaseFolderPath = casePath,
                OpenMode = result.OpenMode.ToString(),
                LastOpenedAt = DateTimeOffset.UtcNow,
                SchemaVersion = 0
            });
        }
        catch (Exception ex)
        {
            _homeViewModel.StatusText = $"Erro ao carregar caso: {ex.Message}";
            CurrentView = _homeViewModel;
        }
    }

    public void CloseCase()
    {
        _reader?.Dispose();
        _reader = null;
        _store?.Dispose();
        _store = null;
        _workspaceService.CloseActiveCase();

        IsCaseLoaded = false;
        WarningBanner = null;
        _caseFolderPath = null;
        WindowTitle = "MailVault Recovery — Visual Inspection Hub";

        _messagePreviewViewModel.SetMessage(null);
        _messageListViewModel.Messages.Clear();
        _folderTreeViewModel.RootFolders.Clear();

        // Refresh recent cases
        var recent = _recentCasesService.Load();
        _homeViewModel.LoadRecentCases(recent);

        CurrentView = _homeViewModel;
    }
}

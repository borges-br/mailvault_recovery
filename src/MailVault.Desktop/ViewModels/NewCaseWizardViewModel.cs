using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MailVault.Desktop.Services;
using MailVault.Domain;
using MailVault.Indexing;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public sealed class NewCaseWizardViewModel : ViewModelBase
{
    private readonly DesktopCaseCreationService _caseCreationService;
    private CancellationTokenSource? _indexingCts;

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
    private string _logsText = "";
    private string _indexingStatus = "";
    private string? _indexingError;

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

    public string LogsText
    {
        get => _logsText;
        private set => this.RaiseAndSetIfChanged(ref _logsText, value);
    }

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

    public bool CanProceedStep1 => File.Exists(SourcePath) && (SourceExtension == ".ost" || SourceExtension == ".pst");
    public bool CanProceedStep2 => !string.IsNullOrWhiteSpace(CaseId) && !string.IsNullOrWhiteSpace(DestinationPath);
    public bool CanProceedStep3 => DisclaimerAccepted;

    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand CancelIndexingCommand { get; }
    public ICommand StartIndexingCommand { get; }

    public event Action<string>? IndexingCompleted;

    public NewCaseWizardViewModel() : this(new DesktopCaseCreationService()) { }

    public NewCaseWizardViewModel(DesktopCaseCreationService caseCreationService)
    {
        _caseCreationService = caseCreationService;

        string currentDir = Directory.GetCurrentDirectory();
        DestinationPath = Path.Combine(currentDir, "mailvault-cases");

        NextCommand = ReactiveCommand.Create(OnNext);
        BackCommand = ReactiveCommand.Create(OnBack);
        CancelIndexingCommand = ReactiveCommand.Create(CancelIndexing);
        StartIndexingCommand = ReactiveCommand.CreateFromTask(StartIndexingAsync);
    }

    private void OnNext()
    {
        if (CurrentStep == 1 && CanProceedStep1)
            CurrentStep = 2;
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
        LogsText = $"[{DateTime.Now:HH:mm:ss}] Preparando caso {CaseId}...\n";
        ProgressPercentage = 0;
        FoldersIndexed = 0;
        MessagesIndexed = 0;
        AttachmentsIndexed = 0;
        IssuesDetected = 0;
        IndexingStatus = "Running";
        IndexingError = null;

        var reporter = new ProgressReporter(this);

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

            IndexingStatus = result.Status;
            IsIndexing = false;
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
            IndexingStatus = "Cancelled";
            IsIndexing = false;
            CurrentStep = 5;
            IndexingError = "Operação cancelada pelo operador.";
            AppendLog("[CANCELADO] Indexação cancelada.");
        }
        catch (Exception ex)
        {
            IndexingStatus = "Failed";
            IsIndexing = false;
            CurrentStep = 5;
            var report = SafeDiagnosticsFormatter.Format(ex, "Indexador");
            IndexingError = $"{report.ProbableCause} {report.SanitizedDetails}";
            AppendLog($"[ERRO CRÍTICO] {IndexingError}");
        }
    }

    private void CancelIndexing()
    {
        if (_indexingCts != null && !_indexingCts.IsCancellationRequested)
        {
            AppendLog("[CANCELANDO] Solicitando cancelamento amigável...");
            _indexingCts.Cancel();
        }
    }

    public void OpenCreatedCase()
    {
        string caseFolder = Path.Combine(DestinationPath, CaseId);
        IndexingCompleted?.Invoke(caseFolder);
    }

    public void AppendLog(string message)
    {
        LogsText += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
    }

    private static string MaskPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return Regex.Replace(path, @"(?i)([a-z]:\\users\\)[^\\]+", "$1<USER>");
    }

    private sealed class ProgressReporter : DesktopCaseCreationService.IIndexingProgressReporter
    {
        private readonly NewCaseWizardViewModel _vm;

        public ProgressReporter(NewCaseWizardViewModel vm)
        {
            _vm = vm;
        }

        public void Report(DesktopIndexingProgress progress)
        {
            _vm.ProgressPercentage = progress.Percentage;
            _vm.ProgressText = progress.CurrentStep;
            _vm.FoldersIndexed = progress.FoldersIndexed;
            _vm.MessagesIndexed = progress.MessagesIndexed;
            _vm.AttachmentsIndexed = progress.AttachmentsIndexed;
            _vm.IssuesDetected = progress.IssuesDetected;
            _vm.AppendLog($"{progress.CurrentStep} (Mapeado: {progress.FoldersIndexed} pastas, {progress.MessagesIndexed} emails, {progress.IssuesDetected} warnings)");
        }
    }
}

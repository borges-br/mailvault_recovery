using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MailVault.Desktop.Services;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class HomeViewModel : LoadableViewModelBase
{
    private string _statusText = "Escolha um fluxo para iniciar.";
    private string? _selectedCasePath;
    private string? _warningBanner;
    private bool _hasWarningBanner;

    private readonly CaseWorkspaceDiagnosticService _diagnostics;
    private readonly IDesktopFileDialogService _fileDialogService;

    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public string? SelectedCasePath
    {
        get => _selectedCasePath;
        set => this.RaiseAndSetIfChanged(ref _selectedCasePath, value);
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

    public ObservableCollection<RecentCaseEntry> RecentCases { get; } = new();

    // Jobs de indexação que ficaram pendentes (app fechado/travado durante a indexação).
    public ObservableCollection<IndexingJobRecord> InterruptedJobs { get; } = new();

    public ICommand OpenCaseCommand { get; }
    public ICommand OpenRecentCaseCommand { get; }
    public ICommand RemoveRecentCaseCommand { get; }
    public ICommand OpenInterruptedJobCommand { get; }
    public ICommand DismissInterruptedJobCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand SelectCaseFolderCommand { get; }

    public event Action<string>? CaseSelected;
    public event Action<string>? RecentCaseRemovalRequested;
    public event Action<string>? InterruptedJobDismissRequested;

    public HomeViewModel() : this(new CaseWorkspaceDiagnosticService(), new DesktopFileDialogService()) { }

    public HomeViewModel(CaseWorkspaceDiagnosticService diagnostics) : this(diagnostics, new DesktopFileDialogService()) { }

    public HomeViewModel(CaseWorkspaceDiagnosticService diagnostics, IDesktopFileDialogService fileDialogService)
    {
        _diagnostics = diagnostics;
        _fileDialogService = fileDialogService ?? new DesktopFileDialogService();
        OpenCaseCommand = ReactiveCommand.CreateFromTask(OnOpenCaseAsync);
        OpenRecentCaseCommand = ReactiveCommand.CreateFromTask<RecentCaseEntry>(OpenRecentCaseAsync);
        RemoveRecentCaseCommand = ReactiveCommand.Create<RecentCaseEntry>(RemoveRecentCase);
        OpenInterruptedJobCommand = ReactiveCommand.Create<IndexingJobRecord>(OpenInterruptedJob);
        DismissInterruptedJobCommand = ReactiveCommand.Create<IndexingJobRecord>(DismissInterruptedJob);
        RetryCommand = ReactiveCommand.CreateFromTask(OnOpenCaseAsync);
        SelectCaseFolderCommand = ReactiveCommand.CreateFromTask(OnSelectCaseFolderAsync);
    }

    private async Task OnSelectCaseFolderAsync()
    {
        string? path = await _fileDialogService.OpenFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            SelectedCasePath = path;
        }
    }

    private async Task OnOpenCaseAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedCasePath))
        {
            ShowError("Informe uma pasta de caso existente.", "Selecione uma pasta que contenha case.db.");
            return;
        }

        await ExecuteLoadAsync(async (ct) =>
        {
            StatusText = "Validando pasta do caso e schema do case.db...";
            WarningBanner = null;

            var diagnosis = await _diagnostics.DiagnoseAsync(SelectedCasePath, ct);

            if (!diagnosis.DirectoryExists)
            {
                ShowError($"Diretório não encontrado: {SelectedCasePath}", diagnosis.SuggestedAction);
                return;
            }

            if (!diagnosis.CaseDbExists)
            {
                ShowError("case.db não encontrado neste diretório.", diagnosis.SuggestedAction);
                return;
            }

            if (!diagnosis.CaseDbReadable || !diagnosis.SchemaValid)
            {
                string message = diagnosis.ErrorMessage ?? "Não foi possível ler case.db.";
                if (diagnosis.MissingSchemaObjects.Count > 0)
                {
                    message += $" Itens ausentes: {string.Join(", ", diagnosis.MissingSchemaObjects)}";
                }

                ShowError(message, diagnosis.SuggestedAction);
                return;
            }

            if (diagnosis.HasWarnings)
            {
                WarningBanner = string.Join(Environment.NewLine, diagnosis.Warnings);
            }

            State = LoadingState.Loaded;
            StatusText = "case.db validado. Abrindo workspace...";
            CaseSelected?.Invoke(SelectedCasePath);
        }, "Validando case.db...");
    }

    public async Task OpenRecentCaseAsync(RecentCaseEntry entry)
    {
        SelectedCasePath = entry.CaseFolderPath;

        if (!Directory.Exists(entry.CaseFolderPath))
        {
            ShowError(
                $"Case recente não encontrado: {entry.CaseFolderPath}",
                "Remova este item da lista de recentes ou restaure a pasta original.");
            return;
        }

        StatusText = $"Abrindo case recente: {entry.CaseId}";
        await Task.CompletedTask;
        CaseSelected?.Invoke(entry.CaseFolderPath);
    }

    private void RemoveRecentCase(RecentCaseEntry entry)
    {
        RecentCaseRemovalRequested?.Invoke(entry.CaseFolderPath);
    }

    public void LoadRecentCases(System.Collections.Generic.IEnumerable<RecentCaseEntry> entries)
    {
        RecentCases.Clear();
        foreach (var e in entries)
        {
            RecentCases.Add(e);
        }
    }

    public void LoadInterruptedJobs(System.Collections.Generic.IEnumerable<IndexingJobRecord> jobs)
    {
        InterruptedJobs.Clear();
        foreach (var j in jobs)
        {
            InterruptedJobs.Add(j);
        }
    }

    private void OpenInterruptedJob(IndexingJobRecord job)
    {
        if (job == null || string.IsNullOrWhiteSpace(job.CaseFolderPath)) return;
        SelectedCasePath = job.CaseFolderPath;
        StatusText = $"Abrindo o que foi recuperado de {job.CaseId}...";
        CaseSelected?.Invoke(job.CaseFolderPath);
    }

    private void DismissInterruptedJob(IndexingJobRecord job)
    {
        if (job == null) return;
        InterruptedJobs.Remove(job);
        InterruptedJobDismissRequested?.Invoke(job.JobId);
    }

    public void SelectRecentCase(RecentCaseEntry entry)
    {
        SelectedCasePath = entry.CaseFolderPath;
    }

    public void ShowError(string message, string? suggestedAction = null)
    {
        State = LoadingState.Error;
        ErrorMessage = message;
        StatusText = string.IsNullOrWhiteSpace(suggestedAction)
            ? message
            : $"{message}\nAção recomendada: {suggestedAction}";
    }
}

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
    private string _statusText = "Selecione uma pasta de caso existente ou arraste um arquivo .ost/.pst.";
    private string? _selectedCasePath;
    private string? _warningBanner;
    private bool _hasWarningBanner;

    private readonly CaseWorkspaceDiagnosticService _diagnostics;

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
            HasWarningBanner = !string.IsNullOrEmpty(value);
        }
    }

    public bool HasWarningBanner
    {
        get => _hasWarningBanner;
        private set => this.RaiseAndSetIfChanged(ref _hasWarningBanner, value);
    }

    public ObservableCollection<RecentCaseEntry> RecentCases { get; } = new();

    public ICommand OpenCaseCommand { get; }
    public ICommand CreateCaseCommand { get; }
    public ICommand OpenMboxCaseCommand { get; }

    public event Action<string>? CaseSelected;

    public HomeViewModel() : this(new CaseWorkspaceDiagnosticService()) { }

    public HomeViewModel(CaseWorkspaceDiagnosticService diagnostics)
    {
        _diagnostics = diagnostics;
        OpenCaseCommand = ReactiveCommand.CreateFromTask(OnOpenCaseAsync);
        CreateCaseCommand = ReactiveCommand.Create(OnCreateCase);
        OpenMboxCaseCommand = ReactiveCommand.Create(OnOpenMbox);
    }

    private async Task OnOpenCaseAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedCasePath))
        {
            StatusText = "⚠ Por favor, especifique um caminho válido.";
            return;
        }

        await ExecuteLoadAsync(async (ct) =>
        {
            StatusText = "Validando caso...";
            WarningBanner = null;

            var diagnosis = await _diagnostics.DiagnoseAsync(SelectedCasePath, ct);

            if (!diagnosis.DirectoryExists)
            {
                State = LoadingState.Error;
                ErrorMessage = $"Diretório não encontrado: {SelectedCasePath}";
                StatusText = ErrorMessage;
                return;
            }

            if (!diagnosis.CaseDbExists)
            {
                State = LoadingState.Error;
                ErrorMessage = "case.db não encontrado neste diretório.";
                StatusText = ErrorMessage;
                if (diagnosis.SuggestedAction != null)
                    StatusText += $"\n{diagnosis.SuggestedAction}";
                return;
            }

            if (!diagnosis.CaseDbReadable)
            {
                State = LoadingState.Error;
                ErrorMessage = diagnosis.ErrorMessage ?? "Não foi possível ler case.db.";
                StatusText = ErrorMessage;
                return;
            }

            // Warnings (journal, missing manifest)
            if (diagnosis.JournalFileExists)
                WarningBanner = "⚠ Journal SQLite detectado. O banco será aberto com recuperação automática.";
            else if (!diagnosis.ManifestExists)
                WarningBanner = "⚠ manifest.json ausente. Caso aberto em modo limitado.";

            State = LoadingState.Loaded;
            CaseSelected?.Invoke(SelectedCasePath);
        }, "Validando caso...");
    }

    private void OnCreateCase()
    {
        StatusText = "ℹ A criação de caso pela UI está em desenvolvimento. Use 'mailvault index' no terminal.";
    }

    private void OnOpenMbox()
    {
        StatusText = "ℹ Suporte a MBOX: use 'mailvault index <arquivo.mbox>' no terminal. A UI mostrará o caso após indexação.";
    }

    public void LoadRecentCases(System.Collections.Generic.IEnumerable<RecentCaseEntry> entries)
    {
        RecentCases.Clear();
        foreach (var e in entries)
            RecentCases.Add(e);
    }

    public void SelectRecentCase(RecentCaseEntry entry)
    {
        SelectedCasePath = entry.CaseFolderPath;
    }
}

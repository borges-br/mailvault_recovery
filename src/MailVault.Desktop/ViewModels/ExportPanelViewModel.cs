using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MailVault.Core;
using MailVault.Desktop.Services;
using MailVault.Exporters.Eml;
using MailVault.Exporters.Mbox;
using Microsoft.Data.Sqlite;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class ExportPanelViewModel : ViewModelBase
{
    private readonly DesktopExportService _exportService;
    private readonly LocalSettingsService _settingsService = new();

    // ── Forensic export (existing) ────────────────────────────────────────
    private string _caseFolderPath = "";
    private string _exportFormat = "eml";
    private string _exportPath = "";
    private string _exportStatus = "Selecione o formato e pasta de destino para iniciar a exportação.";
    private bool _includeAttachments = true;
    private bool _dryRun;
    private int _messagesSelected;
    private int _messagesExported;
    private long _bytesExported;

    // ── Recovery export (new) ─────────────────────────────────────────────
    private string _recoverySourcePath = "";
    private string _recoveryOutputPath = "";
    private bool _isRecoveryRunning;
    private bool _hasRecoveryCompleted;
    private string _recoveryStatus = "";
    private int _recoveryExported;
    private int _recoveryFailed;
    private int _recoveryAttachmentsExported;
    private int _recoveryAttachmentsFailed;
    private string _recoveryElapsedText = "";
    private string _recoveryMetricsSummary = "";
    private string? _selectedFolderPath;
    private CancellationTokenSource? _recoveryCts;
    private System.Timers.Timer? _recoveryTimer;
    private DateTime _recoveryStartTime;

    // Confirmação antes de exportar (setting ConfirmBeforeExport).
    private bool _showExportConfirm;
    private string _exportConfirmMessage = "";
    private bool _exportConfirmed;
    private bool _pendingEml;
    private bool _pendingUseSelectedFolder;

    // ── Forensic properties ───────────────────────────────────────────────
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

    // ── Recovery properties ───────────────────────────────────────────────
    public string RecoverySourcePath
    {
        get => _recoverySourcePath;
        set => this.RaiseAndSetIfChanged(ref _recoverySourcePath, value);
    }

    public string RecoveryOutputPath
    {
        get => _recoveryOutputPath;
        set => this.RaiseAndSetIfChanged(ref _recoveryOutputPath, value);
    }

    public bool IsRecoveryRunning
    {
        get => _isRecoveryRunning;
        set
        {
            this.RaiseAndSetIfChanged(ref _isRecoveryRunning, value);
            this.RaisePropertyChanged(nameof(IsRecoveryIdle));
        }
    }

    public bool IsRecoveryIdle => !_isRecoveryRunning;

    public bool HasRecoveryCompleted
    {
        get => _hasRecoveryCompleted;
        set => this.RaiseAndSetIfChanged(ref _hasRecoveryCompleted, value);
    }

    public string RecoveryStatus
    {
        get => _recoveryStatus;
        set => this.RaiseAndSetIfChanged(ref _recoveryStatus, value);
    }

    public int RecoveryExported
    {
        get => _recoveryExported;
        set => this.RaiseAndSetIfChanged(ref _recoveryExported, value);
    }

    public int RecoveryFailed
    {
        get => _recoveryFailed;
        set => this.RaiseAndSetIfChanged(ref _recoveryFailed, value);
    }

    public int RecoveryAttachmentsExported
    {
        get => _recoveryAttachmentsExported;
        set => this.RaiseAndSetIfChanged(ref _recoveryAttachmentsExported, value);
    }

    public int RecoveryAttachmentsFailed
    {
        get => _recoveryAttachmentsFailed;
        set => this.RaiseAndSetIfChanged(ref _recoveryAttachmentsFailed, value);
    }

    public string RecoveryElapsedText
    {
        get => _recoveryElapsedText;
        set => this.RaiseAndSetIfChanged(ref _recoveryElapsedText, value);
    }

    public string RecoveryMetricsSummary
    {
        get => _recoveryMetricsSummary;
        set => this.RaiseAndSetIfChanged(ref _recoveryMetricsSummary, value);
    }

    /// <summary>Pasta selecionada no FolderTree para exportação seletiva. Null = todas as pastas.</summary>
    public string? SelectedFolderPath
    {
        get => _selectedFolderPath;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedFolderPath, value);
            this.RaisePropertyChanged(nameof(HasSelectedFolder));
            this.RaisePropertyChanged(nameof(SelectedFolderLabel));
        }
    }

    public bool HasSelectedFolder => !string.IsNullOrWhiteSpace(_selectedFolderPath);
    public string SelectedFolderLabel => HasSelectedFolder
        ? $"Exportar pasta: {_selectedFolderPath}"
        : "Exportar todas as pastas";

    public bool ShowExportConfirm
    {
        get => _showExportConfirm;
        set => this.RaiseAndSetIfChanged(ref _showExportConfirm, value);
    }

    public string ExportConfirmMessage
    {
        get => _exportConfirmMessage;
        set => this.RaiseAndSetIfChanged(ref _exportConfirmMessage, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────
    public ICommand RunExportCommand { get; }
    public ICommand RecoverToEmlCommand { get; }
    public ICommand RecoverToMboxCommand { get; }
    public ICommand RecoverSelectedFolderToEmlCommand { get; }
    public ICommand RecoverSelectedFolderToMboxCommand { get; }
    public ICommand CancelRecoveryCommand { get; }
    public ICommand OpenRecoveryFolderCommand { get; }
    public ICommand ClearSelectedFolderCommand { get; }
    public ICommand ConfirmExportCommand { get; }
    public ICommand CancelExportConfirmCommand { get; }

    public ExportPanelViewModel() : this(new DesktopExportService()) { }

    public ExportPanelViewModel(DesktopExportService exportService)
    {
        _exportService = exportService;

        var runExportCmd = ReactiveCommand.CreateFromTask(OnRunExportAsync);
        runExportCmd.ThrownExceptions.Subscribe(ex =>
            ExportStatus = $"❌ Falha inesperada no comando de exportação: {ex.Message}");
        RunExportCommand = runExportCmd;

        var recoverEmlCmd = ReactiveCommand.CreateFromTask(() => RunRecoveryExportAsync(eml: true));
        recoverEmlCmd.ThrownExceptions.Subscribe(ex => RecoveryStatus = $"❌ {ex.Message}");
        RecoverToEmlCommand = recoverEmlCmd;

        var recoverMboxCmd = ReactiveCommand.CreateFromTask(() => RunRecoveryExportAsync(eml: false));
        recoverMboxCmd.ThrownExceptions.Subscribe(ex => RecoveryStatus = $"❌ {ex.Message}");
        RecoverToMboxCommand = recoverMboxCmd;

        var recoverFolderEmlCmd = ReactiveCommand.CreateFromTask(() => RunRecoveryExportAsync(eml: true, useSelectedFolder: true));
        recoverFolderEmlCmd.ThrownExceptions.Subscribe(ex => RecoveryStatus = $"❌ {ex.Message}");
        RecoverSelectedFolderToEmlCommand = recoverFolderEmlCmd;

        var recoverFolderMboxCmd = ReactiveCommand.CreateFromTask(() => RunRecoveryExportAsync(eml: false, useSelectedFolder: true));
        recoverFolderMboxCmd.ThrownExceptions.Subscribe(ex => RecoveryStatus = $"❌ {ex.Message}");
        RecoverSelectedFolderToMboxCommand = recoverFolderMboxCmd;

        CancelRecoveryCommand = ReactiveCommand.Create(() => { _recoveryCts?.Cancel(); });
        OpenRecoveryFolderCommand = ReactiveCommand.Create(OpenRecoveryFolder);
        ClearSelectedFolderCommand = ReactiveCommand.Create(() => { SelectedFolderPath = null; });

        var confirmExportCmd = ReactiveCommand.CreateFromTask(OnConfirmExportAsync);
        confirmExportCmd.ThrownExceptions.Subscribe(ex => RecoveryStatus = $"❌ {ex.Message}");
        ConfirmExportCommand = confirmExportCmd;
        CancelExportConfirmCommand = ReactiveCommand.Create(() => { ShowExportConfirm = false; });
    }

    public void SetCaseFolder(string caseFolderPath)
    {
        _caseFolderPath = caseFolderPath;

        if (string.IsNullOrWhiteSpace(ExportPath))
            ExportPath = Path.Combine(caseFolderPath, "exports");

        // Read source_file from case.db to pre-populate the recovery source
        string dbPath = Path.Combine(caseFolderPath, "case.db");
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT source_file FROM case_info LIMIT 1";
                RecoverySourcePath = cmd.ExecuteScalar()?.ToString() ?? "";
            }
            catch
            {
                RecoverySourcePath = "";
            }
        }
        else
        {
            RecoverySourcePath = "";
        }

        if (string.IsNullOrWhiteSpace(RecoveryOutputPath))
            RecoveryOutputPath = Path.Combine(caseFolderPath, "recovery-export");
    }

    private async Task RunRecoveryExportAsync(bool eml, bool useSelectedFolder = false)
    {
        if (string.IsNullOrWhiteSpace(RecoverySourcePath) || !File.Exists(RecoverySourcePath))
        {
            RecoveryStatus = "❌ Arquivo de origem não encontrado. Carregue um caso com arquivo indexado.";
            return;
        }

        if (string.IsNullOrWhiteSpace(RecoveryOutputPath))
        {
            RecoveryStatus = "❌ Especifique uma pasta de destino.";
            return;
        }

        string? folderFilter = useSelectedFolder ? SelectedFolderPath : null;
        if (useSelectedFolder && string.IsNullOrWhiteSpace(SelectedFolderPath))
        {
            RecoveryStatus = "⚠️ Nenhuma pasta selecionada. Use 'Exportar tudo' ou selecione uma pasta no navegador.";
            return;
        }

        var settings = _settingsService.Load();

        // Gate de confirmação (lê a setting na hora; toggla sem reiniciar).
        if (settings.ConfirmBeforeExport && !_exportConfirmed)
        {
            _pendingEml = eml;
            _pendingUseSelectedFolder = useSelectedFolder;
            string fmtC = eml ? "EML" : "MBOX";
            string scopeC = folderFilter != null ? $"a pasta '{folderFilter}'" : "todas as pastas";
            ExportConfirmMessage = $"Exportar {scopeC} em formato {fmtC} para:\n{RecoveryOutputPath}";
            ShowExportConfirm = true;
            return;
        }
        _exportConfirmed = false;
        ShowExportConfirm = false;

        IsRecoveryRunning = true;
        HasRecoveryCompleted = false;
        RecoveryExported = 0;
        RecoveryFailed = 0;
        RecoveryAttachmentsExported = 0;
        RecoveryAttachmentsFailed = 0;
        RecoveryMetricsSummary = "";
        string scopeLabel = folderFilter != null ? $"pasta '{folderFilter}'" : "todas as pastas";
        RecoveryStatus = eml ? $"Iniciando exportação EML ({scopeLabel})..." : $"Iniciando exportação MBOX ({scopeLabel})...";

        _recoveryCts = new CancellationTokenSource();
        StartRecoveryTimer();

        try
        {
            string ext = Path.GetExtension(RecoverySourcePath).ToLowerInvariant();
            var resolver = new ReflectionAdapterResolver();
            var adapterResult = resolver.ResolveAdapter(ext);

            if (!adapterResult.Success || adapterResult.Reader == null)
            {
                RecoveryStatus = $"❌ Adaptador não disponível: {adapterResult.ErrorMessage}";
                return;
            }

            var progress = new Progress<RecoveryExportProgress>(p =>
            {
                RecoveryExported = p.MessagesExported;
                RecoveryFailed = p.MessagesFailed;
                RecoveryAttachmentsExported = p.AttachmentsExported;
                RecoveryAttachmentsFailed = p.AttachmentsFailed;
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    RecoveryStatus = p.StatusMessage;
            });

            var runner = new RecoveryExportRunner();
            string sourcePath = RecoverySourcePath;
            string outputDir = RecoveryOutputPath;
            var ct = _recoveryCts.Token;
            RecoveryExportResult result;

            if (eml)
            {
                result = await Task.Run(() => runner.ExportToEmlAsync(
                    adapterResult.Reader, new EmlExporter(),
                    sourcePath, outputDir,
                    targetFolderPath: folderFilter, messageIds: null,
                    progress, ct));
            }
            else
            {
                result = await Task.Run(() => runner.ExportToMboxAsync(
                    adapterResult.Reader, new MboxExporter(new EmlExporter()),
                    sourcePath, outputDir,
                    targetFolderPath: folderFilter,
                    progress, ct));
            }

            RecoveryExported = result.ExportedMessages;
            RecoveryFailed = result.FailedMessages;
            RecoveryAttachmentsExported = result.ExportedAttachments;
            RecoveryAttachmentsFailed = result.FailedAttachments;

            string fmt = eml ? "EML" : "MBOX";
            string scope = folderFilter != null ? $" (pasta '{folderFilter}')" : "";
            RecoveryStatus = result.FailedMessages == 0
                ? $"✅ Exportação {fmt} concluída{scope}. {result.ExportedMessages} e-mails exportados para: {outputDir}"
                : $"⚠️ Exportação {fmt} com {result.FailedMessages} falhas{scope}. {result.ExportedMessages} e-mails exportados para: {outputDir}";

            if (result.Metrics is { } m)
                RecoveryMetricsSummary = $"{m.MessagesPerSecond:F2} msg/s · {m.AvgMillisecondsPerMessage:F0} ms/msg · getMsg {m.GetMessageMs:F0} ms · etapa+lenta: {m.SlowestStage}";

            HasRecoveryCompleted = true;

            if (settings.OpenFolderAfterExport)
            {
                OpenRecoveryFolder();
            }
        }
        catch (OperationCanceledException)
        {
            RecoveryStatus = "⚠️ Exportação cancelada pelo usuário.";
        }
        catch (Exception ex)
        {
            RecoveryStatus = $"❌ Erro durante exportação: {ex.Message}";
        }
        finally
        {
            IsRecoveryRunning = false;
            StopRecoveryTimer();
            _recoveryCts?.Dispose();
            _recoveryCts = null;
        }
    }

    private async Task OnConfirmExportAsync()
    {
        ShowExportConfirm = false;
        _exportConfirmed = true;
        await RunRecoveryExportAsync(_pendingEml, _pendingUseSelectedFolder);
    }

    private void OpenRecoveryFolder()
    {
        string path = RecoveryOutputPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void StartRecoveryTimer()
    {
        _recoveryStartTime = DateTime.Now;
        RecoveryElapsedText = "00:00:00";
        try
        {
            _recoveryTimer?.Stop();
            _recoveryTimer?.Dispose();
            _recoveryTimer = new System.Timers.Timer(1000) { AutoReset = true };
            _recoveryTimer.Elapsed += OnRecoveryTimerTick;
            _recoveryTimer.Start();
        }
        catch { }
    }

    private void StopRecoveryTimer()
    {
        try
        {
            _recoveryTimer?.Stop();
            _recoveryTimer?.Dispose();
            _recoveryTimer = null;
        }
        catch { }
    }

    private void OnRecoveryTimerTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!IsRecoveryRunning) { StopRecoveryTimer(); return; }
        var elapsed = DateTime.Now - _recoveryStartTime;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RecoveryElapsedText = elapsed.ToString(@"hh\:mm\:ss");
        });
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
                ExportStatus = $"[DRY RUN] Análise concluída. E-mails selecionados: {result.MessagesSelected}. Nenhum arquivo físico foi gravado.";
            else
                ExportStatus = $"✓ Exportação concluída com sucesso! E-mails gravados: {result.MessagesExported}/{result.MessagesSelected} em {ExportPath}.";
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

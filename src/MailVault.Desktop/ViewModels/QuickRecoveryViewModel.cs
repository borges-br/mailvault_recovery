using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MailVault.Core;
using MailVault.Exporters.Eml;
using MailVault.Exporters.Mbox;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class QuickRecoveryViewModel : ViewModelBase
{
    private string _sourceFilePath = "";
    private string _outputFolderPath = "";
    private bool _isRunning;
    private bool _hasCompleted;
    private string _statusText = "Selecione um arquivo de e-mail e uma pasta de destino para iniciar.";
    private int _messagesExported;
    private int _messagesFailed;
    private int _attachmentsExported;
    private string _elapsedText = "";
    private CancellationTokenSource? _cts;
    private System.Timers.Timer? _timer;
    private DateTime _startTime;

    // Delegates for file/folder picking (set from the view code-behind)
    public Func<Task<string?>>? BrowseForFile { get; set; }
    public Func<Task<string?>>? BrowseForFolder { get; set; }

    // ── Properties ────────────────────────────────────────────────────────
    public string SourceFilePath
    {
        get => _sourceFilePath;
        set => this.RaiseAndSetIfChanged(ref _sourceFilePath, value);
    }

    public string OutputFolderPath
    {
        get => _outputFolderPath;
        set => this.RaiseAndSetIfChanged(ref _outputFolderPath, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            this.RaiseAndSetIfChanged(ref _isRunning, value);
            this.RaisePropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !_isRunning;

    public bool HasCompleted
    {
        get => _hasCompleted;
        set => this.RaiseAndSetIfChanged(ref _hasCompleted, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public int MessagesExported
    {
        get => _messagesExported;
        set => this.RaiseAndSetIfChanged(ref _messagesExported, value);
    }

    public int MessagesFailed
    {
        get => _messagesFailed;
        set => this.RaiseAndSetIfChanged(ref _messagesFailed, value);
    }

    public int AttachmentsExported
    {
        get => _attachmentsExported;
        set => this.RaiseAndSetIfChanged(ref _attachmentsExported, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        set => this.RaiseAndSetIfChanged(ref _elapsedText, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────
    public ICommand BrowseSourceFileCommand { get; }
    public ICommand BrowseOutputFolderCommand { get; }
    public ICommand ExportToEmlCommand { get; }
    public ICommand ExportToMboxCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }

    public QuickRecoveryViewModel()
    {
        BrowseSourceFileCommand = ReactiveCommand.CreateFromTask(OnBrowseSourceFileAsync);
        BrowseOutputFolderCommand = ReactiveCommand.CreateFromTask(OnBrowseOutputFolderAsync);

        var exportEmlCmd = ReactiveCommand.CreateFromTask(() => RunExportAsync(eml: true));
        exportEmlCmd.ThrownExceptions.Subscribe(ex => StatusText = $"Erro inesperado: {ex.Message}");
        ExportToEmlCommand = exportEmlCmd;

        var exportMboxCmd = ReactiveCommand.CreateFromTask(() => RunExportAsync(eml: false));
        exportMboxCmd.ThrownExceptions.Subscribe(ex => StatusText = $"Erro inesperado: {ex.Message}");
        ExportToMboxCommand = exportMboxCmd;

        CancelCommand = ReactiveCommand.Create(() => { _cts?.Cancel(); });
        OpenOutputFolderCommand = ReactiveCommand.Create(OpenOutputFolder);
    }

    private async Task OnBrowseSourceFileAsync()
    {
        if (BrowseForFile != null)
        {
            string? path = await BrowseForFile();
            if (!string.IsNullOrWhiteSpace(path))
                SourceFilePath = path;
        }
    }

    private async Task OnBrowseOutputFolderAsync()
    {
        if (BrowseForFolder != null)
        {
            string? path = await BrowseForFolder();
            if (!string.IsNullOrWhiteSpace(path))
                OutputFolderPath = path;
        }
    }

    private async Task RunExportAsync(bool eml)
    {
        if (string.IsNullOrWhiteSpace(SourceFilePath) || !File.Exists(SourceFilePath))
        {
            StatusText = "Arquivo de origem nao encontrado. Selecione um arquivo valido.";
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFolderPath))
        {
            StatusText = "Especifique uma pasta de destino.";
            return;
        }

        IsRunning = true;
        HasCompleted = false;
        MessagesExported = 0;
        MessagesFailed = 0;
        AttachmentsExported = 0;
        StatusText = eml ? "Iniciando exportacao EML..." : "Iniciando exportacao MBOX...";

        _cts = new CancellationTokenSource();
        StartTimer();

        try
        {
            string ext = Path.GetExtension(SourceFilePath).ToLowerInvariant();
            var resolver = new ReflectionAdapterResolver();
            var adapterResult = resolver.ResolveAdapter(ext);

            if (!adapterResult.Success || adapterResult.Reader == null)
            {
                StatusText = $"Adaptador nao disponivel para '{ext}': {adapterResult.ErrorMessage}";
                return;
            }

            var progress = new Progress<RecoveryExportProgress>(p =>
            {
                MessagesExported = p.MessagesExported;
                MessagesFailed = p.MessagesFailed;
                AttachmentsExported = p.AttachmentsExported;
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    StatusText = p.StatusMessage;
            });

            var runner = new RecoveryExportRunner();
            string sourcePath = SourceFilePath;
            string outputDir = OutputFolderPath;
            var ct = _cts.Token;
            RecoveryExportResult result;

            if (eml)
            {
                result = await Task.Run(() => runner.ExportToEmlAsync(
                    adapterResult.Reader, new EmlExporter(),
                    sourcePath, outputDir,
                    targetFolderPath: null, messageIds: null,
                    progress, ct));
            }
            else
            {
                result = await Task.Run(() => runner.ExportToMboxAsync(
                    adapterResult.Reader, new MboxExporter(new EmlExporter()),
                    sourcePath, outputDir,
                    targetFolderPath: null,
                    progress, ct));
            }

            MessagesExported = result.ExportedMessages;
            MessagesFailed = result.FailedMessages;
            AttachmentsExported = result.ExportedAttachments;

            string fmt = eml ? "EML" : "MBOX";
            StatusText = result.FailedMessages == 0
                ? $"Exportacao {fmt} concluida. {result.ExportedMessages} e-mails exportados para: {outputDir}"
                : $"Exportacao {fmt} com {result.FailedMessages} falhas. {result.ExportedMessages} e-mails exportados para: {outputDir}";

            HasCompleted = true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Exportacao cancelada pelo usuario.";
        }
        catch (Exception ex)
        {
            StatusText = $"Erro durante exportacao: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            StopTimer();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OpenOutputFolder()
    {
        string path = OutputFolderPath;
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

    private void StartTimer()
    {
        _startTime = DateTime.Now;
        ElapsedText = "00:00:00";
        try
        {
            _timer?.Stop();
            _timer?.Dispose();
            _timer = new System.Timers.Timer(1000) { AutoReset = true };
            _timer.Elapsed += OnTimerTick;
            _timer.Start();
        }
        catch { }
    }

    private void StopTimer()
    {
        try
        {
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
        }
        catch { }
    }

    private void OnTimerTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!IsRunning) { StopTimer(); return; }
        var elapsed = DateTime.Now - _startTime;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ElapsedText = elapsed.ToString(@"hh\:mm\:ss");
        });
    }
}

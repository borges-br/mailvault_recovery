using System;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

/// <summary>
/// Possible states for a loadable view model.
/// </summary>
public enum LoadingState
{
    Idle,
    Loading,
    Loaded,
    Empty,
    Error,
    Cancelled
}

/// <summary>
/// Base class for ViewModels that load data asynchronously.
/// Provides loading state, error handling, cancellation, and timeout support.
/// Default timeout is 15 seconds.
/// </summary>
public abstract class LoadableViewModelBase : ViewModelBase
{
    private LoadingState _state = LoadingState.Idle;
    private string _errorMessage = "";
    private string _loadingMessage = "Carregando...";
    private bool _isLoading;
    private bool _hasError;
    private bool _isEmpty;
    private bool _isLoaded;

    protected CancellationTokenSource? _cts;

    /// <summary>Default timeout for load operations.</summary>
    protected virtual TimeSpan LoadTimeout => TimeSpan.FromSeconds(15);

    public LoadingState State
    {
        get => _state;
        protected set
        {
            this.RaiseAndSetIfChanged(ref _state, value);
            IsLoading = value == LoadingState.Loading;
            HasError = value == LoadingState.Error;
            IsEmpty = value == LoadingState.Empty;
            IsLoaded = value == LoadingState.Loaded;
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        protected set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public string LoadingMessage
    {
        get => _loadingMessage;
        protected set => this.RaiseAndSetIfChanged(ref _loadingMessage, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => this.RaiseAndSetIfChanged(ref _hasError, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => this.RaiseAndSetIfChanged(ref _isEmpty, value);
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        private set => this.RaiseAndSetIfChanged(ref _isLoaded, value);
    }

    /// <summary>
    /// Starts a load operation with timeout and cancellation support.
    /// Sets state to Loading before calling <paramref name="loadAction"/>.
    /// Sets state to Loaded, Empty, Error or Cancelled based on the result.
    /// </summary>
    protected async Task ExecuteLoadAsync(Func<CancellationToken, Task> loadAction, string loadingMessage = "Carregando...")
    {
        // Cancel any previous load
        CancelCurrentLoad();
        _cts = new CancellationTokenSource();

        State = LoadingState.Loading;
        LoadingMessage = loadingMessage;
        ErrorMessage = "";

        try
        {
            using var timeoutCts = new CancellationTokenSource(LoadTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);

            await loadAction(linkedCts.Token);

            // Subclass can override OnLoadedAsync to set State = Empty if no results
            if (State == LoadingState.Loading)
                State = LoadingState.Loaded;
        }
        catch (OperationCanceledException)
        {
            State = _cts.IsCancellationRequested ? LoadingState.Cancelled : LoadingState.Error;
            if (State == LoadingState.Error)
                ErrorMessage = "A operação excedeu o tempo limite (15 segundos).";
        }
        catch (Exception ex)
        {
            State = LoadingState.Error;
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Cancels the current in-progress load operation if any.</summary>
    protected void CancelCurrentLoad()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Resets to Idle state so the VM can be reloaded.</summary>
    public virtual void Reset()
    {
        CancelCurrentLoad();
        State = LoadingState.Idle;
        ErrorMessage = "";
    }
}

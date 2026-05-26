using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MailVault.Core;
using MailVault.Domain;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class MessageListViewModel : LoadableViewModelBase
{
    private FolderId? _currentFolderId;
    private int _currentPage = 1;
    private int _pageSize = 50;
    private int _totalCount;
    private MailItem? _selectedMessage;

    private ICaseIndexReader? _reader;

    public ObservableCollection<MailItem> Messages { get; }

    public int CurrentPage
    {
        get => _currentPage;
        set => this.RaiseAndSetIfChanged(ref _currentPage, value);
    }

    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)_totalCount / _pageSize));

    public MailItem? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedMessage, value);
            if (value != null)
            {
                MessageSelected?.Invoke(value);
            }
        }
    }

    public ICommand NextPageCommand { get; }
    public ICommand PrevPageCommand { get; }

    public event Action<MailItem>? MessageSelected;

    public MessageListViewModel()
    {
        Messages = new ObservableCollection<MailItem>();
        NextPageCommand = ReactiveCommand.CreateFromTask(OnNextPageAsync);
        PrevPageCommand = ReactiveCommand.CreateFromTask(OnPrevPageAsync);
    }

    public async Task SetFolderAsync(FolderId folderId, ICaseIndexReader reader, CancellationToken ct)
    {
        _reader = reader;
        _currentFolderId = folderId;
        CurrentPage = 1;
        await LoadMessagesAsync(reader, ct);
    }

    public async Task LoadMessagesAsync(ICaseIndexReader reader, CancellationToken ct)
    {
        if (_currentFolderId == null) return;

        await ExecuteLoadAsync(async linkedCt =>
        {
            Messages.Clear();
            int offset = (CurrentPage - 1) * _pageSize;

            var list = new List<MailItem>();
            await foreach (var msg in reader.GetMessagesInFolderAsync(_currentFolderId, _pageSize, offset, linkedCt))
            {
                list.Add(msg);
            }

            foreach (var msg in list)
            {
                Messages.Add(msg);
            }

            _totalCount = Messages.Count == _pageSize ? offset + _pageSize + 1 : offset + Messages.Count;
            this.RaisePropertyChanged(nameof(TotalPages));
            State = Messages.Count == 0 ? LoadingState.Empty : LoadingState.Loaded;
        }, "Carregando mensagens...");
    }

    private async Task OnNextPageAsync()
    {
        if (CurrentPage < TotalPages && _reader != null)
        {
            CurrentPage++;
            await LoadMessagesAsync(_reader, CancellationToken.None);
        }
    }

    private async Task OnPrevPageAsync()
    {
        if (CurrentPage > 1 && _reader != null)
        {
            CurrentPage--;
            await LoadMessagesAsync(_reader, CancellationToken.None);
        }
    }

    public void ResetMessages()
    {
        _currentFolderId = null;
        CurrentPage = 1;
        _totalCount = 0;
        SelectedMessage = null;
        Messages.Clear();
        this.RaisePropertyChanged(nameof(TotalPages));
        Reset();
    }
}

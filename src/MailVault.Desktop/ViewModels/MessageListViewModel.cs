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

public class MessageListViewModel : ViewModelBase
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
        NextPageCommand = ReactiveCommand.Create(OnNextPage);
        PrevPageCommand = ReactiveCommand.Create(OnPrevPage);
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

        Messages.Clear();
        int offset = (CurrentPage - 1) * _pageSize;

        var list = new List<MailItem>();
        await foreach (var msg in reader.GetMessagesInFolderAsync(_currentFolderId, _pageSize, offset, ct))
        {
            list.Add(msg);
        }

        foreach (var msg in list)
        {
            Messages.Add(msg);
        }

        _totalCount = Messages.Count == _pageSize ? offset + _pageSize + 1 : offset + Messages.Count;
        this.RaisePropertyChanged(nameof(TotalPages));
    }

    private void OnNextPage()
    {
        if (CurrentPage < TotalPages && _reader != null)
        {
            CurrentPage++;
            _ = LoadMessagesAsync(_reader, CancellationToken.None);
        }
    }

    private void OnPrevPage()
    {
        if (CurrentPage > 1 && _reader != null)
        {
            CurrentPage--;
            _ = LoadMessagesAsync(_reader, CancellationToken.None);
        }
    }
}

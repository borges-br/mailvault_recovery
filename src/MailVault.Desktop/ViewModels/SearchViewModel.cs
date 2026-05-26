using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MailVault.Core;
using MailVault.Domain;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class SearchViewModel : ViewModelBase
{
    private string _searchQuery = "";
    private string _statusText = "";
    private MailItem? _selectedMessage;

    public string SearchQuery
    {
        get => _searchQuery;
        set => this.RaiseAndSetIfChanged(ref _searchQuery, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public ObservableCollection<MailItem> Results { get; } = new();

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

    private ICaseIndexReader? _reader;

    public ICommand SearchCommand { get; }

    public event Action<MailItem>? MessageSelected;

    public SearchViewModel()
    {
        SearchCommand = ReactiveCommand.CreateFromTask(OnSearchAsync);
    }

    public void SetReader(ICaseIndexReader reader)
    {
        _reader = reader;
    }

    public async Task OnSearchAsync()
    {
        if (_reader == null)
        {
            StatusText = "Erro: Leitor do caso não inicializado.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            StatusText = "Por favor, digite um termo de busca.";
            return;
        }

        Results.Clear();
        StatusText = "Buscando...";

        var list = new System.Collections.Generic.List<MailItem>();
        await foreach (var msg in _reader.SearchMessagesAsync(SearchQuery, null, 100, 0, CancellationToken.None))
        {
            list.Add(msg);
        }

        foreach (var msg in list)
        {
            Results.Add(msg);
        }

        StatusText = $"Busca concluída. Encontradas {Results.Count} correspondências.";
    }

}

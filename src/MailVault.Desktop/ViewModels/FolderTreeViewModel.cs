using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class FolderTreeItemViewModel : ViewModelBase
{
    private readonly FolderNode _node;
    private bool _isExpanded;
    private bool _isSelected;

    public string DisplayName => $"{_node.DisplayName} ({_node.MessageCount})";
    public string FullPath => _node.FullPath;
    public FolderId FolderId => _node.Id;

    public ObservableCollection<FolderTreeItemViewModel> Children { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public FolderTreeItemViewModel(FolderNode node)
    {
        _node = node;
        Children = new ObservableCollection<FolderTreeItemViewModel>();
        foreach (var child in node.Children)
        {
            Children.Add(new FolderTreeItemViewModel(child));
        }
    }
}

public class FolderTreeViewModel : ViewModelBase
{
    private FolderTreeItemViewModel? _selectedItem;

    public ObservableCollection<FolderTreeItemViewModel> RootFolders { get; }

    public FolderTreeItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedItem, value);
            if (value != null)
            {
                FolderSelected?.Invoke(value.FolderId);
            }
        }
    }

    public event Action<FolderId>? FolderSelected;

    public FolderTreeViewModel()
    {
        RootFolders = new ObservableCollection<FolderTreeItemViewModel>();
    }

    public async Task LoadFoldersAsync(ICaseIndexReader reader, CancellationToken ct)
    {
        RootFolders.Clear();
        
        var rootList = new List<FolderNode>();
        await foreach (var node in reader.GetFolderHierarchyAsync(ct))
        {
            rootList.Add(node);
        }

        foreach (var rootNode in rootList)
        {
            RootFolders.Add(new FolderTreeItemViewModel(rootNode));
        }
    }
}

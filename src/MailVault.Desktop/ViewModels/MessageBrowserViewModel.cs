namespace MailVault.Desktop.ViewModels;

public class MessageBrowserViewModel : ViewModelBase
{
    public FolderTreeViewModel FolderTree { get; }
    public MessageListViewModel MessageList { get; }
    public MessagePreviewViewModel MessagePreview { get; }

    public MessageBrowserViewModel(FolderTreeViewModel folderTree, MessageListViewModel messageList, MessagePreviewViewModel messagePreview)
    {
        FolderTree = folderTree;
        MessageList = messageList;
        MessagePreview = messagePreview;
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class CaseOverviewViewModel : LoadableViewModelBase
{
    private string _caseId = "";
    private string _sourceFileMasked = "";
    private string _sourceSha256 = "";
    private string _adapterNameVersion = "";
    private int _folderCount;
    private int _messageCount;
    private int _attachmentCount;
    private int _issueCount;
    private string _totalAttachmentSize = "0 MB";

    public string CaseId
    {
        get => _caseId;
        set => this.RaiseAndSetIfChanged(ref _caseId, value);
    }

    public string SourceFileMasked
    {
        get => _sourceFileMasked;
        set => this.RaiseAndSetIfChanged(ref _sourceFileMasked, value);
    }

    public string SourceSha256
    {
        get => _sourceSha256;
        set => this.RaiseAndSetIfChanged(ref _sourceSha256, value);
    }

    public string AdapterNameVersion
    {
        get => _adapterNameVersion;
        set => this.RaiseAndSetIfChanged(ref _adapterNameVersion, value);
    }

    public int FolderCount
    {
        get => _folderCount;
        set => this.RaiseAndSetIfChanged(ref _folderCount, value);
    }

    public int MessageCount
    {
        get => _messageCount;
        set => this.RaiseAndSetIfChanged(ref _messageCount, value);
    }

    public int AttachmentCount
    {
        get => _attachmentCount;
        set => this.RaiseAndSetIfChanged(ref _attachmentCount, value);
    }

    public int IssueCount
    {
        get => _issueCount;
        set => this.RaiseAndSetIfChanged(ref _issueCount, value);
    }

    public string TotalAttachmentSize
    {
        get => _totalAttachmentSize;
        set => this.RaiseAndSetIfChanged(ref _totalAttachmentSize, value);
    }

    public async Task LoadFromReaderAsync(ICaseIndexReader reader, CancellationToken ct)
    {
        await ExecuteLoadAsync(async (linkedCt) =>
        {
            var caseInfo = await reader.GetCaseInfoAsync(linkedCt);
            if (caseInfo != null)
            {
                CaseId = caseInfo.CaseId;

                // Mask user path
                string origFile = caseInfo.SourceFile;
                if (origFile.Contains("Users\\natha"))
                {
                    origFile = origFile.Replace("Users\\natha", "Users\\<USER>");
                }
                else if (origFile.Contains("Users\\"))
                {
                    int index = origFile.IndexOf("Users\\") + 6;
                    int nextSlash = origFile.IndexOf('\\', index);
                    if (nextSlash != -1)
                    {
                        origFile = origFile.Substring(0, index) + "<USER>" + origFile.Substring(nextSlash);
                    }
                }
                SourceFileMasked = origFile;
                SourceSha256 = caseInfo.SourceSha256;
                AdapterNameVersion = $"{caseInfo.AdapterName} ({caseInfo.AdapterVersion})";
            }

            FolderCount = await reader.GetFolderCountAsync(linkedCt);
            MessageCount = await reader.GetMessageCountAsync(linkedCt);
            AttachmentCount = await reader.GetAttachmentCountAsync(linkedCt);
            IssueCount = await reader.GetIssueCountAsync(linkedCt);

            long totalSize = await reader.GetTotalAttachmentSizeAsync(linkedCt);
            TotalAttachmentSize = $"{((double)totalSize / 1024 / 1024):N2} MB";

            State = MessageCount > 0 ? LoadingState.Loaded : LoadingState.Empty;
        }, "Carregando visão geral...");
    }
}

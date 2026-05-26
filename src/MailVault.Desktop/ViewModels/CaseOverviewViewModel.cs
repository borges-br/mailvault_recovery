using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Desktop.Services;
using MailVault.Domain;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class CaseOverviewViewModel : LoadableViewModelBase
{
    private string _caseId = "Não disponível";
    private string _adapterName = "Não disponível";
    private string _adapterVersion = "Não disponível";
    private string _adapterNameVersion = "Não disponível";
    private string _sourceFileMasked = "Não informado no case.db";
    private string _sourceSha256 = "Não informado no case.db";
    private string _sourceSha256Short = "Não informado";
    private int _folderCount;
    private int _messageCount;
    private int _attachmentCount;
    private int _issueCount;
    private string _totalAttachmentSizeFormatted = "0,00 MB";
    private string _healthStatus = "Vazio";
    private string _suggestedAction = "";

    public string CaseId
    {
        get => _caseId;
        set => this.RaiseAndSetIfChanged(ref _caseId, value);
    }

    public string AdapterName
    {
        get => _adapterName;
        set => this.RaiseAndSetIfChanged(ref _adapterName, value);
    }

    public string AdapterVersion
    {
        get => _adapterVersion;
        set => this.RaiseAndSetIfChanged(ref _adapterVersion, value);
    }

    public string AdapterNameVersion
    {
        get => _adapterNameVersion;
        set => this.RaiseAndSetIfChanged(ref _adapterNameVersion, value);
    }

    public string SourceFileMasked
    {
        get => _sourceFileMasked;
        set => this.RaiseAndSetIfChanged(ref _sourceFileMasked, value);
    }

    public string SourceSha256
    {
        get => _sourceSha256;
        set
        {
            this.RaiseAndSetIfChanged(ref _sourceSha256, value);
            SourceSha256Short = value.Length > 16 ? $"{value[..16]}..." : value;
        }
    }

    public string SourceSha256Short
    {
        get => _sourceSha256Short;
        set => this.RaiseAndSetIfChanged(ref _sourceSha256Short, value);
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

    public string TotalAttachmentSizeFormatted
    {
        get => _totalAttachmentSizeFormatted;
        set
        {
            this.RaiseAndSetIfChanged(ref _totalAttachmentSizeFormatted, value);
            this.RaisePropertyChanged(nameof(TotalAttachmentSize));
        }
    }

    public string TotalAttachmentSize
    {
        get => TotalAttachmentSizeFormatted;
        set => TotalAttachmentSizeFormatted = value;
    }

    public string HealthStatus
    {
        get => _healthStatus;
        set => this.RaiseAndSetIfChanged(ref _healthStatus, value);
    }

    public ObservableCollection<string> Warnings { get; } = new();

    public bool HasWarnings => Warnings.Count > 0;

    public string SuggestedAction
    {
        get => _suggestedAction;
        set => this.RaiseAndSetIfChanged(ref _suggestedAction, value);
    }

    public async Task LoadFromWorkspaceAsync(CaseOpenResult workspace, CancellationToken ct)
    {
        await ExecuteLoadAsync(async (linkedCt) =>
        {
            linkedCt.ThrowIfCancellationRequested();
            ApplyCaseInfo(workspace.CaseInfo, Path.GetFileName(workspace.CaseFolderPath));
            ApplyStats(workspace.Stats);
            ApplyWarnings(workspace.Warnings);
            HealthStatus = ToHealthStatus(workspace.Status);
            SuggestedAction = workspace.SuggestedAction ?? "";

            if (workspace.Status == CaseWorkspaceStatus.Empty)
            {
                State = LoadingState.Empty;
            }
            else if (workspace.Status == CaseWorkspaceStatus.Error)
            {
                State = LoadingState.Error;
                ErrorMessage = workspace.ErrorMessage ?? "Erro ao abrir case.db.";
            }
            else
            {
                State = LoadingState.Loaded;
            }

            await Task.CompletedTask;
        }, "Carregando visão geral...");
    }

    public async Task LoadFromReaderAsync(ICaseIndexReader reader, CancellationToken ct)
    {
        await ExecuteLoadAsync(async (linkedCt) =>
        {
            var caseInfo = await reader.GetCaseInfoAsync(linkedCt);
            var stats = new CaseWorkspaceStats(
                FolderCount: await reader.GetFolderCountAsync(linkedCt),
                MessageCount: await reader.GetMessageCountAsync(linkedCt),
                AttachmentCount: await reader.GetAttachmentCountAsync(linkedCt),
                IssueCount: await reader.GetIssueCountAsync(linkedCt),
                TotalAttachmentSizeBytes: await reader.GetTotalAttachmentSizeAsync(linkedCt));

            ApplyCaseInfo(caseInfo, "Caso sem case_info");
            ApplyStats(stats);

            Warnings.Clear();
            if (caseInfo is null)
            {
                Warnings.Add("case_info não contém metadados do caso.");
            }

            if (stats.MessageCount == 0)
            {
                Warnings.Add("case.db foi aberto, mas não há mensagens indexadas.");
                SuggestedAction = "Reindexe a mídia de origem ou confira o audit.log para entender por que nenhuma mensagem foi gravada.";
                HealthStatus = "Vazio";
                State = LoadingState.Empty;
            }
            else if (Warnings.Count > 0)
            {
                HealthStatus = "Com avisos";
                State = LoadingState.Loaded;
            }
            else
            {
                SuggestedAction = "";
                HealthStatus = "Íntegro";
                State = LoadingState.Loaded;
            }

            this.RaisePropertyChanged(nameof(HasWarnings));
        }, "Carregando visão geral...");
    }

    private void ApplyCaseInfo(CaseInfoRef? caseInfo, string fallbackCaseId)
    {
        if (caseInfo is null)
        {
            CaseId = string.IsNullOrWhiteSpace(fallbackCaseId) ? "Não disponível" : fallbackCaseId;
            AdapterName = "Não disponível";
            AdapterVersion = "Não disponível";
            AdapterNameVersion = "Não disponível";
            SourceFileMasked = "Não informado no case.db";
            SourceSha256 = "Não informado no case.db";
            return;
        }

        CaseId = string.IsNullOrWhiteSpace(caseInfo.CaseId) ? fallbackCaseId : caseInfo.CaseId;
        AdapterName = string.IsNullOrWhiteSpace(caseInfo.AdapterName) ? "Não disponível" : caseInfo.AdapterName;
        AdapterVersion = string.IsNullOrWhiteSpace(caseInfo.AdapterVersion) ? "Não disponível" : caseInfo.AdapterVersion;
        AdapterNameVersion = $"{AdapterName} ({AdapterVersion})";
        SourceFileMasked = string.IsNullOrWhiteSpace(caseInfo.SourceFile)
            ? "Não informado no case.db"
            : MaskUserPath(caseInfo.SourceFile);
        SourceSha256 = string.IsNullOrWhiteSpace(caseInfo.SourceSha256)
            ? "Não informado no case.db"
            : caseInfo.SourceSha256;
    }

    private void ApplyStats(CaseWorkspaceStats stats)
    {
        FolderCount = stats.FolderCount;
        MessageCount = stats.MessageCount;
        AttachmentCount = stats.AttachmentCount;
        IssueCount = stats.IssueCount;
        TotalAttachmentSizeFormatted = $"{((double)stats.TotalAttachmentSizeBytes / 1024 / 1024):N2} MB";
    }

    private void ApplyWarnings(IReadOnlyList<string> warnings)
    {
        Warnings.Clear();
        foreach (string warning in warnings)
        {
            Warnings.Add(warning);
        }

        this.RaisePropertyChanged(nameof(HasWarnings));
    }

    private static string ToHealthStatus(CaseWorkspaceStatus status)
    {
        return status switch
        {
            CaseWorkspaceStatus.Intact => "Íntegro",
            CaseWorkspaceStatus.Limited => "Modo limitado",
            CaseWorkspaceStatus.Warning => "Com avisos",
            CaseWorkspaceStatus.Empty => "Vazio",
            CaseWorkspaceStatus.Error => "Erro",
            _ => "Com avisos"
        };
    }

    private static string MaskUserPath(string path)
    {
        return Regex.Replace(
            path,
            @"(?i)([a-z]:\\users\\)[^\\]+",
            "$1<USER>");
    }
}

using System;
using System.IO;
using System.Windows.Input;
using MailVault.Desktop.Services;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly LocalSettingsService _settingsService;
    private readonly RecentCasesService _recentCasesService;

    private string _defaultCaseFolder = "";
    private string _defaultCorpusPath = "";
    private bool _darkTheme;
    private int _maxPreviewLength;
    private bool _confirmBeforeExport;
    private bool _openFolderAfterExport;
    private bool _advancedMode;
    private string _statusMessage = "";

    public string DefaultCaseFolder
    {
        get => _defaultCaseFolder;
        set => this.RaiseAndSetIfChanged(ref _defaultCaseFolder, value);
    }

    public string DefaultCorpusPath
    {
        get => _defaultCorpusPath;
        set => this.RaiseAndSetIfChanged(ref _defaultCorpusPath, value);
    }

    public bool DarkTheme
    {
        get => _darkTheme;
        set => this.RaiseAndSetIfChanged(ref _darkTheme, value);
    }

    public int MaxPreviewLength
    {
        get => _maxPreviewLength;
        set => this.RaiseAndSetIfChanged(ref _maxPreviewLength, value);
    }

    public bool ConfirmBeforeExport
    {
        get => _confirmBeforeExport;
        set => this.RaiseAndSetIfChanged(ref _confirmBeforeExport, value);
    }

    public bool OpenFolderAfterExport
    {
        get => _openFolderAfterExport;
        set => this.RaiseAndSetIfChanged(ref _openFolderAfterExport, value);
    }

    public bool AdvancedMode
    {
        get => _advancedMode;
        set => this.RaiseAndSetIfChanged(ref _advancedMode, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand PurgeCacheCommand { get; }

    public SettingsViewModel() : this(new LocalSettingsService(), new RecentCasesService()) { }

    public SettingsViewModel(LocalSettingsService settingsService, RecentCasesService recentCasesService)
    {
        _settingsService = settingsService;
        _recentCasesService = recentCasesService;

        SaveCommand = ReactiveCommand.Create(SaveSettings);
        PurgeCacheCommand = ReactiveCommand.Create(PurgeCache);

        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Load();
        DefaultCaseFolder = settings.DefaultCaseFolder;
        DefaultCorpusPath = settings.DefaultCorpusPath;
        DarkTheme = settings.DarkTheme;
        MaxPreviewLength = settings.MaxPreviewLength;
        ConfirmBeforeExport = settings.ConfirmBeforeExport;
        OpenFolderAfterExport = settings.OpenFolderAfterExport;
        AdvancedMode = settings.AdvancedMode;
    }

    private void SaveSettings()
    {
        var settings = new LocalSettings
        {
            DefaultCaseFolder = DefaultCaseFolder,
            DefaultCorpusPath = DefaultCorpusPath,
            DarkTheme = DarkTheme,
            MaxPreviewLength = MaxPreviewLength,
            ConfirmBeforeExport = ConfirmBeforeExport,
            OpenFolderAfterExport = OpenFolderAfterExport,
            AdvancedMode = AdvancedMode
        };

        _settingsService.Save(settings);
        StatusMessage = "Configurações salvas localmente com sucesso!";
    }

    private void PurgeCache()
    {
        try
        {
            _recentCasesService.Clear();
            StatusMessage = "Cache operacional e histórico de casos limpos com segurança.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao limpar cache: {ex.Message}";
        }
    }
}

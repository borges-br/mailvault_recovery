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

    // Disparado ao salvar: permite que a janela principal reflita o modo diagnóstico na navegação.
    public event Action<bool>? DiagnosticModeChanged;

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
        AdvancedMode = settings.AdvancedMode;
    }

    private void SaveSettings()
    {
        var settings = new LocalSettings
        {
            DefaultCaseFolder = DefaultCaseFolder,
            DefaultCorpusPath = DefaultCorpusPath,
            DarkTheme = DarkTheme,
            AdvancedMode = AdvancedMode
        };

        _settingsService.Save(settings);
        StatusMessage = "Configurações salvas localmente com sucesso!";
        DiagnosticModeChanged?.Invoke(AdvancedMode);
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

using System;
using System.IO;
using System.Text.Json;

namespace MailVault.Desktop.Services;

public sealed class LocalSettings
{
    public string DefaultCaseFolder { get; set; } = "";
    public string DefaultCorpusPath { get; set; } = "";
    public bool DarkTheme { get; set; } = true;
    public bool AdvancedMode { get; set; } = false;
}

public sealed class LocalSettingsService
{
    private readonly string _settingsFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public LocalSettingsService() : this(GetDefaultSettingsPath()) { }

    public LocalSettingsService(string settingsFilePath)
    {
        _settingsFilePath = settingsFilePath;
    }

    private static string GetDefaultSettingsPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "MailVault");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public LocalSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return CreateDefaultSettings();
            }

            string json = File.ReadAllText(_settingsFilePath);
            var settings = JsonSerializer.Deserialize<LocalSettings>(json, JsonOptions);
            return settings ?? CreateDefaultSettings();
        }
        catch
        {
            return CreateDefaultSettings();
        }
    }

    public void Save(LocalSettings settings)
    {
        try
        {
            string dir = Path.GetDirectoryName(_settingsFilePath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // Graceful degradation
        }
    }

    private LocalSettings CreateDefaultSettings()
    {
        string currentDir = Directory.GetCurrentDirectory();
        return new LocalSettings
        {
            DefaultCaseFolder = Path.Combine(currentDir, "mailvault-cases"),
            DefaultCorpusPath = Path.Combine(currentDir, ".local-corpus"),
            DarkTheme = true,
            AdvancedMode = false
        };
    }
}

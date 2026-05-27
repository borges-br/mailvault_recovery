using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace MailVault.Desktop.Services;

public sealed class DesktopFileDialogService : IDesktopFileDialogService
{
    private Window? GetMainWindow()
    {
        return (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    }

    public async Task<string?> OpenEvidenceFileAsync()
    {
        var window = GetMainWindow();
        if (window == null) return null;

        var options = new FilePickerOpenOptions
        {
            Title = "Selecionar Mídia de Origem (.ost/.pst)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Outlook Data Files")
                {
                    Patterns = new[] { "*.ost", "*.pst" }
                }
            }
        };

        var result = await window.StorageProvider.OpenFilePickerAsync(options);
        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }

    public async Task<string?> OpenFolderAsync()
    {
        var window = GetMainWindow();
        if (window == null) return null;

        var options = new FolderPickerOpenOptions
        {
            Title = "Selecionar Pasta",
            AllowMultiple = false
        };

        var result = await window.StorageProvider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }

    public async Task<string?> OpenCaseDatabaseAsync()
    {
        var window = GetMainWindow();
        if (window == null) return null;

        var options = new FilePickerOpenOptions
        {
            Title = "Selecionar Banco de Dados do Caso (case.db)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQLite Database")
                {
                    Patterns = new[] { "case.db" }
                }
            }
        };

        var result = await window.StorageProvider.OpenFilePickerAsync(options);
        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }

    public Task OpenFolderInExplorerAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // Safe bypass for OS missing handler errors
        }

        return Task.CompletedTask;
    }

    public Task RevealFileInExplorerAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Task.CompletedTask;
        }

        try
        {
            string arguments = $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            });
        }
        catch
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir != null)
            {
                _ = OpenFolderInExplorerAsync(dir);
            }
        }

        return Task.CompletedTask;
    }
}

using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public sealed class AuditManifestViewModel : ViewModelBase
{
    private string _caseFolderPath = "";
    private string _auditLogContent = "Nenhum caso ativo ou audit.log não encontrado.";
    private string _manifestContent = "Nenhum caso ativo ou manifest.json não encontrado.";
    private string _exportManifestContent = "Nenhuma exportação ativa ou export-manifest.json não encontrado.";
    private string _validationReportContent = "Nenhum relatório de validação disponível.";
    private string _searchText = "";
    private string _statusText = "Selecione um arquivo para inspecionar.";

    public string AuditLogContent
    {
        get => _auditLogContent;
        private set => this.RaiseAndSetIfChanged(ref _auditLogContent, value);
    }

    public string ManifestContent
    {
        get => _manifestContent;
        private set => this.RaiseAndSetIfChanged(ref _manifestContent, value);
    }

    public string ExportManifestContent
    {
        get => _exportManifestContent;
        private set => this.RaiseAndSetIfChanged(ref _exportManifestContent, value);
    }

    public string ValidationReportContent
    {
        get => _validationReportContent;
        private set => this.RaiseAndSetIfChanged(ref _validationReportContent, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public ICommand CopyAuditCommand { get; }
    public ICommand CopyManifestCommand { get; }
    public ICommand RefreshCommand { get; }

    public AuditManifestViewModel()
    {
        CopyAuditCommand = ReactiveCommand.Create(CopyAuditToClipboard);
        CopyManifestCommand = ReactiveCommand.Create(CopyManifestToClipboard);
        RefreshCommand = ReactiveCommand.Create(RefreshData);
    }

    public void SetCaseFolder(string caseFolderPath)
    {
        _caseFolderPath = caseFolderPath;
        RefreshData();
    }

    public void ClearData()
    {
        _caseFolderPath = "";
        AuditLogContent = "Nenhum caso ativo ou audit.log não encontrado.";
        ManifestContent = "Nenhum caso ativo ou manifest.json não encontrado.";
        ExportManifestContent = "Nenhuma exportação ativa ou export-manifest.json não encontrado.";
        ValidationReportContent = "Nenhum relatório de validação disponível.";
        StatusText = "Selecione um arquivo para inspecionar.";
    }

    private void RefreshData()
    {
        if (string.IsNullOrWhiteSpace(_caseFolderPath) || !Directory.Exists(_caseFolderPath))
        {
            ClearData();
            return;
        }

        try
        {
            // 1. Audit Log
            string auditPath = Path.Combine(_caseFolderPath, "audit.log");
            if (File.Exists(auditPath))
            {
                string raw = File.ReadAllText(auditPath);
                AuditLogContent = MaskSensitive(raw);
            }
            else
            {
                AuditLogContent = "Arquivo audit.log não encontrado na pasta do caso.";
            }

            // 2. Manifest
            string manifestPath = Path.Combine(_caseFolderPath, "manifest.json");
            if (File.Exists(manifestPath))
            {
                string raw = File.ReadAllText(manifestPath);
                ManifestContent = MaskSensitive(raw);
            }
            else
            {
                ManifestContent = "Arquivo manifest.json não encontrado.";
            }

            // 3. Export Manifest
            string exportPath = Path.Combine(_caseFolderPath, "exports", "export-manifest.json");
            if (File.Exists(exportPath))
            {
                string raw = File.ReadAllText(exportPath);
                ExportManifestContent = MaskSensitive(raw);
            }
            else
            {
                ExportManifestContent = "export-manifest.json ausente (nenhuma exportação concluída nesta pasta).";
            }

            // 4. Validation Report
            string validationPath = Path.Combine(_caseFolderPath, "validation-report.json");
            if (File.Exists(validationPath))
            {
                string raw = File.ReadAllText(validationPath);
                ValidationReportContent = MaskSensitive(raw);
            }
            else
            {
                ValidationReportContent = "Nenhum validation-report.json encontrado nesta pasta do caso.";
            }

            StatusText = $"Dados carregados com sucesso em {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText = $"Erro ao ler arquivos de auditoria: {ex.Message}";
        }
    }

    private void CopyAuditToClipboard()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            desktop.MainWindow.Clipboard?.SetTextAsync(AuditLogContent);
            StatusText = "Conteúdo do audit.log copiado para a área de transferência.";
        }
    }

    private void CopyManifestToClipboard()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            desktop.MainWindow.Clipboard?.SetTextAsync(ManifestContent);
            StatusText = "Conteúdo do manifest.json copiado para a área de transferência.";
        }
    }

    private static string MaskSensitive(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        return Regex.Replace(input, @"(?i)([a-z]:\\users\\)[^\\]+", "$1<USER>");
    }
}

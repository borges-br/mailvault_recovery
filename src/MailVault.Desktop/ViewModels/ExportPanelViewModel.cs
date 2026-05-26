using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class ExportPanelViewModel : ViewModelBase
{
    private string _exportFormat = "eml";
    private string _exportPath = "";
    private string _exportStatus = "Selecione o formato e pasta de destino para iniciar a exportação forense.";
    private bool _includeAttachments = true;

    public string ExportFormat
    {
        get => _exportFormat;
        set => this.RaiseAndSetIfChanged(ref _exportFormat, value);
    }

    public string ExportPath
    {
        get => _exportPath;
        set => this.RaiseAndSetIfChanged(ref _exportPath, value);
    }

    public string ExportStatus
    {
        get => _exportStatus;
        set => this.RaiseAndSetIfChanged(ref _exportStatus, value);
    }

    public bool IncludeAttachments
    {
        get => _includeAttachments;
        set => this.RaiseAndSetIfChanged(ref _includeAttachments, value);
    }

    public ICommand RunExportCommand { get; }

    public ExportPanelViewModel()
    {
        RunExportCommand = ReactiveCommand.Create(OnRunExport);
    }

    private void OnRunExport()
    {
        if (string.IsNullOrEmpty(ExportPath))
        {
            ExportStatus = "Erro: Por favor, especifique uma pasta de destino válida.";
            return;
        }

        ExportStatus = "Exportação agendada. A execução da exportação dinâmica via UI está sob integração com os serviços do Core. Use o CLI 'mailvault export' no terminal.";
    }
}

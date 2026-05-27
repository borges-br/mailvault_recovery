using System.Threading.Tasks;

namespace MailVault.Desktop.Services;

public interface IDesktopFileDialogService
{
    Task<string?> OpenEvidenceFileAsync();
    Task<string?> OpenFolderAsync();
    Task<string?> OpenCaseDatabaseAsync();
    Task OpenFolderInExplorerAsync(string path);
    Task RevealFileInExplorerAsync(string path);
}

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MailVault.Desktop.ViewModels;

namespace MailVault.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            var qr = vm.QuickRecoveryViewModel;

            qr.BrowseForFile = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Selecionar arquivo de e-mail",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Arquivos de E-mail") { Patterns = new[] { "*.ost", "*.pst", "*.mbox", "*.eml" } },
                        new FilePickerFileType("Todos os Arquivos") { Patterns = new[] { "*" } }
                    }
                });
                return files.Count > 0 ? files[0].Path.LocalPath : null;
            };

            qr.BrowseForFolder = async () =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Selecionar pasta de destino"
                });
                return folders.Count > 0 ? folders[0].Path.LocalPath : null;
            };
        }
    }
}

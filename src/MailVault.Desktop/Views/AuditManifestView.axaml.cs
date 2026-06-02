using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MailVault.Desktop.Views;

public partial class AuditManifestView : UserControl
{
    public AuditManifestView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

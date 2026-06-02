using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MailVault.Desktop.Views;

public partial class ExportPanelView : UserControl
{
    public ExportPanelView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

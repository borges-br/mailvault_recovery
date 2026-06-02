using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MailVault.Desktop.Views;

public partial class CaseOverviewView : UserControl
{
    public CaseOverviewView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

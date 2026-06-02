using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MailVault.Desktop.Views;

public partial class QuickRecoveryView : UserControl
{
    public QuickRecoveryView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MailVault.Desktop.Views;

public partial class MessagePreviewView : UserControl
{
    public MessagePreviewView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

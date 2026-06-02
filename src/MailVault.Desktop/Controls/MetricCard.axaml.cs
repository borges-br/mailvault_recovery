using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace MailVault.Desktop.Controls;

public partial class MetricCard : UserControl
{
    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<MetricCard, string?>(nameof(Glyph), "•");

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<MetricCard, string?>(nameof(Value), "0");

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<MetricCard, string?>(nameof(Label), "");

    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<MetricCard, IBrush?>(nameof(Accent), Brushes.Cyan);

    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public MetricCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

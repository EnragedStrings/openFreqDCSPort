using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace OpenFreq.Client.Controls;

public partial class AudioChannelSelector : UserControl
{
    public static readonly StyledProperty<int> PanProperty =
        AvaloniaProperty.Register<AudioChannelSelector, int>(
            nameof(Pan),
            defaultValue: 0,
            defaultBindingMode: BindingMode.TwoWay);

    public int Pan
    {
        get => GetValue(PanProperty);
        set => SetValue(PanProperty, value);
    }

    public AudioChannelSelector()
    {
        InitializeComponent();
    }
}

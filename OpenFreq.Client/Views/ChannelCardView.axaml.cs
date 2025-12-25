using Avalonia.Controls;
using Avalonia.Input;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Views;

public partial class ChannelCardView : UserControl
{
    public ChannelCardView()
    {
        InitializeComponent();
    }

    private void OnPttStart(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ChannelCardViewModel vm && !vm.IsEditing)
            vm.StartTransmission();
    }

    private void OnPttStop(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is ChannelCardViewModel vm && !vm.IsEditing)
            vm.StopTransmission();
    }
}
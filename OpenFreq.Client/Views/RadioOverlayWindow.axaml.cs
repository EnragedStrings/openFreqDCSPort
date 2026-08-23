using Avalonia.Controls;
using Avalonia.Input;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Views;

/// <summary>Always-on-top, chromeless heads-up window showing the user's currently tuned radios
/// and TX/RX activity -- see RadioOverlayViewModel for the live data projection and
/// MainWindowViewModel for show/hide/position-persistence. Has no title bar
/// (SystemDecorations="None"), so dragging the body via OnBorderPointerPressed is the only way to
/// reposition it.</summary>
public partial class RadioOverlayWindow : Window
{
    public RadioOverlayWindow()
    {
        InitializeComponent();
    }

    public RadioOverlayWindow(RadioOverlayViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnBorderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}

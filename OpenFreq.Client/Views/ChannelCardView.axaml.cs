using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Views;

public partial class ChannelCardView : UserControl
{
    private ChannelCardViewModel? _trackedVm;

    public ChannelCardView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_trackedVm != null)
            _trackedVm.PropertyChanged -= OnVmPropertyChanged;
        _trackedVm = DataContext as ChannelCardViewModel;
        if (_trackedVm != null)
            _trackedVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelCardViewModel.IsEditing)
            && DataContext is ChannelCardViewModel { IsEditing: true })
        {
            Dispatcher.UIThread.Post(() =>
        {
            FrequencyInput.Focus();
            FrequencyInput.SelectAll();
        }, DispatcherPriority.Loaded);
        }
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ChannelCardViewModel vm && !vm.IsEditing)
            vm.Select();
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

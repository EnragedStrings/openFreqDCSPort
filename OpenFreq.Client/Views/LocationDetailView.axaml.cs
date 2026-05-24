using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Views;

public partial class LocationDetailView : UserControl
{
    private LocationViewModel? _trackedVm;

    public LocationDetailView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_trackedVm != null)
            _trackedVm.PropertyChanged -= OnVmPropertyChanged;
        _trackedVm = DataContext as LocationViewModel;
        if (_trackedVm != null)
            _trackedVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocationViewModel.EditMode)
            && DataContext is LocationViewModel { EditMode: true })
        {
            Dispatcher.UIThread.Post(() =>
            {
                NameInput.Focus();
                NameInput.SelectAll();
            }, DispatcherPriority.Loaded);
        }
    }
}

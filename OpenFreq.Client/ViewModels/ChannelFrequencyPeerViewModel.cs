using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenFreqClient.ViewModels;

public partial class ChannelFrequencyPeerViewModel : ViewModelBase
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FrequencyMhzString))] public partial int FrequencyKhz { get; set; }
    [ObservableProperty] public partial ObservableCollection<ChannelPeerViewModel> Peers { get; set; }
    [ObservableProperty] public partial bool CanJoin { get; set; }

    public IRelayCommand JoinCommand { get; }

    public ChannelFrequencyPeerViewModel(int frequencyKhz, ObservableCollection<ChannelPeerViewModel> peers, Action<int> joinFrequency, bool canJoin)
    {
        FrequencyKhz = frequencyKhz;
        Peers = peers;
        CanJoin = canJoin;
        JoinCommand = new RelayCommand(() => joinFrequency(frequencyKhz));
    }

    public string FrequencyMhzString
    {
        get => (FrequencyKhz / 1000d).ToString("F3", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var mhz))
            {
                FrequencyKhz = (int)(mhz * 1000d);
            }
        }
    }


}

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenFreqClient.Models;

namespace OpenFreqClient.Converters;

public class ChannelStatusToColorConverter: IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Channel.ChannelStatus status) return new SolidColorBrush(Color.FromRgb(60, 60, 60));
        var app = Application.Current;
        if (app?.Resources == null)
            return GetFallbackBrush(status);
            
        return status switch
        {
            Channel.ChannelStatus.Disconnected => GetThemeColor(app, "ChannelStatusDisconnectedBrush"),
            Channel.ChannelStatus.Connected => GetThemeColor(app, "ChannelStatusConnectedBrush"),
            Channel.ChannelStatus.Receiving => GetThemeColor(app, "ChannelStatusReceivingBrush"),
            Channel.ChannelStatus.Transmitting => GetThemeColor(app, "ChannelStatusTransmittingBrush"),
            _ => Brushes.Gray
        };

    }

    private static IBrush? GetThemeColor(Application app, string resourceKey)
    {
        if (app.Resources.TryGetResource(resourceKey, null, out var resource))
        {
            return resource as IBrush;
        }
        return null;
    }

    private static IBrush GetFallbackBrush(Channel.ChannelStatus status)
    {
        return status switch
        {
            Channel.ChannelStatus.Disconnected => new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            Channel.ChannelStatus.Connected => new SolidColorBrush(Color.FromRgb(33, 150, 243)),
            Channel.ChannelStatus.Receiving => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
            Channel.ChannelStatus.Transmitting => new SolidColorBrush(Color.FromRgb(244, 67, 54)),
            _ => new SolidColorBrush(Color.FromRgb(60, 60, 60))
        };
    }
    
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
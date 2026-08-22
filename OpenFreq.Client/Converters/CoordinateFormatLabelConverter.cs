using System;
using System.Globalization;
using Avalonia.Data.Converters;
using OpenFreq.Utilities;

namespace OpenFreqClient.Converters;

public class CoordinateFormatLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CoordinateFormat.DecimalDegrees => "Decimal Degrees",
        CoordinateFormat.LatLongStandard => "Lat/Long Standard (DMS)",
        CoordinateFormat.Precise => "Precise (DMS.ss)",
        CoordinateFormat.DecimalMinutes => "Decimal Minutes (DDM)",
        CoordinateFormat.Mgrs => "MGRS Grid",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

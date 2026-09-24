using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace GameShare.Client.Views;

/// <summary>A game's picture as the agent serves it, .ico or .png bytes, as an image. Anything that does not decode shows nothing.</summary>
public sealed class IconConverter : IValueConverter
{
    public static readonly IconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception) { return null; }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

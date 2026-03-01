using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace CommunicationManager.Converters;

/// <summary>
/// Converts a temp file path (extracted from BLOB) to an ImageSource.
/// The TempPath property of MediaItem is expected to be set before binding.
/// </summary>
public class FilePathToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
            return null;

        string? path = value as string;
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            // Path should be an absolute path to a temp file
            if (!File.Exists(path))
            {
                System.Diagnostics.Debug.WriteLine($"Image not found: {path}");
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading image: {ex.Message}");
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a list of MediaItems to an ImageSource for the first image.
/// Uses the TempPath property which should be populated via ExtractMediaToTemp.
/// </summary>
public class FirstMediaPathConverter : IValueConverter
{
    private static readonly FilePathToImageSourceConverter _imageConverter = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IList<CommunicationManager.Models.MediaItem> mediaList || mediaList.Count == 0)
            return null;

        var firstMedia = mediaList[0];
        if (firstMedia?.TempPath == null)
            return null;

        return _imageConverter.Convert(firstMedia.TempPath, targetType, parameter, culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

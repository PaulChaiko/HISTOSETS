using System.IO;
using System.Windows.Media.Imaging;
using HistOSets.Core;

namespace HistOSets.Services;

public sealed record LoadedImage(BitmapSource Bitmap, int PixelWidth, int PixelHeight, double DpiX, double DpiY);

public static class ImageLoader
{
    public static LoadedImage Load(string path)
    {
        // The native view is a bounded preview; its Canvas uses source pixels.
        int width, height;
        double dpiX, dpiY;
        using (var stream = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            dpiX = ImageCoordinates.EffectiveDpi(frame.DpiX);
            dpiY = ImageCoordinates.EffectiveDpi(frame.DpiY);
        }
        if (width <= 0 || height <= 0) throw new InvalidDataException("Изображение имеет недопустимые размеры.");
        using var source = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = source;
        if (Math.Max(width, height) > 4096)
            image.DecodePixelWidth = Math.Max(1, (int)Math.Round(width * 4096d / Math.Max(width, height)));
        image.EndInit();
        image.Freeze();
        return new(image, width, height, dpiX, dpiY);
    }
}

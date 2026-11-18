using System.IO;
using System.Windows.Media.Imaging;
using HistOSets.Core;
using HistOSets.Storage;

namespace HistOSets.Services;

public sealed record LoadedImage(BitmapSource Bitmap, int PixelWidth, int PixelHeight, double DpiX, double DpiY);

public static class ImageLoader
{
    public static ImageMetadata ReadMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadMetadata(stream);
    }

    public static ImageMetadata ReadMetadata(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Многостраничные и анимированные изображения пока не поддерживаются.");
        var frame = decoder.Frames[0];
        var orientation = 1;
        if (frame.Metadata is BitmapMetadata metadata)
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is { } value)
                    orientation = Convert.ToInt32(value);
        return new(frame.PixelWidth, frame.PixelHeight, ImageCoordinates.EffectiveDpi(frame.DpiX),
            ImageCoordinates.EffectiveDpi(frame.DpiY), decoder.CodecInfo.MimeTypes.Split(',')[0].Trim(), orientation);
    }

    public static LoadedImage Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static LoadedImage Load(Stream source, int maxDimension = 4096)
    {
        // Decode a bounded preview; regions keep using original pixel coordinates.
        var metadata = ReadMetadata(source);
        if (metadata.Width <= 0 || metadata.Height <= 0) throw new InvalidDataException("Изображение имеет недопустимые размеры.");
        source.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = source;
        if (Math.Max(metadata.Width, metadata.Height) > maxDimension)
        {
            if (metadata.Width >= metadata.Height) image.DecodePixelWidth = maxDimension;
            else image.DecodePixelHeight = maxDimension;
        }
        image.EndInit();
        image.Freeze();
        return new(image, metadata.Width, metadata.Height, metadata.DpiX, metadata.DpiY);
    }

    public static LoadedImage Load(CatalogStore store, Guid imageId, int maxDimension = 4096)
    {
        using var stream = store.OpenImage(imageId);
        return Load(stream, maxDimension);
    }
}

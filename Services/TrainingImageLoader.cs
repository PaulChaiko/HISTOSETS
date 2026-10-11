using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using HistOSets.Core;

namespace HistOSets.Services;

public enum TrainingImageError { Unavailable, Changed, InvalidImage }

/// <summary>A learner-facing media failure; its message never includes source paths or card names.</summary>
public sealed class TrainingImageLoadException : Exception
{
    public TrainingImageError Error { get; }

    internal TrainingImageLoadException(TrainingImageError error) : base(MessageFor(error)) => Error = error;

    private static string MessageFor(TrainingImageError error) => error switch
    {
        TrainingImageError.Changed => UiLanguage.T(
            "Изображение изменилось после подготовки занятия. Сформируйте новый план.",
            "The image changed after the session was prepared. Prepare a new plan."),
        TrainingImageError.InvalidImage => UiLanguage.T(
            "Изображение не соответствует подготовленному заданию или не может быть показано.",
            "The image does not match the prepared question or cannot be displayed."),
        _ => UiLanguage.T("Изображение задания недоступно.", "The question image is unavailable.")
    };
}

/// <summary>
/// Verifies and decodes one opened original. It can run on a worker thread; only a frozen,
/// bounded preview and the original pixel dimensions cross back to the user interface.
/// </summary>
public static class TrainingImageLoader
{
    public const int MaximumPreviewDimension = 4096;

    public static LoadedImage Load(TrainingImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.IsUsable || string.IsNullOrWhiteSpace(image.ImagePath))
            throw new TrainingImageLoadException(TrainingImageError.Unavailable);
        if (image.Width <= 0 || image.Height <= 0 || image.Sha256 is not { Length: 64 } ||
            !image.Sha256.All(Uri.IsHexDigit))
            throw new TrainingImageLoadException(TrainingImageError.InvalidImage);

        try
        {
            // Deny replacement/writes on Windows for the entire verification and decode. Never
            // reopen the path: the preview and both hashes must refer to this exact file handle.
            using var source = new FileStream(image.ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
            var length = source.Length;
            var before = SHA256.HashData(source);
            if (!string.Equals(Convert.ToHexString(before), image.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new TrainingImageLoadException(TrainingImageError.Changed);

            source.Position = 0;
            var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            if (decoder.Frames.Count != 1)
                throw new TrainingImageLoadException(TrainingImageError.InvalidImage);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != image.Width || frame.PixelHeight != image.Height || ReadOrientation(frame) != 1)
                throw new TrainingImageLoadException(TrainingImageError.InvalidImage);
            var dpiX = ImageCoordinates.EffectiveDpi(frame.DpiX);
            var dpiY = ImageCoordinates.EffectiveDpi(frame.DpiY);

            source.Position = 0;
            var preview = new BitmapImage();
            preview.BeginInit();
            preview.CacheOption = BitmapCacheOption.OnLoad;
            preview.StreamSource = source;
            // Bound the dominant dimension directly: rounding a very narrow portrait's width
            // can otherwise produce a decoded height much larger than the intended limit.
            if (Math.Max(image.Width, image.Height) > MaximumPreviewDimension)
            {
                if (image.Width >= image.Height) preview.DecodePixelWidth = MaximumPreviewDimension;
                else preview.DecodePixelHeight = MaximumPreviewDimension;
            }
            preview.EndInit();
            if (preview.PixelWidth <= 0 || preview.PixelHeight <= 0 ||
                preview.PixelWidth > MaximumPreviewDimension || preview.PixelHeight > MaximumPreviewDimension)
                throw new TrainingImageLoadException(TrainingImageError.InvalidImage);
            preview.Freeze();

            source.Position = 0;
            var after = SHA256.HashData(source);
            if (source.Length != length || !CryptographicOperations.FixedTimeEquals(before, after))
                throw new TrainingImageLoadException(TrainingImageError.Changed);
            return new(preview, image.Width, image.Height, dpiX, dpiY);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new TrainingImageLoadException(TrainingImageError.Unavailable);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or NotSupportedException or
            InvalidOperationException or CryptographicException or OverflowException or ExternalException)
        {
            throw new TrainingImageLoadException(TrainingImageError.InvalidImage);
        }
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        var orientation = 1;
        if (frame.Metadata is BitmapMetadata metadata)
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is { } value)
                    orientation = Convert.ToInt32(value);
        return orientation;
    }
}

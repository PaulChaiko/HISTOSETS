using System.Security.Cryptography;
using System.Text;

namespace HistOSets.Storage;

public sealed record ImageMetadata(int Width, int Height, double DpiX, double DpiY, string MimeType, int Orientation = 1);
public sealed record CatalogStatistics(int Materials, int Images, int Translations, int Elements, int Regions);
public sealed record ImportResult(bool AlreadyImported, CatalogStatistics Statistics);
public sealed class CatalogStorageException(string message, Exception? inner = null) : Exception(message, inner);

internal static class StorageFiles
{
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // RFC 9562 UUIDv5: a project-specific namespace and a reproducible source key.
    public static string Id(string key)
    {
        var space = Guid.Parse("17d1248b-69bb-5dcb-9899-5c6e5277c703").ToByteArray(bigEndian: true);
        var bytes = SHA1.HashData(space.Concat(Encoding.UTF8.GetBytes(key)).ToArray())[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    public static string Resolve(string root, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\\') || key.Contains(':') || key.StartsWith('/') ||
            key.Split('/').Any(p => p is "" or "." or ".."))
            throw new CatalogStorageException("Недопустимый путь файла в каталоге: " + key);
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new CatalogStorageException("Путь выходит за пределы каталога.");
        // Managed catalogs never use symbolic links or junctions for their assets.
        for (var current = fullPath; current.Length >= fullRoot.Length; current = Path.GetDirectoryName(current)!)
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new CatalogStorageException("Ссылки на внешние файлы не поддерживаются: " + key);
        return fullPath;
    }

    public static string UtcNow() => DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}

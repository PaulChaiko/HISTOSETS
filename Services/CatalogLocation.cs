using System.IO;

namespace HistOSets.Services;

internal static class CatalogLocation
{
    private static string HomeDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HISTOSETS");
    private static string SettingsPath => Path.Combine(HomeDirectory, "catalog-path.txt");
    internal static string Load()
    {
        if (File.Exists(SettingsPath))
        {
            var value = File.ReadAllText(SettingsPath).Trim();
            if (Path.IsPathFullyQualified(value)) return value;
        }
        return Path.Combine(HomeDirectory, "Catalog");
    }

    internal static void Save(string directory)
    {
        Directory.CreateDirectory(HomeDirectory);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, Path.GetFullPath(directory));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

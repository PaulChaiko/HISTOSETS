using System.IO;

namespace HistOSets.Services;

internal static class CatalogLocation
{
    private static string HomeDirectory => UiLanguage.SettingsDirectory;
    private static string SettingsPath => Path.Combine(HomeDirectory, "catalog-path.txt");
    internal static string DefaultDirectory => Path.Combine(HomeDirectory, "Catalog");
    internal static string? LoadSaved()
    {
        if (File.Exists(SettingsPath))
        {
            var value = File.ReadAllText(SettingsPath).Trim();
            if (Path.IsPathFullyQualified(value)) return value;
        }
        return null;
    }

    internal static void Save(string path)
    {
        Directory.CreateDirectory(HomeDirectory);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, Path.GetFullPath(path));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

using System.Globalization;
using System.IO;

namespace HistOSets.Services;

/// <summary>User interface preference, kept outside the shared atlas catalog.</summary>
internal static partial class UiLanguage
{
    internal static string SettingsDirectory =>
        Environment.GetEnvironmentVariable("HISTOSETS_SETTINGS_DIRECTORY") is { Length: > 0 } isolated
            ? Path.GetFullPath(isolated)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HISTOSETS");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "language.txt");
    private static string locale = Load();
    internal static string Locale => locale;
    internal static event EventHandler? Changed;

    static UiLanguage() => ApplyCulture();

    internal static string T(string russian, string english) => locale == "en" ? english : russian;

    internal static void Set(string value)
    {
        if (value is not ("ru" or "en")) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == locale) return;
        Directory.CreateDirectory(SettingsDirectory);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, value);
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        locale = value;
        ApplyCulture();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static string Load()
    {
        try
        {
            if (File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Trim() == "en") return "en";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "ru";
    }

    private static void ApplyCulture()
    {
        var culture = CultureInfo.GetCultureInfo(locale == "en" ? "en-US" : "ru-RU");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}

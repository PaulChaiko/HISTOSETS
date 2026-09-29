using System.IO;

namespace HistOSets.Services;

public static class ErrorLog
{
    public static string? Write(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HISTOSETS", "Logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyy-MM-dd}.log");
            File.AppendAllText(path, $"{DateTime.UtcNow:O}\n{exception}\n\n");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

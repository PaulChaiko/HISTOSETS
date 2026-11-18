using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

public sealed partial class CatalogStore
{
    private sealed record BackupFile(string Path, string Sha256, long Size);
    private sealed record BackupManifest(int FormatVersion, string CreatedUtc, List<BackupFile> Files);
    private const long MaxRestoreBytes = 64L * 1024 * 1024 * 1024;

    public void CreateBackup(string destination)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CopyCatalog(DatabasePath, destination);
        }
    }

    public static void CopyCatalog(string sourcePath, string destination)
    {
        PublishFile(destination, partial =>
        {
            using (var source = Open(Path.GetFullPath(sourcePath)))
            using (var target = Open(partial, SqliteOpenMode.ReadWriteCreate))
                source.BackupDatabase(target);
            using var snapshot = Open(partial, SqliteOpenMode.ReadWrite);
            Execute(snapshot, "PRAGMA journal_mode=DELETE;");
            Validate(snapshot, deep: true);
            VerifyImageContents(snapshot, hash: true);
        });
    }

    private static void ExtractLegacyBackup(string archivePath, string stage)
    {
        try
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                if (archive.Entries.Count > 100_001 || archive.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
                    throw new CatalogStorageException("Архив содержит слишком много файлов либо повторяющиеся пути.");
                var manifestEntry = archive.GetEntry("manifest.json") ?? throw new CatalogStorageException("В архиве нет manifest.json.");
                if (manifestEntry.Length > 16 * 1024 * 1024) throw new CatalogStorageException("Слишком большой манифест архива.");
                BackupManifest manifest;
                using (var stream = manifestEntry.Open())
                    manifest = JsonSerializer.Deserialize<BackupManifest>(stream) ?? throw new CatalogStorageException("Пустой манифест архива.");
                if (manifest.FormatVersion != 1 || manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Count > 100_000 ||
                    manifest.Files.Any(f => f is null || f.Size <= 0 || f.Size > MaxRestoreBytes || string.IsNullOrWhiteSpace(f.Path) ||
                        f.Sha256 is null || f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)) ||
                    manifest.Files.Sum(f => f.Size) > MaxRestoreBytes ||
                    manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count ||
                    manifest.Files.Count(f => f.Path == "catalog.sqlite") != 1 || archive.Entries.Count != manifest.Files.Count + 1)
                    throw new CatalogStorageException("Неподдерживаемая или повреждённая структура резервной копии (предел 64 ГиБ).");
                foreach (var file in manifest.Files)
                {
                    if (file.Path != "catalog.sqlite" && !file.Path.StartsWith("SPECIMENS/", StringComparison.Ordinal) && !file.Path.StartsWith("SOURCES/", StringComparison.Ordinal))
                        throw new CatalogStorageException("Неизвестный тип файла в архиве: " + file.Path);
                    // XML is obsolete and not required to recover an already imported catalog.
                    if (file.Path.StartsWith("SOURCES/", StringComparison.Ordinal)) continue;
                    var path = StorageFiles.Resolve(stage, file.Path);
                    var entry = archive.GetEntry(file.Path) ?? throw new CatalogStorageException("В архиве отсутствует файл: " + file.Path);
                    if (entry.Length != file.Size) throw new CatalogStorageException("Неверный размер файла: " + file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var input = entry.Open();
                    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    CopyVerified(input, output, file);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or SqliteException)
        {
            throw new CatalogStorageException("Старый архив повреждён или имеет неподдерживаемый формат.", ex);
        }
    }

    private static void CopyVerified(Stream input, Stream output, BackupFile file)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > file.Size) throw new CatalogStorageException("Файл превышает указанный размер: " + file.Path);
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        if (total != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
            throw new CatalogStorageException("Не совпадает контрольная сумма файла: " + file.Path);
    }
}

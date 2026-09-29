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
            destination = Path.GetFullPath(destination);
            if (File.Exists(destination)) throw new CatalogStorageException("Файл уже существует. Выберите новое имя резервной копии.");
            var stage = Path.Combine(Path.GetTempPath(), "histosets-backup-" + Guid.NewGuid().ToString("N"));
            var temporaryZip = destination + ".partial-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stage);
            try
            {
                var snapshot = Path.Combine(stage, "catalog.sqlite");
                using (var source = Open(DatabasePath))
                {
                    Validate(source);
                    using var target = Open(snapshot, SqliteOpenMode.ReadWriteCreate);
                    source.BackupDatabase(target);
                }
                List<BackupFile> files;
                using (var connection = Open(snapshot))
                {
                    Validate(connection);
                    files = ReferencedFiles(connection, RootDirectory);
                }
                files.Insert(0, new("catalog.sqlite", StorageFiles.Hash(snapshot), new FileInfo(snapshot).Length));
                using (var archive = ZipFile.Open(temporaryZip, ZipArchiveMode.Create))
                {
                    foreach (var file in files)
                    {
                        var path = file.Path == "catalog.sqlite" ? snapshot : StorageFiles.Resolve(RootDirectory, file.Path);
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var output = archive.CreateEntry(file.Path, CompressionLevel.Fastest).Open();
                        CopyVerified(input, output, file);
                    }
                    using var manifest = archive.CreateEntry("manifest.json").Open();
                    JsonSerializer.Serialize(manifest, new BackupManifest(1, StorageFiles.UtcNow(), files));
                }
                File.Move(temporaryZip, destination);
            }
            finally
            {
                Directory.Delete(stage, true);
                if (File.Exists(temporaryZip)) File.Delete(temporaryZip);
            }
        }
    }

    /// <summary>Restore into a new or empty directory. The current catalog is never replaced.</summary>
    public static void RestoreBackup(string archivePath, string targetDirectory)
    {
        targetDirectory = Path.GetFullPath(targetDirectory);
        RequireEmpty(targetDirectory);
        var parent = Path.GetDirectoryName(targetDirectory) ?? throw new CatalogStorageException("Выберите отдельную папку каталога.");
        var stage = Path.Combine(parent, ".histosets-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
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
                    var path = StorageFiles.Resolve(stage, file.Path);
                    var entry = archive.GetEntry(file.Path) ?? throw new CatalogStorageException("В архиве отсутствует файл: " + file.Path);
                    if (entry.Length != file.Size) throw new CatalogStorageException("Неверный размер файла: " + file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var input = entry.Open();
                    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    CopyVerified(input, output, file);
                }
                using var connection = Open(Path.Combine(stage, "catalog.sqlite"));
                Validate(connection);
                var referenced = ReferencedFiles(connection, stage);
                if (referenced.Count + 1 != manifest.Files.Count || referenced.Any(f => !manifest.Files.Contains(f)))
                    throw new CatalogStorageException("Файлы архива не соответствуют ссылкам в базе.");
            }
            RequireEmpty(targetDirectory);
            if (Directory.Exists(targetDirectory)) Directory.Delete(targetDirectory, recursive: false);
            Directory.Move(stage, targetDirectory);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or SqliteException)
        {
            throw new CatalogStorageException("Резервная копия повреждена или имеет неподдерживаемый формат. Текущий каталог не изменён.", ex);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private static void RequireEmpty(string path)
    {
        if (File.Exists(path) || (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()))
            throw new CatalogStorageException("Для восстановления нужна новая или пустая папка. Существующий каталог остаётся на месте.");
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new CatalogStorageException("Выберите обычную папку, а не ссылку или соединение каталогов.");
    }

    private static List<BackupFile> ReferencedFiles(SqliteConnection connection, string root)
    {
        var result = new List<BackupFile>();
        using var command = Command(connection, """
            SELECT storage_key, sha256, byte_size FROM images
            UNION ALL SELECT source_key, xml_sha256, NULL FROM import_batches;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (!key.StartsWith("SPECIMENS/", StringComparison.Ordinal) && !key.StartsWith("SOURCES/", StringComparison.Ordinal))
                throw new CatalogStorageException("Неизвестный путь файла в базе.");
            var path = StorageFiles.Resolve(root, key);
            var hash = reader.GetString(1);
            if (!File.Exists(path) || StorageFiles.Hash(path) != hash || (!reader.IsDBNull(2) && new FileInfo(path).Length != reader.GetInt64(2)))
                throw new CatalogStorageException("Отсутствует или повреждён файл: " + key);
            result.Add(new(key, hash, new FileInfo(path).Length));
        }
        return result;
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

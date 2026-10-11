using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

/// <summary>A validated, immutable transport graph with privately staged original image bytes.</summary>
public sealed class ExchangePackage : IDisposable
{
    internal const long MaxBytes = 64L * 1024 * 1024 * 1024;
    internal const long MaxGraphBytes = 128L * 1024 * 1024;
    internal const int MaxRows = 500_000;
    internal const int MaxFiles = 100_000;
    private bool disposed;
    private readonly string stage;
    private readonly IReadOnlyList<ExchangeFile> files;
    public int FormatVersion { get; }
    public Guid PackageId { get; }
    public Guid SourceCatalogId { get; }
    public string SourceCatalogName { get; }
    public long SourceContentRevision { get; }
    public string CreatedUtc { get; }
    public string ContentSha256 { get; }
    public IReadOnlyList<ExchangeMaterialInfo> Materials { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> Tables { get; }
    internal IReadOnlyDictionary<string, string> ImagePaths { get; }

    private ExchangePackage(ExchangeManifest manifest, string staging,
        IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> tables)
    {
        FormatVersion = manifest.FormatVersion;
        PackageId = Guid.Parse(manifest.PackageId); SourceCatalogId = Guid.Parse(manifest.SourceCatalogId);
        SourceCatalogName = manifest.SourceCatalogName; SourceContentRevision = manifest.SourceContentRevision;
        CreatedUtc = manifest.CreatedUtc; ContentSha256 = manifest.Files.Single(f => f.Path == "graph.json").Sha256;
        stage = staging; files = Array.AsReadOnly(manifest.Files.ToArray()); Tables = tables;
        Materials = Array.AsReadOnly(ExchangeSchema.MaterialInfo(tables, "ru").ToArray());
        ImagePaths = new ReadOnlyDictionary<string, string>(files.Where(f => f.Path != "graph.json")
            .ToDictionary(f => f.Path, f => StorageFiles.Resolve(stage, f.Path), StringComparer.Ordinal));
    }

    public static ExchangePackage Open(string archivePath)
    {
        var stage = Path.Combine(Path.GetTempPath(), "histosets-exchange-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count is < 2 or > MaxFiles + 2 || archive.Entries.Select(e => e.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
                throw Invalid("Повторяющиеся пути или превышен предел числа файлов.");
            var manifestEntry = archive.GetEntry("manifest.json") ?? throw Invalid("Отсутствует manifest.json.");
            if (manifestEntry.Length is <= 0 or > 16 * 1024 * 1024) throw Invalid("Недопустимый размер manifest.json.");
            ExchangeManifest manifest;
            using (var stream = manifestEntry.Open())
            using (var document = ReadJson(stream, manifestEntry.Length)) manifest = ReadManifest(document.RootElement);
            if (archive.Entries.Count != manifest.Files.Count + 1) throw Invalid("Есть незаявленные файлы.");
            foreach (var file in manifest.Files)
            {
                var entry = archive.GetEntry(file.Path) ?? throw Invalid("Отсутствует заявленный файл.");
                if (entry.Length != file.Size) throw Invalid("Размер файла не совпадает с манифестом.");
                var path = StorageFiles.Resolve(stage, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = entry.Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyVerified(input, output, file);
            }
            IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> tables;
            using (var stream = File.OpenRead(Path.Combine(stage, "graph.json")))
            using (var document = ReadJson(stream, stream.Length)) tables = ReadTables(document.RootElement, manifest.FormatVersion);
            tables = ValidateGraph(tables, manifest, stage);
            return new(manifest, stage, tables);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or SqliteException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        {
            Cleanup(stage);
            throw Invalid("Файл повреждён или имеет неподдерживаемую структуру.", ex);
        }
        catch { Cleanup(stage); throw; }
    }

    internal void VerifyFiles()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var file in files)
        {
            var path = StorageFiles.Resolve(stage, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size || StorageFiles.Hash(path) != file.Sha256)
                throw Invalid("Данные предварительного просмотра изменились. Откройте пакет заново.");
        }
    }

    /// <summary>Desktop callers validate original pixels with their supported image decoder before preview.</summary>
    public void ValidateImages(Func<string, ImageMetadata> readMetadata)
    {
        ArgumentNullException.ThrowIfNull(readMetadata);
        VerifyFiles();
        foreach (var row in Tables["images"])
        {
            var decoded = readMetadata(ImagePaths[row.String("storage_key")!]);
            if (decoded.Width != row.Integer("pixel_width") || decoded.Height != row.Integer("pixel_height") ||
                decoded.Orientation != 1 || decoded.MimeType != row.String("mime_type") ||
                HistOSets.Core.ImageCoordinates.EffectiveDpi(decoded.DpiX) != Convert.ToDouble(row.Values["dpi_x"], CultureInfo.InvariantCulture) ||
                HistOSets.Core.ImageCoordinates.EffectiveDpi(decoded.DpiY) != Convert.ToDouble(row.Values["dpi_y"], CultureInfo.InvariantCulture))
                throw Invalid("Параметры исходного изображения не совпадают с описанием. Контуры могут сместиться.");
        }
        VerifyFiles();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Cleanup(stage);
    }

    private static void Cleanup(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal sealed record ExchangeFile(string Path, string Sha256, long Size);
    internal sealed record ExchangeManifest(int FormatVersion, string PackageId, string SourceCatalogId,
        string SourceCatalogName, long SourceContentRevision, string CreatedUtc, List<ExchangeFile> Files);

    private static ExchangeManifest ReadManifest(JsonElement value)
    {
        RequireProperties(value, ["FormatVersion", "PackageId", "SourceCatalogId", "SourceCatalogName", "SourceContentRevision", "CreatedUtc", "Files"]);
        var version = value.GetProperty("FormatVersion").GetInt32();
        var package = value.GetProperty("PackageId").GetString()!;
        var source = value.GetProperty("SourceCatalogId").GetString()!;
        var name = value.GetProperty("SourceCatalogName").GetString()!;
        var revision = value.GetProperty("SourceContentRevision").GetInt64();
        var created = value.GetProperty("CreatedUtc").GetString()!;
        if (version is not (1 or 2) || !IsUuid(package) || !IsUuid(source) || name is null || name.Length > 200 || revision < 0 || !IsTime(created))
            throw Invalid("Неподдерживаемая версия или неверные сведения о каталоге.");
        var fileList = value.GetProperty("Files");
        if (fileList.ValueKind != JsonValueKind.Array || fileList.GetArrayLength() is < 1 or > MaxFiles + 1)
            throw Invalid("Превышен предел числа файлов.");
        var files = new List<ExchangeFile>();
        long total = 0;
        foreach (var item in fileList.EnumerateArray())
        {
            RequireProperties(item, ["Path", "Sha256", "Size"]);
            var path = item.GetProperty("Path").GetString()!;
            var hash = item.GetProperty("Sha256").GetString()!;
            var size = item.GetProperty("Size").GetInt64();
            if (path is null || (path != "graph.json" && !IsImageKey(path)) || !IsHash(hash) ||
                size <= 0 || size > MaxBytes || (path == "graph.json" && size > MaxGraphBytes) || size > MaxBytes - total)
                throw Invalid("Недопустимый путь, размер или контрольная сумма файла (предел 64 ГиБ).");
            total += size;
            files.Add(new(path, hash, size));
        }
        if (files.Count(f => f.Path == "graph.json") != 1 || files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw Invalid("Отсутствует граф объектов или повторяются пути.");
        return new(version, package, source, name, revision, created, files);
    }

    internal static void RequireProperties(JsonElement value, IReadOnlyCollection<string> allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Ожидался объект JSON.");
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != allowed.Count || names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !allowed.Contains(n)))
            throw Invalid("Неизвестные, отсутствующие или повторяющиеся поля JSON.");
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> ReadTables(JsonElement graph, int formatVersion)
    {
        var transmittedTables = formatVersion == 1 ? ExchangeSchema.LegacyTableNames : ExchangeSchema.TableNames;
        RequireProperties(graph, transmittedTables.ToArray());
        var tables = new Dictionary<string, IReadOnlyList<ExchangeRow>>(StringComparer.Ordinal);
        var count = 0;
        foreach (var name in ExchangeSchema.TableNames)
        {
            if (!transmittedTables.Contains(name))
            {
                tables.Add(name, Array.Empty<ExchangeRow>());
                continue;
            }
            var array = graph.GetProperty(name);
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxRows - count) throw Invalid("Превышен предел числа записей.");
            count += array.GetArrayLength();
            var rows = new List<ExchangeRow>();
            foreach (var value in array.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object) throw Invalid("Неверная запись графа.");
                var cells = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var cell in value.EnumerateObject())
                {
                    object? scalar = cell.Value.ValueKind switch {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => cell.Value.GetString(),
                        JsonValueKind.Number => cell.Value.TryGetInt64(out var number) ? (object)number : cell.Value.GetDouble(),
                        _ => throw Invalid("В таблице допустимы только скалярные значения.")
                    };
                    if (scalar is double real && !double.IsFinite(real)) throw Invalid("Нечисловая величина.");
                    if (!cells.TryAdd(cell.Name, scalar)) throw Invalid("Повторяющийся реквизит записи.");
                }
                rows.Add(new(cells));
            }
            tables.Add(name, rows.AsReadOnly());
        }
        if (tables["materials"].Count == 0) throw Invalid("В пакете нет выбранных объектов.");
        return new ReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>>(tables);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> ValidateGraph(IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> tables, ExchangeManifest manifest, string stage)
    {
        // The database and SQL come only from this executable, never from an archive entry.
        var validationPath = Path.Combine(stage, "validation.sqlite");
        Create(validationPath, "Exchange validation");
        using (var connection = CatalogDatabase.Open(validationPath, SqliteOpenMode.ReadWrite))
        using (var transaction = connection.BeginTransaction())
        {
            Execute(connection, "DELETE FROM tag_group_translations; DELETE FROM tag_groups;", transaction);
            foreach (var table in ExchangeSchema.TableNames)
            {
                var columns = new Dictionary<string, (string Type, bool NotNull)>(StringComparer.Ordinal);
                using (var command = Command(connection, $"PRAGMA table_info({table});", transaction))
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) columns.Add(reader.GetString(1), (reader.GetString(2), reader.GetBoolean(3)));
                foreach (var row in tables[table])
                {
                    if (row.Values.Count != columns.Count || row.Values.Keys.Any(k => !columns.ContainsKey(k)))
                        throw Invalid("Неизвестные или отсутствующие реквизиты таблицы.");
                    foreach (var (column, scalar) in row.Values)
                    {
                        var type = columns[column];
                        if (scalar is null && type.NotNull || scalar is not null && type.Type switch {
                            "TEXT" => scalar is not string,
                            "INTEGER" => scalar is not long,
                            "REAL" => scalar is not (double or long),
                            _ => true
                        }) throw Invalid("Тип реквизита не соответствует схеме.");
                        if ((column == "id" || column.EndsWith("_id", StringComparison.Ordinal)) && scalar is not null && !IsUuid(scalar as string))
                            throw Invalid("Неверный UUID записи.");
                        if (column.EndsWith("_utc", StringComparison.Ordinal) && scalar is not null && !IsTime(scalar as string))
                            throw Invalid("Неверное время записи.");
                    }
                    if (row.Values["source_batch_id"] is not null) throw Invalid("Исходные XML не входят в обмен выбранными объектами.");
                    var names = columns.Keys.ToArray();
                    Execute(connection, $"INSERT INTO {table} ({string.Join(',', names)}) VALUES ({string.Join(',', names.Select((_, n) => "$p" + n))});", transaction,
                        names.Select((column, n) => ("$p" + n, row.Values[column])).ToArray());
                }
            }
            Validate(connection, transaction: transaction);
            ValidateReachability(connection, transaction);
            ValidateCalibrationSources(connection, transaction);
            // Readiness is part of the portable content contract, independent of target state.
            foreach (var material in tables["materials"].Where(r => r.String("deleted_utc") is null && r.String("status") == "ready"))
            {
                var issues = CatalogStore.ExchangeReadinessIssues(connection, material.Id, transaction);
                if (issues.Count > 0) throw Invalid("Готовый объект не удовлетворяет требованиям полноты.");
            }
            // JSON does not distinguish 96 from 96.0. Read trusted SQLite affinities
            // back so REAL metadata and INTEGER revisions have stable CLR types.
            tables = new ReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>>(ExchangeSchema.TableNames.ToDictionary(
                table => table, table => CatalogStore.ReadExchangeRows(connection, table, transaction: transaction), StringComparer.Ordinal));
        }
        File.Delete(validationPath);
        var imageFiles = manifest.Files.Where(f => f.Path != "graph.json").ToDictionary(f => f.Path, StringComparer.Ordinal);
        if (imageFiles.Count != tables["images"].Count) throw Invalid("Состав изображений не соответствует графу.");
        foreach (var row in tables["images"])
        {
            var key = row.String("storage_key")!;
            if (!IsImageKey(key) || !IsHash(row.String("sha256")) || !imageFiles.TryGetValue(key, out var file) ||
                file.Sha256 != row.String("sha256") || file.Size != row.Integer("byte_size"))
                throw Invalid("Изображение не соответствует манифесту.");
            var extension = row.String("mime_type") switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/bmp" => ".bmp", "image/tiff" => ".tif", "image/gif" => ".gif", _ => null };
            if (extension is null || key != "SPECIMENS/" + file.Sha256 + extension ||
                row.Integer("pixel_width") > int.MaxValue || row.Integer("pixel_height") > int.MaxValue)
                throw Invalid("Неподдерживаемый формат или размеры изображения.");
        }
        return tables;
    }

    private static void ValidateCalibrationSources(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, """
            SELECT c.pixel_size_x_m,c.pixel_size_y_m,c.input_width,c.input_height,c.input_unit,i.pixel_width,i.pixel_height
            FROM image_calibrations c JOIN material_images mi ON mi.id=c.material_image_id JOIN images i ON i.id=mi.image_id
            WHERE c.input_width IS NOT NULL;
            """, transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var expected = HistOSets.Core.ImageCalibration.FromDimensions(reader.GetDouble(2), reader.GetDouble(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6));
            static bool SameScale(double actual, double calculated) => Math.Abs(actual - calculated) <= Math.Abs(calculated) * 1e-12;
            if (!SameScale(reader.GetDouble(0), expected.PixelSizeXMetres) || !SameScale(reader.GetDouble(1), expected.PixelSizeYMetres))
                throw Invalid("Калибровка не соответствует физическим размерам исходного изображения.");
        }
    }

    private static void ValidateReachability(SqliteConnection connection, SqliteTransaction transaction)
    {
        // No unrelated catalog rows may hide inside a selected-material package.
        foreach (var sql in new[] {
            "SELECT COUNT(*) FROM images i WHERE NOT EXISTS(SELECT 1 FROM material_images mi WHERE mi.image_id=i.id)",
            "SELECT COUNT(*) FROM tags t WHERE NOT EXISTS(SELECT 1 FROM material_tags mt WHERE mt.tag_id=t.id)",
            "SELECT COUNT(*) FROM tag_groups g WHERE g.is_system=0 AND NOT EXISTS(SELECT 1 FROM tags t WHERE t.group_id=g.id)",
            "SELECT COUNT(*) FROM analog_groups g WHERE NOT EXISTS(SELECT 1 FROM materials m WHERE m.analog_group_id=g.id)"
        }) if (Convert.ToInt64(Scalar(connection, sql, transaction)) != 0) throw Invalid("В пакет включены посторонние записи.");
        foreach (var row in new[] {
            "SELECT COUNT(*) FROM regions r JOIN elements e ON e.id=r.element_id JOIN material_images mi ON mi.id=r.material_image_id WHERE r.deleted_utc IS NULL AND (e.deleted_utc IS NOT NULL OR mi.deleted_utc IS NOT NULL)",
            "SELECT COUNT(*) FROM material_images mi JOIN images i ON i.id=mi.image_id WHERE mi.deleted_utc IS NULL AND i.deleted_utc IS NOT NULL",
            "SELECT COUNT(*) FROM image_calibrations c JOIN material_images mi ON mi.id=c.material_image_id WHERE c.deleted_utc IS NULL AND mi.deleted_utc IS NOT NULL",
            "SELECT COUNT(*) FROM element_translations t JOIN elements e ON e.id=t.element_id WHERE t.deleted_utc IS NULL AND e.deleted_utc IS NOT NULL",
            "SELECT COUNT(*) FROM training_prompts p JOIN elements e ON e.id=p.element_id WHERE p.deleted_utc IS NULL AND e.deleted_utc IS NOT NULL",
            "SELECT COUNT(*) FROM training_prompt_translations t JOIN training_prompts p ON p.id=t.prompt_id WHERE t.deleted_utc IS NULL AND p.deleted_utc IS NOT NULL",
            "SELECT COUNT(*) FROM material_tags mt JOIN tags t ON t.id=mt.tag_id WHERE mt.deleted_utc IS NULL AND t.deleted_utc IS NOT NULL",
            "SELECT COUNT(*) FROM tags t JOIN tag_groups g ON g.id=t.group_id WHERE t.deleted_utc IS NULL AND g.deleted_utc IS NOT NULL",
            "SELECT COUNT(*) FROM tag_translations t JOIN tags p ON p.id=t.tag_id WHERE t.deleted_utc IS NULL AND p.deleted_utc IS NOT NULL",
            "SELECT COUNT(*) FROM tag_group_translations t JOIN tag_groups p ON p.id=t.group_id WHERE t.deleted_utc IS NULL AND p.deleted_utc IS NOT NULL",
            "SELECT COUNT(*) FROM analog_group_translations t JOIN analog_groups p ON p.id=t.group_id WHERE t.deleted_utc IS NULL AND p.deleted_utc IS NOT NULL",
            "SELECT COUNT(*) FROM materials m JOIN analog_groups g ON g.id=m.analog_group_id WHERE m.deleted_utc IS NULL AND g.deleted_utc IS NOT NULL"
        }) if (Convert.ToInt64(Scalar(connection, row, transaction)) != 0) throw Invalid("Действующая запись ссылается на удалённого владельца.");
        foreach (var table in new[] { "material_translations", "material_images", "elements", "regions", "material_tags", "training_prompts" })
            if (Convert.ToInt64(Scalar(connection, $"SELECT COUNT(*) FROM {table} c JOIN materials m ON m.id=c.material_id WHERE c.deleted_utc IS NULL AND m.deleted_utc IS NOT NULL;", transaction)) != 0)
                throw Invalid("Действующая запись ссылается на удалённого владельца.");
        (string Code, string Ru, string En)[] systemTypes = [
            ("author", "Автор", "Author"), ("organization", "Организация", "Organization"), ("subject", "Предметная область", "Subject"),
            ("topic", "Тема", "Topic"), ("purpose", "Назначение", "Purpose"), ("other", "Прочее", "Other")];
        foreach (var type in systemTypes)
        {
            var id = StorageFiles.Id("system-tag-type/" + type.Code);
            if (Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM tag_groups WHERE id=$id AND code=$code AND is_system=1 AND deleted_utc IS NULL;", transaction,
                ("$id", id), ("$code", type.Code))) != 1) throw Invalid("Повреждён системный тип тега.");
            foreach (var (locale, label) in new[] { ("ru", type.Ru), ("en", type.En) })
                if (Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM tag_group_translations WHERE id=$id AND group_id=$group AND locale=$locale AND name=$name AND deleted_utc IS NULL;", transaction,
                    ("$id", StorageFiles.Id("system-tag-type/" + type.Code + "/" + locale)), ("$group", id), ("$locale", locale), ("$name", label))) != 1)
                    throw Invalid("Повреждён перевод системного типа тега.");
        }
        if (Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM tag_groups WHERE is_system=1;", transaction)) != systemTypes.Length)
            throw Invalid("Неизвестный системный тип тега.");
    }

    internal static void CopyVerified(Stream input, Stream output, ExchangeFile file)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > file.Size) throw Invalid("Файл превышает заявленный размер.");
            hash.AppendData(buffer, 0, read); output.Write(buffer, 0, read);
        }
        if (total != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
            throw Invalid("Контрольная сумма файла не совпадает.");
    }

    private static JsonDocument ReadJson(Stream stream, long size)
    {
        using var bounded = new MemoryStream(checked((int)size));
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bounded.Length + read > size) throw Invalid("JSON превышает заявленный размер.");
            bounded.Write(buffer, 0, read);
        }
        if (bounded.Length != size) throw Invalid("Неполный JSON.");
        return JsonDocument.Parse(bounded.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static bool IsUuid(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString() == value;
    private static bool IsTime(string? value) => value is { Length: <= 64 } && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsImageKey(string key) => key.Length == 78 && key.StartsWith("SPECIMENS/", StringComparison.Ordinal) &&
        IsHash(key.Substring(10, 64)) && key[74..] is ".png" or ".jpg" or ".bmp" or ".tif" or ".gif";
    private static CatalogStorageException Invalid(string detail, Exception? inner = null) => new("Пакет обмена: " + detail, inner);
}

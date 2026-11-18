using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

/// <summary>A validated graph and a read-only SQLite snapshot of its original images.</summary>
public sealed class ExchangePackage : IDisposable
{
    internal const long MaxGraphBytes = 128L * 1024 * 1024;
    internal const int MaxRows = 500_000;
    private readonly SqliteConnection source;
    private readonly SqliteTransaction snapshot;
    private bool disposed;
    public Guid PackageId { get; }
    public Guid SourceCatalogId { get; }
    public string SourceCatalogName { get; }
    public long SourceContentRevision { get; }
    public string CreatedUtc { get; }
    public string ContentSha256 { get; }
    public IReadOnlyList<ExchangeMaterialInfo> Materials { get; }
    internal string SourcePath { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> Tables { get; }

    private ExchangePackage(string path, SqliteConnection source, SqliteTransaction snapshot, Guid catalogId,
        Guid sourceId, string sourceName, long sourceRevision, string createdUtc,
        IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> tables)
    {
        SourcePath = path;
        this.source = source;
        this.snapshot = snapshot;
        PackageId = catalogId;
        SourceCatalogId = sourceId;
        SourceCatalogName = sourceName;
        SourceContentRevision = sourceRevision;
        CreatedUtc = createdUtc;
        Tables = tables;
        Materials = Array.AsReadOnly(ExchangeSchema.MaterialInfo(tables, "ru").ToArray());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var table in ExchangeSchema.TableNames)
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(table));
            foreach (var row in tables[table]) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row.Values));
        }
        ContentSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static ExchangePackage Open(string path)
    {
        path = Path.GetFullPath(path);
        using (var file = File.OpenRead(path))
        {
            Span<byte> header = stackalloc byte[16];
            var count = file.ReadAtLeast(header, 2, false);
            if (count >= 2 && header[..2].SequenceEqual("PK"u8))
                throw Invalid(Path.GetExtension(path).Equals(".histosets-exchange", StringComparison.OrdinalIgnoreCase)
                    ? "Старый пакет обмена откройте в HISTOSETS 0.7.3, объедините с каталогом и перенесите каталог в новый формат."
                    : "Сначала перенесите этот резервный архив через «Перенести старый каталог…».");
            if (count < 16) count += file.ReadAtLeast(header[count..], 16 - count, false);
            if (count != 16 || !header.SequenceEqual("SQLite format 3\0"u8)) throw Invalid("Выберите файл каталога .histosets.");
        }

        var source = CatalogDatabase.Open(path);
        SqliteTransaction? snapshot = null;
        try
        {
            snapshot = source.BeginTransaction(deferred: true);
            if (Convert.ToInt64(Scalar(source, "PRAGMA application_id;", snapshot)) != CatalogDatabase.ApplicationId ||
                Convert.ToInt64(Scalar(source, "PRAGMA user_version;", snapshot)) != CatalogDatabase.Version)
                throw Invalid("Этот файл не является каталогом HISTOSETS текущего формата.");

            // Validate the projected rows against SQL and column lists created by HISTOSETS.
            // Only image streams come from the incoming catalog; its SQL is never copied.
            using var verifier = CatalogDatabase.Open(":memory:", SqliteOpenMode.ReadWriteCreate);
            Create(verifier, "Проверка обмена");
            using var validation = verifier.BeginTransaction();
            VerifySourceShape(source, snapshot, verifier, validation);
            Validate(source, transaction: snapshot, deep: true);
            var ids = new List<Guid>();
            using (var command = Command(source, "SELECT id FROM materials ORDER BY id;", snapshot))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
            if (ids.Count == 0) throw Invalid("В каталоге нет объектов для передачи.");
            if (ids.Count > 100_000) throw Invalid("В каталоге больше 100 000 объектов. Передайте их по частям.");
            var tables = CatalogStore.ReadExchangeSelection(source, ids, snapshot);
            if (tables["materials"].Count != ids.Count) throw Invalid("Повреждены идентификаторы объектов.");
            var (catalogId, id, name, revision, created) = CatalogStore.ReadExchangeOrigin(source, snapshot);
            InsertGraph(verifier, validation, tables);
            foreach (var row in tables["images"])
            {
                using var input = CatalogStore.ReadImageContent(source, snapshot, row.Id);
                if (input.Length != row.Integer("byte_size") || Convert.ToHexStringLower(SHA256.HashData(input)) != row.String("sha256"))
                    throw Invalid("Размер или контрольная сумма оригинала не совпадает: " + row.Id);
            }
            return new(path, source, snapshot, catalogId, id, name, revision, created, tables);
        }
        catch (Exception ex) when (ex is SqliteException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        {
            snapshot?.Dispose();
            source.Dispose();
            throw Invalid("Файл повреждён или имеет неподдерживаемую структуру.", ex);
        }
        catch
        {
            snapshot?.Dispose();
            source.Dispose();
            throw;
        }
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    internal Stream OpenImage(string imageId)
    {
        ThrowIfDisposed();
        return CatalogStore.ReadImageContent(source, snapshot, imageId);
    }

    /// <summary>The desktop decoder checks the originals against their scientific coordinate metadata.</summary>
    public void ValidateImages(Func<Stream, ImageMetadata> readMetadata)
    {
        ArgumentNullException.ThrowIfNull(readMetadata);
        ThrowIfDisposed();
        foreach (var row in Tables["images"])
        {
            using var input = OpenImage(row.Id);
            var decoded = readMetadata(input);
            static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= Math.Abs(expected) * 1e-9;
            if (decoded.Width != row.Integer("pixel_width") || decoded.Height != row.Integer("pixel_height") ||
                decoded.Orientation != 1 || decoded.MimeType != row.String("mime_type") ||
                !Near(HistOSets.Core.ImageCoordinates.EffectiveDpi(decoded.DpiX), Convert.ToDouble(row.Values["dpi_x"], CultureInfo.InvariantCulture)) ||
                !Near(HistOSets.Core.ImageCoordinates.EffectiveDpi(decoded.DpiY), Convert.ToDouble(row.Values["dpi_y"], CultureInfo.InvariantCulture)))
                throw Invalid("Параметры исходного изображения не совпадают с описанием. Контуры могут сместиться.");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { snapshot.Dispose(); }
        finally { source.Dispose(); }
    }

    private sealed record Column(string Name, string Type, bool NotNull, bool Hidden);

    private static Column[] ReadColumns(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        var columns = new List<Column>();
        using var command = Command(connection, $"PRAGMA table_xinfo({table});", transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(new(reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(6)));
        return columns.ToArray();
    }

    private static void VerifySourceShape(SqliteConnection source, SqliteTransaction snapshot,
        SqliteConnection verifier, SqliteTransaction validation)
    {
        long rows = 0;
        long bytes = 0;
        foreach (var table in ExchangeSchema.TableNames)
        {
            var columns = ReadColumns(verifier, validation, table);
            if (!Equals(Scalar(source, "SELECT type FROM sqlite_schema WHERE name=$table;", snapshot, ("$table", table)), "table") ||
                !columns.SequenceEqual(ReadColumns(source, snapshot, table)))
                throw Invalid("Структура таблицы не соответствует формату: " + table);
            rows += Convert.ToInt64(Scalar(source, $"SELECT COUNT(*) FROM {table};", snapshot));
            if (rows > MaxRows) throw Invalid("Слишком много записей для одного обмена. Передайте объекты по частям.");
            var size = string.Join(" + ", columns.Where(c => c.Type == "TEXT").Select(c => $"COALESCE(length(CAST({c.Name} AS BLOB)),0)"));
            bytes += Convert.ToInt64(Scalar(source, $"SELECT COALESCE(SUM({size}),0) FROM {table};", snapshot));
            if (bytes > MaxGraphBytes) throw Invalid("Объём описаний превышает 128 МиБ. Передайте объекты по частям.");
        }
    }

    internal static void InsertGraph(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> tables)
    {
        long rows = 0;
        long bytes = 0;
        Execute(connection, "DELETE FROM tag_group_translations; DELETE FROM tag_groups;", transaction);
        foreach (var table in ExchangeSchema.TableNames)
        {
            var columns = ReadColumns(connection, transaction, table);
            using var insert = Command(connection,
                $"INSERT INTO {table} ({string.Join(',', columns.Select(c => c.Name))}) VALUES ({string.Join(',', columns.Select((_, i) => "$p" + i))});", transaction);
            foreach (var (column, index) in columns.Select((column, index) => (column, index)))
                insert.Parameters.AddWithValue("$p" + index, DBNull.Value);
            foreach (var row in tables[table])
            {
                if (++rows > MaxRows || row.Values.Count != columns.Length)
                    throw Invalid("Слишком много записей или столбцы таблицы не соответствуют формату.");
                for (var i = 0; i < columns.Length; i++)
                {
                    var (name, type, notNull, _) = columns[i];
                    if (!row.Values.TryGetValue(name, out var value) || value is null && notNull || value is not null && type switch {
                        "TEXT" => value is not string,
                        "INTEGER" => value is not long,
                        "REAL" => value is not double real || !double.IsFinite(real),
                        _ => true
                    }) throw Invalid("Тип реквизита не соответствует формату.");
                    if (value is string str)
                    {
                        bytes += System.Text.Encoding.UTF8.GetByteCount(str);
                        if (bytes > MaxGraphBytes) throw Invalid("Объём описаний превышает 128 МиБ.");
                        if ((name == "id" || name.EndsWith("_id", StringComparison.Ordinal)) && !IsUuid(str))
                            throw Invalid("Неверный UUID записи.");
                        if (name == "source_batch_id") throw Invalid("Запись содержит ссылку на пакет импорта.");
                        if (name.EndsWith("_utc", StringComparison.Ordinal) && !IsTime(str))
                            throw Invalid("Неверное время записи.");
                    }
                    insert.Parameters[i].Value = value ?? DBNull.Value;
                }
                _ = insert.ExecuteNonQuery();
            }
        }
        ValidateReachability(connection, transaction);
        ValidateCalibrationSources(connection, transaction);
        foreach (var material in tables["materials"].Where(r => r.String("deleted_utc") is null && r.String("status") == "ready"))
            if (CatalogStore.ExchangeReadinessIssues(connection, material.Id, transaction).Count > 0)
                throw Invalid("Готовый объект не удовлетворяет требованиям полноты.");
        foreach (var row in tables["images"])
        {
            var extension = row.String("mime_type") switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/bmp" => ".bmp", "image/tiff" => ".tif", "image/gif" => ".gif", _ => null };
            var hash = row.String("sha256");
            if (extension is null || !IsHash(hash) || row.String("storage_key") != "SPECIMENS/" + hash + extension ||
                row.Integer("pixel_width") > int.MaxValue || row.Integer("pixel_height") > int.MaxValue)
                throw Invalid("Неподдерживаемый формат или размеры изображения.");
        }
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
        // The projected catalog must contain exactly the records reachable from the selected cards.
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
        if (Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM tag_groups;", transaction)) != systemTypes.Length)
            throw Invalid("Неизвестный системный тип тега.");
    }


    internal static bool IsUuid(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString() == value;
    internal static bool IsTime(string? value) => value is { Length: <= 64 } && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static CatalogStorageException Invalid(string detail, Exception? inner = null) => new("Обмен каталогами: " + detail, inner);
}

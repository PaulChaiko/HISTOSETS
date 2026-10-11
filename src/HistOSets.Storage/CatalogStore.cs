using HistOSets.Core;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

/// <summary>One writable application owner per catalog. Connections never remain pooled across operations.</summary>
public sealed partial class CatalogStore : IDisposable
{
    private readonly FileStream ownership;
    private readonly object gate = new();
    private bool disposed;
    public string RootDirectory { get; }
    public string DatabasePath => Path.Combine(RootDirectory, "catalog.sqlite");
    public string? LastMigrationBackupPath { get; private set; }

    public CatalogStore(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RootDirectory);
        if (!File.Exists(DatabasePath) && Directory.EnumerateFileSystemEntries(RootDirectory).Any())
            throw new CatalogStorageException("В непустой папке отсутствует catalog.sqlite. Автоматическое создание отменено, чтобы не заменить утраченный каталог. Восстановите копию или выберите пустую папку.");
        try
        {
            ownership = new FileStream(Path.Combine(RootDirectory, "catalog.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new CatalogStorageException("Каталог занят другим экземпляром HISTOSETS либо недоступен для записи.", ex);
        }
        try
        {
            if (!File.Exists(DatabasePath))
            {
                // A database is installed only after the full schema transaction succeeds.
                var temporary = DatabasePath + ".creating-" + Guid.NewGuid().ToString("N");
                try { Create(temporary, DefaultCatalogName(RootDirectory)); File.Move(temporary, DatabasePath); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            int version;
            using (var connection = Open(DatabasePath))
            {
                Validate(connection, allowLegacy: true);
                version = SchemaVersion(connection);
            }
            if (version < CatalogDatabase.Version)
            {
                var backups = Path.Combine(RootDirectory, "BACKUPS");
                Directory.CreateDirectory(backups);
                var path = Path.Combine(backups, "before-schema4-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".histosets");
                // This snapshot must contain every referenced file before the first database write.
                CreateBackup(path);
                LastMigrationBackupPath = path;
                try
                {
                    using var connection = Open(DatabasePath, SqliteOpenMode.ReadWrite);
                    using var transaction = connection.BeginTransaction();
                    if (version == 1) ApplyVersion2(connection, transaction, DefaultCatalogName(RootDirectory));
                    if (version < 3) ApplyVersion3(connection, transaction);
                    ApplyVersion4(connection, transaction);
                    Validate(connection, transaction: transaction);
                    transaction.Commit();
                }
                catch (Exception ex) when (ex is SqliteException or CatalogStorageException or ArgumentException or FormatException)
                {
                    throw new CatalogStorageException("Не удалось обновить структуру каталога. Резервная копия: " + path, ex);
                }
            }
        }
        catch { ownership.Dispose(); throw; }
    }

    public CatalogStatistics GetStatistics()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var connection = Open(DatabasePath);
            return Statistics(connection);
        }
    }

    private static CatalogStatistics Statistics(SqliteConnection connection)
    {
        int Count(string table) => Convert.ToInt32(Scalar(connection, $"SELECT COUNT(*) FROM {table} WHERE deleted_utc IS NULL;"));
        return new(Count("materials"), Count("images"), Count("material_translations"), Count("elements"), Count("regions"));
    }

    public AtlasCatalog Load()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var connection = Open(DatabasePath);
            Validate(connection);
            var warnings = new List<string>();
            var assets = new Dictionary<string, (string Path, string? Issue)>();
            using (var command = Command(connection, "SELECT id, storage_key, sha256, byte_size FROM images WHERE deleted_utc IS NULL;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var path = StorageFiles.Resolve(RootDirectory, reader.GetString(1));
                    string? issue = null;
                    if (!File.Exists(path)) issue = "Не найдено изображение «" + reader.GetString(1) + "». Восстановите резервную копию.";
                    else if (new FileInfo(path).Length != reader.GetInt64(3) || StorageFiles.Hash(path) != reader.GetString(2))
                        issue = "Изображение изменено вне HISTOSETS. Показ остановлен, чтобы не сместить контуры: " + reader.GetString(1);
                    if (issue is not null) warnings.Add(issue);
                    assets.Add(reader.GetString(0), (path, issue));
                }
            var specimens = new List<AtlasSpecimen>();
            var analogNames = ReadNames(connection, "analog_group_translations", "group_id");
            using (var command = Command(connection, """
                SELECT t.material_id, t.locale, t.name, t.summary, t.description, mi.id, mi.image_id,
                       m.status, mi.is_primary, m.analog_group_id
                FROM material_translations t
                JOIN materials m ON m.id = t.material_id AND m.deleted_utc IS NULL
                JOIN material_images mi ON mi.material_id = m.id AND mi.deleted_utc IS NULL
                JOIN images i ON i.id = mi.image_id AND i.deleted_utc IS NULL
                WHERE t.deleted_utc IS NULL ORDER BY t.sort_order, t.id, mi.sort_order, mi.id;
                """))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var material = reader.GetString(0);
                    var locale = reader.GetString(1);
                    var elements = ReadElements(connection, material, locale, reader.GetString(5));
                    var asset = assets[reader.GetString(6)];
                    specimens.Add(new(reader.GetString(2), asset.Path, reader.GetString(3), reader.GetString(4), CoordinateSpace.Pixels, elements)
                    {
                        MaterialId = Guid.Parse(material), ImageId = Guid.Parse(reader.GetString(6)), Locale = locale, ImageIssue = asset.Issue,
                        MaterialImageId = Guid.Parse(reader.GetString(5)), Status = reader.GetString(7), IsPrimaryImage = reader.GetBoolean(8),
                        Calibration = ReadCalibration(connection, Guid.Parse(reader.GetString(5))),
                        AnalogGroupId = reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
                        AnalogGroupNames = reader.IsDBNull(9) ? new Dictionary<string,string>() : analogNames.GetValueOrDefault(reader.GetString(9)) ?? new Dictionary<string,string>()
                    });
                }
            return new(specimens, warnings);
        }
    }

    private static List<AtlasElement> ReadElements(SqliteConnection connection, string material, string locale, string materialImage)
    {
        var elements = new List<AtlasElement>();
        using var command = Command(connection, """
            SELECT e.id, t.name, t.summary, t.description FROM elements e
            JOIN element_translations t ON t.element_id = e.id AND t.locale = $locale AND t.deleted_utc IS NULL
            WHERE e.material_id = $material AND e.deleted_utc IS NULL ORDER BY e.sort_order, e.id;
            """, null, ("$material", material), ("$locale", locale));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var polygons = new List<AtlasPolygon>();
            var geometryRegions = new List<AtlasRegion>();
            using var regions = Command(connection, """
                SELECT id,geometry_json FROM regions WHERE element_id = $element AND material_image_id = $image
                AND deleted_utc IS NULL ORDER BY sort_order, id;
                """, null, ("$element", reader.GetString(0)), ("$image", materialImage));
            using var regionReader = regions.ExecuteReader();
            while (regionReader.Read())
            {
                var geometry = RegionGeometryCodec.Deserialize(regionReader.GetString(1));
                geometryRegions.Add(new(Guid.Parse(regionReader.GetString(0)),geometry));
                if (geometry.Kind != "compound") polygons.Add(RegionGeometryCodec.ToPolygon(geometry));
            }
            elements.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), polygons) { Id = Guid.Parse(reader.GetString(0)), Regions = geometryRegions });
        }
        return elements;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ownership.Dispose();
        }
    }
}

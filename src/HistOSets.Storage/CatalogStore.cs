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
    public string RootDirectory => Path.GetDirectoryName(DatabasePath)!;
    public string DatabasePath { get; }

    public CatalogStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        if (!File.Exists(DatabasePath))
            throw new CatalogStorageException("Файл каталога не найден. Выберите существующий .histosets или создайте новый.");
        var key = OperatingSystem.IsWindows() ? DatabasePath.ToUpperInvariant() : DatabasePath;
        var lockDirectory = Path.Combine(Path.GetTempPath(), "histosets-file-locks");
        Directory.CreateDirectory(lockDirectory);
        try
        {
            ownership = new FileStream(Path.Combine(lockDirectory, StorageFiles.Id(key)), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new CatalogStorageException("Каталог уже открыт другим экземпляром HISTOSETS либо занят.", ex);
        }
        try
        {
            using var connection = Open(DatabasePath);
            Validate(connection, deep: true);
            VerifyImageContents(connection);
        }
        catch { ownership.Dispose(); throw; }
    }

    public static void CreateCatalog(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new CatalogStorageException("Введите название каталога длиной от 1 до 200 символов.");
        PublishFile(path, partial => Create(partial, name.Trim()));
    }

    internal static void PublishFile(string destination, Action<string> write)
    {
        destination = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(destination), ".histosets", StringComparison.OrdinalIgnoreCase))
            throw new CatalogStorageException("Выберите имя файла с расширением .histosets.");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new CatalogStorageException("Этот путь уже занят. Выберите новое имя файла каталога.");
        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            write(partial);
            File.Move(partial, destination);
        }
        finally
        {
            foreach (var path in new[] { partial, partial + "-journal", partial + "-wal", partial + "-shm" })
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
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
            var assets = new Dictionary<string, (string Key, string? Issue)>();
            using (var command = Command(connection, """
                SELECT i.id,i.storage_key,i.byte_size,length(c.data) FROM images i
                LEFT JOIN image_contents c ON c.image_id=i.id WHERE i.deleted_utc IS NULL;
                """))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var key = reader.GetString(1);
                    var issue = reader.IsDBNull(3) || reader.GetInt64(3) != reader.GetInt64(2)
                        ? "Изображение отсутствует или повреждено: " + key : null;
                    if (issue is not null) warnings.Add(issue);
                    assets.Add(reader.GetString(0), (key, issue));
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
                    specimens.Add(new(reader.GetString(2), asset.Key, reader.GetString(3), reader.GetString(4), CoordinateSpace.Pixels, elements)
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

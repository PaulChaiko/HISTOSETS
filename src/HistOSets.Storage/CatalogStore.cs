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
                try { Create(temporary); File.Move(temporary, DatabasePath); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            using var connection = Open(DatabasePath);
            Validate(connection);
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
            using (var command = Command(connection, """
                SELECT t.material_id, t.locale, t.name, t.summary, t.description, mi.id, mi.image_id
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
                        MaterialId = Guid.Parse(material), ImageId = Guid.Parse(reader.GetString(6)), Locale = locale, ImageIssue = asset.Issue
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
            using var regions = Command(connection, """
                SELECT id FROM regions WHERE element_id = $element AND material_image_id = $image
                AND deleted_utc IS NULL ORDER BY sort_order, id;
                """, null, ("$element", reader.GetString(0)), ("$image", materialImage));
            using var regionReader = regions.ExecuteReader();
            while (regionReader.Read())
            {
                var points = new List<AtlasPoint>();
                using var pointCommand = Command(connection, "SELECT x,y FROM region_points WHERE region_id = $id ORDER BY ordinal;",
                    null, ("$id", regionReader.GetString(0)));
                using var pointReader = pointCommand.ExecuteReader();
                while (pointReader.Read()) points.Add(new(pointReader.GetDouble(0), pointReader.GetDouble(1)));
                polygons.Add(new(points));
            }
            elements.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), polygons) { Id = Guid.Parse(reader.GetString(0)) });
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

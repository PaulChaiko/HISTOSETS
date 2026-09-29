using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HistOSets.Core;
using HistOSets.Storage;
using Microsoft.Data.Sqlite;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    count++;
    Console.WriteLine("PASS " + name);
}
void Reject<T>(Action action, string name) where T : Exception
{
    try { action(); } catch (T) { Check(true, name); return; }
    throw new Exception("Expected " + typeof(T).Name + ": " + name);
}
string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}
SqliteConnection Connect(string path)
{
    var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());
    c.Open();
    return c;
}
object? Sql(string path, string sql)
{
    using var c = Connect(path);
    using var command = c.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar();
}
bool Equivalent(AtlasCatalog before, AtlasCatalog after, Func<string, ImageMetadata> metadata)
{
    if (before.Specimens.Count != after.Specimens.Count) return false;
    for (var s = 0; s < before.Specimens.Count; s++)
    {
        var a = before.Specimens[s]; var b = after.Specimens[s];
        if ((a.Name, a.Summary, a.Description, a.Elements.Count) != (b.Name, b.Summary, b.Description, b.Elements.Count) || b.CoordinateSpace != CoordinateSpace.Pixels || Hash(a.ImagePath) != Hash(b.ImagePath)) return false;
        var m = metadata(a.ImagePath);
        for (var e = 0; e < a.Elements.Count; e++)
        {
            var x = a.Elements[e]; var y = b.Elements[e];
            if ((x.Name, x.Summary, x.Description, x.Polygons.Count) != (y.Name, y.Summary, y.Description, y.Polygons.Count)) return false;
            for (var p = 0; p < x.Polygons.Count; p++)
                if (!x.Polygons[p].Points.Select(v => ImageCoordinates.ToPixels(v, a.CoordinateSpace, m.DpiX, m.DpiY)).SequenceEqual(y.Polygons[p].Points)) return false;
        }
    }
    return true;
}

var source = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var metadataByHash = JsonSerializer.Deserialize<Dictionary<string, ImageMetadata>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "baseline-images.json")))!;
ImageMetadata Metadata(string path) => metadataByHash[Hash(path)];
var original = AtlasLoader.Load(source);
var sourceXml = Path.Combine(source, "ATLAS", "ATLAS.xml");
var originalXmlHash = Hash(sourceXml);
var temp = Path.Combine(Path.GetTempPath(), "histosets-storage-" + Guid.NewGuid());
Directory.CreateDirectory(temp);
try
{
    var root = Path.Combine(temp, "Каталог ' с пробелом");
    var backup = Path.Combine(temp, "complete.histosets");
    AtlasCatalog imported;
    using (var store = new CatalogStore(root))
    {
        var result = store.ImportLegacy(sourceXml, source, Metadata);
        Check(!result.AlreadyImported && result.Statistics == new CatalogStatistics(5, 5, 10, 27, 33), "Baseline normalized to 5 materials / 5 images / 10 translations / 27 elements / 33 regions");
        imported = store.Load();
        Check(Equivalent(original, imported, Metadata), "Every description, name, image byte and vertex survives XML → SQLite");
        Check(imported.Specimens.Sum(s => s.Elements.Count) == 54 && imported.Specimens.SelectMany(s => s.Elements).Sum(e => e.Polygons.Count) == 66, "All localized views remain available");
        Check(imported.Specimens[1].MaterialId == imported.Specimens[6].MaterialId && imported.Specimens[1].Elements[0].Id == imported.Specimens[6].Elements[0].Id, "RU and EN share stable element and material IDs");
        Check(imported.Specimens.SelectMany(s => s.Elements).Count(e => e.Polygons.Count == 0) == 4, "Unmarked elements preserved");
        Check(imported.Specimens[0].Elements[0].Polygons[0].Points.Any(p => p.X == 1969), "Existing one-pixel overshoot is not silently corrected");
        var dbHash = Hash(store.DatabasePath);
        Check(store.ImportLegacy(sourceXml, source, Metadata).AlreadyImported && Hash(store.DatabasePath) == dbHash, "Identical import is a byte-for-byte database no-op");
        Reject<CatalogStorageException>(() => { using var duplicate = new CatalogStore(root); }, "Second application cannot own the same catalog");
        Check(Convert.ToInt64(Sql(store.DatabasePath, "SELECT COUNT(*) FROM schema_migrations;")) == 1, "Schema version recorded once");
        Check(Convert.ToInt64(Sql(store.DatabasePath, "PRAGMA foreign_keys;")) == 1, "Foreign keys enforced");
        Reject<SqliteException>(() => Sql(store.DatabasePath, "UPDATE regions SET material_id = (SELECT id FROM materials WHERE id <> regions.material_id LIMIT 1);"), "A region cannot cross material boundaries");
        var changed = Path.Combine(temp, "changed.xml");
        File.WriteAllText(changed, File.ReadAllText(sourceXml).Replace("<ATLAS>", "<ATLAS >"));
        Reject<CatalogStorageException>(() => store.ImportLegacy(changed, source, Metadata), "Changed XML does not overwrite an established catalog");
        Check(Hash(store.DatabasePath) == dbHash, "Rejected import leaves database unchanged");
        store.CreateBackup(backup);
        Check(File.Exists(backup) && new FileInfo(backup).Length > new FileInfo(store.DatabasePath).Length, "Backup includes image files as well as SQLite");
        Reject<CatalogStorageException>(() => store.CreateBackup(backup), "Existing backup is never silently overwritten");
        var asset = imported.Specimens[0].ImagePath;
        var bytes = File.ReadAllBytes(asset);
        File.WriteAllBytes(asset, bytes[..^1]);
        Check(store.Load().Specimens[0].ImageIssue is not null, "Modified image is flagged before stale contours can be displayed");
        Reject<CatalogStorageException>(() => store.CreateBackup(Path.Combine(temp, "damaged.histosets")), "Incomplete catalog cannot masquerade as a complete backup");
        File.WriteAllBytes(asset, bytes);
        File.Delete(asset);
        Check(store.Load().Specimens[0].ImageIssue is not null, "Missing image yields a recoverable catalog warning");
        File.WriteAllBytes(asset, bytes);
    }
    using (var reopened = new CatalogStore(root))
    {
        Check(Equivalent(original, reopened.Load(), Metadata), "Reopening uses persisted SQLite with pixel coordinates exactly once");
    }
    var restored = Path.Combine(temp, "Restored");
    CatalogStore.RestoreBackup(backup, restored);
    using (var store = new CatalogStore(restored))
    {
        Check(Equivalent(original, store.Load(), Metadata), "Full backup restores every field, contour and image to a new directory");
        Check(store.Load().Specimens.Select(s => s.MaterialId).SequenceEqual(imported.Specimens.Select(s => s.MaterialId)), "Restore preserves UUIDs");
        Check(store.ImportLegacy(sourceXml, source, Metadata).AlreadyImported, "Restored catalog recognizes its original import");
    }
    Reject<CatalogStorageException>(() => CatalogStore.RestoreBackup(backup, restored), "Restore refuses an occupied destination");
    var isolated = Path.Combine(temp, "Isolated");
    Directory.Move(restored, isolated);
    using (var store = new CatalogStore(isolated)) Check(Equivalent(original, store.Load(), Metadata), "Moved catalog needs no original XML or original image folder");

    var newer = Path.Combine(temp, "Future");
    CatalogStore.RestoreBackup(backup, newer);
    var futureDb = Path.Combine(newer, "catalog.sqlite");
    Sql(futureDb, "PRAGMA user_version = 999;");
    var futureHash = Hash(futureDb);
    Reject<CatalogStorageException>(() => { using var invalid = new CatalogStore(newer); }, "Newer schema is rejected without downgrade");
    Check(Hash(futureDb) == futureHash, "Unsupported database bytes remain unchanged");

    var transactional = Path.Combine(temp, "Transaction");
    using (var store = new CatalogStore(transactional))
    {
        Sql(store.DatabasePath, "CREATE TRIGGER fail_import BEFORE INSERT ON material_translations BEGIN SELECT RAISE(ABORT, 'simulated disk-side failure'); END;");
        Reject<CatalogStorageException>(() => store.ImportLegacy(sourceXml, source, Metadata), "Import transaction handles failure after partial inserts");
        Check(store.GetStatistics() == new CatalogStatistics(0, 0, 0, 0, 0) && Convert.ToInt64(Sql(store.DatabasePath, "SELECT COUNT(*) FROM import_batches;")) == 0, "All partial database inserts roll back");
        Sql(store.DatabasePath, "DROP TRIGGER fail_import;");
        Check(!store.ImportLegacy(sourceXml, source, Metadata).AlreadyImported, "Import can retry after transaction rollback");
    }

    var malformed = Path.Combine(temp, "malformed.xml");
    File.WriteAllText(malformed, "<!DOCTYPE ATLAS [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><ATLAS>&x;</ATLAS>");
    using (var store = new CatalogStore(Path.Combine(temp, "Malformed")))
    {
        Reject<CatalogStorageException>(() => store.ImportLegacy(malformed, source, Metadata), "Unsafe XML rejected before any data are imported");
        File.WriteAllText(malformed, "<ATLAS><Specimen NAME='Lost' IMAGE='missing.jpg'/></ATLAS>");
        Reject<CatalogStorageException>(() => store.ImportLegacy(malformed, source, Metadata), "Missing original file aborts import, rather than dropping a record");
        Check(store.GetStatistics().Materials == 0, "Failed initial import leaves no partial material");
    }
    var custom = Path.Combine(temp, "custom.xml");
    var imageName = Path.GetFileName(original.Specimens[0].ImagePath);
    File.WriteAllText(custom, $"""
        <ATLAS>
          <Specimen NAME="Одинаковое ' имя" IMAGE="{imageName}" INFO1="Описание &amp; символы" INFO2="Полное описание">
            <ELEMENT NAME="Элемент" INFO1="Кратко" INFO2="Подробно"><POLYGON POINTS="0,0 10.5,0 10.5,20.25"/></ELEMENT>
          </Specimen>
          <Specimen NAME="Одинаковое ' имя" IMAGE="{imageName}" COORDINATES="pixels">
            <ELEMENT NAME="Другой"><POLYGON POINTS="0,0 10.5,0 10.5,20.25"/></ELEMENT>
          </Specimen>
        </ATLAS>
        """);
    using (var store = new CatalogStore(Path.Combine(temp, "Custom")))
    {
        var result = store.ImportLegacy(custom, source, p => Metadata(p) with { DpiX = 300, DpiY = 150 });
        var actual = store.Load();
        Check(result.Statistics == new CatalogStatistics(2, 1, 2, 2, 2) && actual.Specimens.All(s => s.Locale == "und"), "Unknown XML keeps equal-named records independent and shares only image bytes");
        Check(actual.Specimens[0].Elements[0].Polygons[0].Points[2] == new AtlasPoint(10.5 * 300 / 96, 20.25 * 150 / 96) &&
            actual.Specimens[1].Elements[0].Polygons[0].Points[2] == new AtlasPoint(10.5, 20.25), "Independent X/Y DPI conversion; explicit pixels remain pixels");
        Check(actual.Specimens[0].Summary == "Описание & символы" && actual.Specimens[0].Elements[0].Description == "Подробно", "Unicode, quotes and nonempty element descriptions survive parameterized SQL");
    }
    using (var store = new CatalogStore(Path.Combine(temp, "Rotated")))
        Reject<CatalogStorageException>(() => store.ImportLegacy(custom, source, p => Metadata(p) with { Orientation = 6 }), "Unsupported EXIF rotation cannot misalign migrated contours");

    var damagedZip = Path.Combine(temp, "broken.histosets");
    File.Copy(backup, damagedZip);
    using (var archive = ZipFile.Open(damagedZip, ZipArchiveMode.Update))
    {
        var entry = archive.Entries.First(e => e.FullName.StartsWith("SPECIMENS/"));
        var name = entry.FullName;
        entry.Delete();
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write("broken image");
    }
    var failedTarget = Path.Combine(temp, "FailedRestore");
    Reject<CatalogStorageException>(() => CatalogStore.RestoreBackup(damagedZip, failedTarget), "Damaged image archive fails validation");
    Check(!Directory.Exists(failedTarget), "Failed restore never exposes a partially restored catalog");
    var traversal = Path.Combine(temp, "traversal.histosets");
    using (var archive = ZipFile.Open(traversal, ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open())) writer.Write(JsonSerializer.Serialize(new
        {
            FormatVersion = 1, CreatedUtc = "now", Files = new[]
            {
                new { Path = "catalog.sqlite", Sha256 = Convert.ToHexStringLower(SHA256.HashData("x"u8)), Size = 1 },
                new { Path = "SOURCES/../../outside.xml", Sha256 = Convert.ToHexStringLower(SHA256.HashData("x"u8)), Size = 1 }
            }
        }));
        using (var writer = new StreamWriter(archive.CreateEntry("catalog.sqlite").Open())) writer.Write("x");
        using (var writer = new StreamWriter(archive.CreateEntry("SOURCES/../../outside.xml").Open())) writer.Write("x");
    }
    Reject<CatalogStorageException>(() => CatalogStore.RestoreBackup(traversal, failedTarget), "Untrusted archive cannot escape the restore directory");
    Check(!File.Exists(Path.Combine(temp, "outside.xml")), "No file written outside restore staging");
    Check(Hash(sourceXml) == originalXmlHash && original.Specimens.All(s => metadataByHash.ContainsKey(Hash(s.ImagePath))), "All original XML and scientific images left unchanged");
    Console.WriteLine($"{count} storage checks passed.");
}
finally { Directory.Delete(temp, true); }

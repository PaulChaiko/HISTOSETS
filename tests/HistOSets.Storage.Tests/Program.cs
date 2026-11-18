using System.Text.Json;
using HistOSets.Core;
using HistOSets.Storage;
using Microsoft.Data.Sqlite;
using static NativeTestFixtures;
using static FoundationChecks;

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

static bool Equivalent(AtlasCatalog before, AtlasCatalog after)
{
    if (before.Specimens.Count != after.Specimens.Count) return false;
    for (var i = 0; i < before.Specimens.Count; i++)
    {
        var a = before.Specimens[i]; var b = after.Specimens[i];
        if ((a.MaterialId, a.MaterialImageId, a.ImageId, a.Locale, a.Name, a.Summary, a.Description, a.CoordinateSpace, a.Calibration, a.Elements.Count)
            != (b.MaterialId, b.MaterialImageId, b.ImageId, b.Locale, b.Name, b.Summary, b.Description, b.CoordinateSpace, b.Calibration, b.Elements.Count)
            || b.ImageIssue is not null) return false;
        for (var j = 0; j < a.Elements.Count; j++)
        {
            var x = a.Elements[j]; var y = b.Elements[j];
            if ((x.Id, x.Name, x.Summary, x.Description) != (y.Id, y.Name, y.Summary, y.Description) ||
                !x.Regions.Select(r => (r.Id, RegionGeometryCodec.Serialize(r.Geometry)))
                    .SequenceEqual(y.Regions.Select(r => (r.Id, RegionGeometryCodec.Serialize(r.Geometry))))) return false;
        }
    }
    return true;
}

var repository = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var demo = Path.Combine(repository, "Demo.histosets");
var metadataByHash = JsonSerializer.Deserialize<Dictionary<string, ImageMetadata>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "baseline-images.json")))!;
ImageMetadata Metadata(string path) => metadataByHash[Hash(path)];
var temp = Path.Combine(Path.GetTempPath(), "histosets-storage-" + Guid.NewGuid());
Directory.CreateDirectory(temp);
try
{
    var path = Path.Combine(temp, "Каталог ' с пробелом.histosets");
    var backup = Path.Combine(temp, "complete.histosets");
    var source = Path.Combine(temp, "fixture-assets");
    var images = Path.Combine(source, "SPECIMENS");
    Directory.CreateDirectory(images);
    CatalogStore.CopyCatalog(demo, path);
    AtlasCatalog original;
    Dictionary<Guid, string> imageHashes = [];
    using (var store = new CatalogStore(path))
    {
        original = store.Load();
        Check(store.GetStatistics() == new CatalogStatistics(5, 5, 10, 27, 33), "Standalone demo contains 5 materials / 5 photos / 10 translations / 27 elements / 33 regions");
        Check(original.Specimens.Count == 10 && original.Specimens.Sum(s => s.Elements.Count) == 54 &&
            original.Specimens.SelectMany(s => s.Elements).Sum(e => e.Polygons.Count) == 66, "All localized demo views and regions remain available");
        Check(original.Specimens.GroupBy(s => s.MaterialId).All(g => g.Count() == 2 &&
            g.SelectMany(s => s.Elements).GroupBy(e => e.Id).All(e => e.Count() == 2)),
            "RU and EN share stable element and material IDs");
        Check(original.Specimens.SelectMany(s => s.Elements).Count(e => e.Polygons.Count == 0) == 4, "Unmarked elements are preserved");
        Check(original.Specimens.SelectMany(s => s.Elements).SelectMany(e => e.Polygons).SelectMany(p => p.Points).Any(p => p.X == 1969),
            "Existing one-pixel overshoot is not silently corrected");
        foreach (var imageId in original.Specimens.Select(s => s.ImageId!.Value).Distinct())
        {
            using var content = store.OpenImage(imageId);
            var actualHash = Hash(content);
            imageHashes[imageId] = actualHash;
            Check(metadataByHash.ContainsKey(actualHash) && content.CanSeek, "The native file streams the unchanged original photo " + actualHash[..8]);
            content.Position = 0;
            using var image = File.Create(Path.Combine(images, actualHash + ".jpg"));
            content.CopyTo(image);
        }
        Reject<CatalogStorageException>(() => { using var duplicate = new CatalogStore(path); }, "Second application cannot own the same catalog");
        Check(Convert.ToInt64(Sql(path, "SELECT COUNT(*) FROM schema_migrations;")) == 7, "All seven schema migrations recorded once");
        Check(Convert.ToInt64(Sql(path, "PRAGMA foreign_keys;")) == 1, "Foreign keys enforced");
        Reject<SqliteException>(() => Sql(path, "UPDATE regions SET material_id = (SELECT id FROM materials WHERE id <> regions.material_id LIMIT 1);"),
            "A region cannot cross material boundaries");
        store.CreateBackup(backup);
        Check(CatalogStore.IsNativeCatalog(backup), "Backup is a directly openable native catalog");
        Reject<CatalogStorageException>(() => store.CreateBackup(backup), "Existing backup is never silently overwritten");
    }

    var sendFolder = Path.Combine(temp, "Transfer"); Directory.CreateDirectory(sendFolder);
    var transfer = Path.Combine(sendFolder, "Catalog.histosets");
    File.Copy(backup, transfer);
    File.Delete(path);
    using (var store = new CatalogStore(transfer))
    {
        Check(Equivalent(original, store.Load()), "A single copied file opens in a new directory without its input catalog or source files");
        Check(imageHashes.All(pair => { using var input = store.OpenImage(pair.Key); return Hash(input) == pair.Value; }),
            "All original photographs are part of the portable copy");
    }
    Check(Directory.EnumerateFileSystemEntries(sendFolder).SequenceEqual([transfer]), "A closed catalog leaves only the portable document in its folder");

    var newer = Path.Combine(temp, "Future.histosets");
    File.Copy(backup, newer);
    Sql(newer, "PRAGMA user_version = 999;");
    var futureHash = Hash(newer);
    Reject<CatalogStorageException>(() => { using var invalid = new CatalogStore(newer); }, "Newer schema is rejected without downgrade");
    Check(Hash(newer) == futureHash, "Unsupported database bytes remain unchanged");

    var corrupt = Path.Combine(temp, "Corrupt.histosets");
    File.WriteAllText(corrupt, "damaged database header");
    Reject<SqliteException>(() => { using var invalid = new CatalogStore(corrupt); }, "Corrupted SQLite is not replaced with an empty atlas");
    var recovery = Path.Combine(temp, "Recovery.histosets");
    CatalogStore.CopyCatalog(backup, recovery);
    using (var recovered = new CatalogStore(recovery))
        Check(Equivalent(original, recovered.Load()) && File.ReadAllText(corrupt) == "damaged database header",
            "Recovery opens a complete copy and preserves the damaged original");
    Reject<CatalogStorageException>(() => CatalogStore.CopyCatalog(backup, recovery), "Copy refuses an occupied destination");
    File.Delete(corrupt);
    Reject<CatalogStorageException>(() => { using var invalid = new CatalogStore(corrupt); }, "Missing database is never silently recreated");
    Check(!File.Exists(corrupt), "Failed open does not leave a new database behind");

    var damaged = Path.Combine(temp, "Damaged photograph.histosets");
    File.Copy(backup, damaged);
    Sql(damaged, "UPDATE image_contents SET data=zeroblob(length(data)) WHERE rowid=(SELECT MIN(rowid) FROM image_contents);");
    var failedCopy = Path.Combine(temp, "Damaged copy.histosets");
    Reject<CatalogStorageException>(() => CatalogStore.CopyCatalog(damaged, failedCopy), "Same-length damaged photo cannot pass full snapshot validation");
    Check(!File.Exists(failedCopy), "An incomplete snapshot is not published");

    var emptyPath = Path.Combine(temp, "Empty.histosets");
    CatalogStore.CreateCatalog(emptyPath, "Новый атлас");
    using (var empty = new CatalogStore(emptyPath))
        Check(empty.GetMetadata().FormatVersion == 7 && empty.GetStatistics().Materials == 0 && empty.LoadTaxonomy().Groups.Count == 6,
            "New empty catalog has the six predefined tag types and no invented materials");

    var tagRoot = Path.Combine(temp, "TagCatalog.histosets");
    CatalogStore.CopyCatalog(backup, tagRoot);
    var tagsBackup = Path.Combine(temp, "tags.histosets");
    Guid groupId, tagId, materialId;
    using (var store = new CatalogStore(tagRoot))
    {
        var data = store.Load();
        var ru = CatalogBrowser.Filter(data, "ru", "", [], CatalogTaxonomy.Empty);
        var en = CatalogBrowser.Filter(data, "en", "", [], CatalogTaxonomy.Empty);
        Check(ru.Count == 5 && en.Count == 5 && ru.Select(s => s.MaterialId).SequenceEqual(en.Select(s => s.MaterialId)), "One material card per language, stable material identity");
        Check(CatalogBrowser.Filter(data, "en", "РОГОВИЦЫ", [], CatalogTaxonomy.Empty).Count == 2, "Cyrillic search works across translations even while displaying English");
        Check(CatalogBrowser.Filter(data, "ru", "skin", [], CatalogTaxonomy.Empty).Single().Locale == "ru", "English query returns Russian card");
        Check(CatalogBrowser.Filter(data, "ru", "роговицы большое", [], CatalogTaxonomy.Empty).Count == 1 && CatalogBrowser.Filter(data, "ru", "роговицы кожа", [], CatalogTaxonomy.Empty).Count == 0, "Multiple search terms narrow the result");
        var special = new AtlasCatalog([data.Specimens[0] with { Name = "Жёлтая железа", Locale = "und" }], []);
        Check(CatalogBrowser.Filter(special, "en", "ЖЕЛТАЯ", [], CatalogTaxonomy.Empty).Single().Locale == "und" && CatalogBrowser.Filter(special, "ru", "же\u0308лтая", [], CatalogTaxonomy.Empty).Count == 1, "E/YO and composed Unicode match, missing translations stay visible");
        var sameNames = new AtlasCatalog([ru[0] with { Name = "Same" }, ru[1] with { Name = "Same" }], []);
        Check(CatalogBrowser.Filter(sameNames, "ru", "", [], CatalogTaxonomy.Empty).Count == 2, "Equal material titles do not merge UUIDs");
        var hashBeforeOpening = Hash(store.DatabasePath);
        Check(store.LoadTaxonomy().Tags.Count == 0 && Hash(store.DatabasePath) == hashBeforeOpening && Convert.ToInt64(Sql(store.DatabasePath, "PRAGMA user_version;")) == 7, "Native schema reads are a no-op and system types do not invent tag values");

        groupId = store.LoadTaxonomy().Groups.Single(g => g.Code == "topic").Id;
        var purpose = store.LoadTaxonomy().Groups.Single(g => g.Code == "purpose").Id;
        tagId = store.SaveTag(null, groupId, "Эпителий", "Epithelium");
        var other = store.SaveTag(null, groupId, "Другая ткань", "Other tissue");
        var teaching = store.SaveTag(null, purpose, "Учебный", "Teaching");
        materialId = ru[0].MaterialId!.Value;
        var secondMaterial = ru[1].MaterialId!.Value;
        store.SetMaterialTags(materialId, [tagId, teaching]);
        store.SetMaterialTags(secondMaterial, [other]);
        var taxonomy = store.LoadTaxonomy();
        Check(taxonomy.Groups.Count == 6 && taxonomy.Tags.Count == 3 && taxonomy.MaterialTags[materialId].SetEquals([tagId, teaching]), "Use six predefined types, create translated tags and material-wide assignments");
        Check(CatalogBrowser.Filter(data, "ru", "", [tagId, other], taxonomy).Count == 2, "Tag alternatives in one group use OR");
        Check(CatalogBrowser.Filter(data, "en", "", [tagId, other, teaching], taxonomy).Single().MaterialId == materialId, "Different tag groups use AND independent of display language");
        var link = Sql(store.DatabasePath, $"SELECT id FROM material_tags WHERE material_id = '{materialId}' AND tag_id = '{tagId}';");
        var revision = Sql(store.DatabasePath, $"SELECT revision FROM materials WHERE id = '{materialId}';");
        var assignedHash = Hash(store.DatabasePath);
        store.SetMaterialTags(materialId, [tagId, teaching, tagId]);
        Check(Hash(store.DatabasePath) == assignedHash && Equals(revision, Sql(store.DatabasePath, $"SELECT revision FROM materials WHERE id = '{materialId}';")), "Repeated assignment is a no-op including record revisions");
        Reject<CatalogStorageException>(() => store.SaveTagGroup(null, "ТЕМА", ""), "Duplicate Cyrillic group name is rejected");
        Reject<CatalogStorageException>(() => store.SaveTag(null, groupId, "ЭПИТЕЛИЙ", ""), "Duplicate tag in one group is rejected");
        Reject<CatalogStorageException>(() => store.SaveTagGroup(null, " ", ""), "Blank dictionary entry is rejected");
        Reject<CatalogStorageException>(() => store.SaveTag(null, Guid.NewGuid(), "Orphan", ""), "Missing group cannot receive a tag");
        Reject<CatalogStorageException>(() => store.SetMaterialTags(materialId, [Guid.NewGuid()]), "Unknown tag cannot remove existing assignments");
        Check(store.LoadTaxonomy().MaterialTags[materialId].SetEquals([tagId, teaching]), "Rejected mutation preserves the prior complete tag set");
        Sql(store.DatabasePath, "CREATE TRIGGER fail_tags BEFORE INSERT ON material_tags BEGIN SELECT RAISE(ABORT, 'simulated write failure'); END;");
        Reject<CatalogStorageException>(() => store.SetMaterialTags(materialId, [other]), "Assignment handles failure after removals within the transaction");
        Sql(store.DatabasePath, "DROP TRIGGER fail_tags;");
        Check(store.LoadTaxonomy().MaterialTags[materialId].SetEquals([tagId, teaching]), "Failed assignment rolls all removals back");
        store.SetMaterialTags(materialId, [teaching]);
        store.SetMaterialTags(materialId, [tagId, teaching]);
        Check(Equals(link, Sql(store.DatabasePath, $"SELECT id FROM material_tags WHERE material_id = '{materialId}' AND tag_id = '{tagId}';")), "Reassignment revives the same link UUID");
        Reject<CatalogStorageException>(() => store.SaveTagGroup(groupId, "Тема занятия", "Topic"), "System type cannot be renamed");
        store.SaveTag(tagId, groupId, "Эпителиальные ткани", "Epithelial tissue");
        Check(store.LoadTaxonomy().MaterialTags[materialId].Contains(tagId) && CatalogBrowser.Label(store.LoadTaxonomy().Tags.Single(t => t.Id == tagId).Names, "ru") == "Эпителиальные ткани", "Rename preserves tag identity and assignments");
        Reject<CatalogStorageException>(() => store.DeleteTagGroup(groupId), "Nonempty group cannot be deleted accidentally");
        store.DeleteTag(other);
        Check(!store.LoadTaxonomy().MaterialTags.ContainsKey(secondMaterial) && !store.LoadTaxonomy().Tags.Any(t => t.Id == other), "Deleting a tag removes its active assignments");
        Check(Convert.ToInt64(Sql(store.DatabasePath, $"SELECT COUNT(*) FROM material_tags WHERE tag_id = '{other}' AND deleted_utc IS NOT NULL;")) == 1, "Removed assignment retains a deletion marker");
        Reject<CatalogStorageException>(() => store.SaveTagGroup(null, "Временная", "Temporary"), "New custom types cannot be created");
        Reject<CatalogStorageException>(() => store.DeleteTagGroup(purpose), "System type cannot be deleted");
        Check(Equivalent(original, store.Load()), "Tag edits preserve every source text, image link, and contour");
        store.CreateBackup(tagsBackup);
    }
    var tagRestore = Path.Combine(temp, "RestoredTags.histosets");
    CatalogStore.CopyCatalog(tagsBackup, tagRestore);
    using (var store = new CatalogStore(tagRestore))
    {
        var taxonomy = store.LoadTaxonomy();
        Check(taxonomy.MaterialTags[materialId].Contains(tagId) && taxonomy.Groups.Any(g => g.Id == groupId) && taxonomy.Tags.Single(t => t.Id == tagId).Names.Count == 2, "Full backup restores tag identities, translations and assignments");
        Check(Equivalent(original, store.Load()), "Restored tag catalog still preserves all atlas geometry and descriptions");
    }
    count += FoundationChecks.Run(temp);
    count += EditorChecks.Run(source, temp, Metadata);
    count += MeasurementStorageChecks.Run(source, temp, Metadata);
    count += ExchangeStorageChecks.Run(source, backup, temp, Metadata);
    count += TrainingStorageChecks.Run(source, backup, temp, Metadata);
    Console.WriteLine($"{count} storage checks passed.");
}
finally { Directory.Delete(temp, true); }

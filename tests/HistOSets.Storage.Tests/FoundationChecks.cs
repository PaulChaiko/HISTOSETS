using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HistOSets.Storage;
using HistOSets.Core;
using Microsoft.Data.Sqlite;

internal static class FoundationChecks
{
    public static int Run(string source, string baselineBackup, string temp)
    {
        var count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            count++;
            Console.WriteLine("PASS " + description);
        }
        void Reject(Action action, string description)
        {
            try { action(); }
            catch (CatalogStorageException) { Check(true, description); return; }
            throw new Exception("Expected CatalogStorageException: " + description);
        }

        var legacy = MakeSchema1(source, baselineBackup, Path.Combine(temp, "LegacySchema1"));
        var legacyDb = Path.Combine(legacy, "catalog.sqlite");
        var material = (string)Query(legacyDb, "SELECT id FROM materials ORDER BY id LIMIT 1;")!;
        var legacyGroup = Guid.NewGuid();
        var legacyTag = Guid.NewGuid();
        var looseTag = Guid.NewGuid();
        InsertLegacyGroup(legacyDb, legacyGroup, "Старая группа", "Legacy group");
        AddLegacyTag(legacyDb, legacyTag, legacyGroup, material);
        AddLegacyTag(legacyDb, looseTag, null, material);
        Query(legacyDb, """
            INSERT INTO material_translations(id,material_id,locale,name,summary,description,sort_order,created_utc,modified_utc,origin_catalog_id)
            SELECT $uuid,$id,'und','Legacy language','Original summary','Original detail',999,created_utc,modified_utc,origin_catalog_id
            FROM materials WHERE id=$id;
            """, ("$uuid", Guid.NewGuid().ToString()), ("$id", material));
        var tables = LegacyColumns(legacyDb);
        var before = ContentSnapshot(legacyDb, tables, false);
        var originalCatalogId = Query(legacyDb, "SELECT catalog_id FROM catalog_metadata;");
        string migrationBackup;
        var foundationBackup = Path.Combine(temp, "foundation2.histosets");
        Guid persistentAnalog;
        long savedRevision;
        using (var store = new CatalogStore(legacy))
        {
            Check(Convert.ToInt64(Query(legacyDb, "PRAGMA user_version;")) == 4 && Convert.ToInt64(Query(legacyDb, "SELECT COUNT(*) FROM schema_migrations;")) == 4,
                "Schema1 migrates to schema4 with an explicit migration ledger");
            Check(before == ContentSnapshot(legacyDb, tables, true), "Migration preserves all original UUIDs, revisions, geometry, text, und and legacy tag links exactly");
            Check(Equals(originalCatalogId, Query(legacyDb, "SELECT catalog_id FROM catalog_metadata;")), "Migration retains catalog identity");
            Check(store.LoadTaxonomy().Groups.Count(g => g.IsSystem) == 6 && store.LoadTaxonomy().Groups.Single(g => g.Id == legacyGroup).IsSystem == false,
                "Migration adds six fixed types and preserves the independent legacy type");
            Check(store.LoadTaxonomy().Tags.Single(t => t.Id == looseTag).GroupId is null, "Migration does not automatically classify an old untyped tag");
            Check(store.Load().Specimens.Any(s => s.Locale == "und" && s.Name == "Legacy language"), "Legacy und translation remains accessible");
            Check(Convert.ToInt64(Query(legacyDb, "SELECT COUNT(*) FROM material_images WHERE is_primary=1 AND deleted_utc IS NULL;")) == 5,
                "Migration selects exactly one deterministic primary image for each old object");
            Check(store.Load().Specimens.Where(s => s.MaterialId == Guid.Parse(material)).All(s => s.Status == "draft"), "Incomplete bilingual material remains visible as draft");
            migrationBackup = store.LastMigrationBackupPath ?? throw new Exception("Missing pre-migration backup path");
            Check(File.Exists(migrationBackup), "Full pre-migration .histosets archive is available");
            using (var zip = ZipFile.OpenRead(migrationBackup))
                Check(zip.Entries.Count(e => e.FullName.StartsWith("SPECIMENS/")) == 5 && zip.Entries.Any(e => e.FullName.StartsWith("SOURCES/")),
                    "Pre-migration backup includes original scientific images and source XML");
            var unmodified = Hash(legacyDb);
            var metadata = store.GetMetadata();
            store.RenameCatalog(metadata.Name);
            Check(Hash(legacyDb) == unmodified, "Renaming to the same catalog name is a byte-for-byte no-op");
            store.RenameCatalog("Учебный каталог");
            Check(store.GetMetadata().Name == "Учебный каталог" && store.GetMetadata().ContentRevision == metadata.ContentRevision + 1,
                "Catalog rename updates name and content revision atomically");
            Reject(() => store.RenameCatalog("\n"), "Catalog name must be nonempty");
            store.SaveTagGroup(legacyGroup, "Старая категория", "Legacy category");
            Check(store.LoadTaxonomy().Tags.Single(t => t.Id == legacyTag).GroupId == legacyGroup, "Legacy type can be renamed without losing tag membership");
            var author = store.LoadTaxonomy().Groups.Single(g => g.Code == "author");
            Reject(() => store.SaveTagGroup(author.Id, "Другой автор", "Changed author"), "System type rename is prohibited");
            Reject(() => store.DeleteTagGroup(author.Id), "Even an empty system type cannot be deleted");
            Reject(() => store.SaveTagGroup(null, "Новый тип", "New type"), "New custom type creation is prohibited");
            Reject(() => store.SaveTag(null, null, "Нет типа", "No type"), "New tag value requires a selected type");
            var revision = store.GetMetadata().ContentRevision;
            var newTag = store.SaveTag(null, author.Id, "Автор для теста", "Test author");
            Check(store.GetMetadata().ContentRevision == revision + 1, "Creating a tag advances catalog content revision once");
            revision = store.GetMetadata().ContentRevision;
            store.SaveTag(newTag, author.Id, "Автор для теста", "Test author");
            Check(store.GetMetadata().ContentRevision == revision, "Unchanged tag save leaves catalog revision untouched");

            var group = store.SaveAnalogGroup(null, "Роговица", "Cornea");
            store.SetMaterialAnalogGroup(Guid.Parse(material), group);
            Check(store.Load().Specimens.Where(s => s.MaterialId == Guid.Parse(material)).All(s => s.AnalogGroupId == group && s.AnalogGroupNames["en"] == "Cornea"),
                "Analog assignment is shared by all translations and image projections of a material");
            revision = store.GetMetadata().ContentRevision;
            store.SetMaterialAnalogGroup(Guid.Parse(material), group);
            store.SaveAnalogGroup(group, "Роговица", "Cornea");
            Check(store.GetMetadata().ContentRevision == revision, "Repeated analog assignment and unchanged names are no-ops");
            Reject(() => store.SaveAnalogGroup(null, "РОГОВИЦА", "Different"), "Analog names are checked for normalized duplicates");
            Reject(() => store.SaveAnalogGroup(null, "Только RU", ""), "Analog groups require both languages");
            Reject(() => store.SetMaterialAnalogGroup(Guid.Parse(material), Guid.NewGuid()), "Analog assignment rejects a missing group");
            Reject(() => store.DeleteAnalogGroup(group), "Assigned analog group cannot be removed accidentally");
            Query(legacyDb, "CREATE TRIGGER reject_analog_translation BEFORE UPDATE ON analog_group_translations WHEN NEW.locale='en' BEGIN SELECT RAISE(ABORT,'failure'); END;");
            var priorRevision = store.GetMetadata().ContentRevision;
            Reject(() => store.SaveAnalogGroup(group, "Изменение RU", "Changed EN"), "Analog edit handles a failure after its first translation update");
            Check(store.LoadAnalogGroups().Single(g => g.Id == group).Names["ru"] == "Роговица" && store.GetMetadata().ContentRevision == priorRevision,
                "Failed analog edit rolls back translations and catalog revision");
            Query(legacyDb, "DROP TRIGGER reject_analog_translation;");

            var currentImage = store.Load().Specimens.First(s => s.MaterialId == Guid.Parse(material)).MaterialImageId!.Value;
            revision = store.GetMetadata().ContentRevision;
            store.SetPrimaryImage(Guid.Parse(material), currentImage);
            Check(store.GetMetadata().ContentRevision == revision, "Setting the current primary image is a no-op");
            var otherImage = store.Load().Specimens.First(s => s.MaterialId != Guid.Parse(material)).MaterialImageId!.Value;
            Reject(() => store.SetPrimaryImage(Guid.Parse(material), otherImage), "A primary image cannot cross material boundaries");
            Check(!store.CheckReadiness(Guid.Parse(material)).IsReady && store.CheckReadiness(Guid.Parse(material)).Issues.Count > 0,
                "Readiness reports missing bilingual fields without hiding the legacy object");
            Reject(() => store.SetMaterialStatus(Guid.Parse(material), "ready"), "An incomplete material cannot be published as ready");

            // Complete the legacy test specimen without changing the production editor scope.
            Query(legacyDb, """
                UPDATE material_translations SET summary='Complete summary',description='Complete description' WHERE material_id=$id AND locale IN('ru','en');
                UPDATE element_translations SET summary='Complete summary',description='Complete description' WHERE element_id IN(SELECT id FROM elements WHERE material_id=$id) AND locale IN('ru','en');
                """, ("$id", material));
            store.SetMaterialTags(Guid.Parse(material), [newTag]);
            Check(store.CheckReadiness(Guid.Parse(material)).IsReady, "Readiness accepts complete RU/EN content with a verified primary image");
            store.SetMaterialStatus(Guid.Parse(material), "ready");
            Check(store.Load().Specimens.Where(s => s.MaterialId == Guid.Parse(material)).All(s => s.Status == "ready"), "Explicit publication persists ready state");
            store.SetMaterialTags(Guid.Parse(material), [legacyTag]);
            Check(store.Load().Specimens.Where(s => s.MaterialId == Guid.Parse(material)).All(s => s.Status == "draft"),
                "Assigning an incomplete legacy tag demotes ready material in the same transaction");
            store.SaveTag(legacyTag, legacyGroup, "Старый тег", "Legacy tag");
            Check(store.Load().Specimens.Where(s => s.MaterialId == Guid.Parse(material)).All(s => s.Status == "draft") && store.CheckReadiness(Guid.Parse(material)).IsReady,
                "Fixing missing translations never silently republishes a draft");
            store.SetMaterialStatus(Guid.Parse(material), "ready");
            store.SaveTag(legacyTag, legacyGroup, "Старый тег", "");
            Check(store.Load().Specimens.Where(s => s.MaterialId == Guid.Parse(material)).All(s => s.Status == "draft"),
                "Removing a required tag translation also demotes existing ready assignments");

            var groupToDelete = store.SaveAnalogGroup(null, "Временная", "Temporary");
            store.DeleteAnalogGroup(groupToDelete);
            Check(!store.LoadAnalogGroups().Any(g => g.Id == groupToDelete), "Unused analog group is soft-deleted");
            store.SetMaterialAnalogGroup(Guid.Parse(material), null);
            store.DeleteAnalogGroup(group);
            Check(store.Load().Specimens.Where(s => s.MaterialId == Guid.Parse(material)).All(s => s.AnalogGroupId is null), "Explicit unassignment clears all language views before deleting the group");
            Check(Convert.ToInt64(Query(legacyDb, "SELECT COUNT(*) FROM image_calibrations;")) == 0, "Migration does not invent physical calibration from DPI");
            Query(legacyDb, """
                INSERT INTO image_calibrations(id,material_image_id,pixel_size_x_m,pixel_size_y_m,source_kind,source_note,created_utc,modified_utc,origin_catalog_id)
                SELECT $uuid,$image,0.000002,0.000005,'manual_dimensions','Recorded test calibration',created_utc,created_utc,catalog_id FROM catalog_metadata;
                """, ("$uuid", Guid.NewGuid().ToString()), ("$image", currentImage.ToString()));
            try
            {
                Query(legacyDb, "UPDATE image_calibrations SET input_width=100,input_height=NULL,input_unit='um';");
                throw new Exception("Incomplete calibration input unexpectedly accepted");
            }
            catch (SqliteException) { Check(true, "Calibration provenance rejects half-filled width/height/unit triples"); }
            persistentAnalog = store.SaveAnalogGroup(null, "Группа для копии", "Backup group");
            store.SetMaterialAnalogGroup(Guid.Parse(material), persistentAnalog);
            savedRevision = store.GetMetadata().ContentRevision;
            store.CreateBackup(foundationBackup);
        }
        using (var reopened = new CatalogStore(legacy))
            Check(reopened.LastMigrationBackupPath is null && Directory.GetFiles(Path.Combine(legacy, "BACKUPS"), "*.histosets").Length == 1,
                "Reopening schema4 does not migrate or create redundant migration backups");

        var restoredTwo = Path.Combine(temp, "RestoreSchema4Foundation");
        CatalogStore.RestoreBackup(foundationBackup, restoredTwo);
        using (var restored = new CatalogStore(restoredTwo))
        {
            Check(restored.GetMetadata().Name == "Учебный каталог" && restored.GetMetadata().ContentRevision == savedRevision &&
                restored.Load().Specimens.First(s => s.MaterialId == Guid.Parse(material)).AnalogGroupId == persistentAnalog,
                "Schema4 backup restores catalog metadata, analog identities and assignments");
            Check(Convert.ToDouble(Query(restored.DatabasePath, "SELECT pixel_size_y_m FROM image_calibrations;")) == 0.000005,
                "Schema4 backup preserves independent physical X/Y calibration and provenance");
        }

        var restoredOne = Path.Combine(temp, "RestoreOriginalSchema1");
        CatalogStore.RestoreBackup(migrationBackup, restoredOne);
        Check(Convert.ToInt64(Query(Path.Combine(restoredOne, "catalog.sqlite"), "PRAGMA user_version;")) == 1,
            "Restoring a schema1 backup preserves its original version until opening");
        using (var restored = new CatalogStore(restoredOne))
            Check(before == ContentSnapshot(restored.DatabasePath, tables, true), "Schema1 backup restores and migrates again without changing original content");

        var failed = MakeSchema1(source, baselineBackup, Path.Combine(temp, "MigrationFailure"));
        var failedDb = Path.Combine(failed, "catalog.sqlite");
        Query(failedDb, "CREATE TRIGGER fail_migration BEFORE INSERT ON schema_migrations WHEN NEW.version=2 BEGIN SELECT RAISE(ABORT,'migration failure'); END;");
        var failureHash = Hash(failedDb);
        Reject(() => { using var rejected = new CatalogStore(failed); }, "Migration failure after schema alterations is reported");
        Check(Hash(failedDb) == failureHash && Convert.ToInt64(Query(failedDb, "PRAGMA user_version;")) == 1 &&
            Convert.ToInt64(Query(failedDb, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='analog_groups';")) == 0,
            "Failed migration rolls back all DDL, metadata and content, preserving database bytes");
        Check(Directory.GetFiles(Path.Combine(failed, "BACKUPS"), "*.histosets").Length == 1, "A complete schema1 backup remains available after migration rollback");

        var missing = MakeSchema1(source, baselineBackup, Path.Combine(temp, "MigrationMissingImage"));
        var missingDb = Path.Combine(missing, "catalog.sqlite");
        File.Delete(Path.Combine(missing, (string)Query(missingDb, "SELECT storage_key FROM images LIMIT 1;")!));
        var missingHash = Hash(missingDb);
        Reject(() => { using var rejected = new CatalogStore(missing); }, "Missing original blocks migration before any SQL changes");
        Check(Hash(missingDb) == missingHash && Convert.ToInt64(Query(missingDb, "PRAGMA user_version;")) == 1,
            "A failed full backup leaves schema1 and data untouched");

        using (var empty = new CatalogStore(Path.Combine(temp, "EmptySchema4")))
        {
            Check(empty.GetMetadata().FormatVersion == 4 && empty.GetStatistics().Materials == 0 && empty.LoadTaxonomy().Groups.Count == 6,
                "New empty catalog starts directly at schema4 with fixed types and no invented materials");
            Check(empty.LastMigrationBackupPath is null, "A genuinely new catalog does not require a schema1 backup");
        }
        return count;
    }

    internal static string MakeSchema1(string source, string backup, string root)
    {
        CatalogStore.RestoreBackup(backup, root);
        var db = Path.Combine(root, "catalog.sqlite");
        var copy = Path.Combine(root, "schema4-copy.sqlite");
        File.Move(db, copy);
        using (var connection = Open(db))
        {
            Execute(connection, File.ReadAllText(Path.Combine(source, "src", "HistOSets.Storage", "Migrations", "001_initial.sql")));
            Execute(connection, "PRAGMA application_id=1213420627; PRAGMA user_version=1;");
            Execute(connection, "ATTACH DATABASE $path AS previous;", ("$path", copy));
            var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM main.sqlite_master WHERE type='table' ORDER BY rowid;";
                using var reader = command.ExecuteReader();
                while (reader.Read()) tables.Add(reader.GetString(0));
            }
            foreach (var table in tables)
            {
                if (table == "schema_migrations") { Execute(connection, "INSERT INTO schema_migrations SELECT * FROM previous.schema_migrations WHERE version=1;"); continue; }
                if (table == "catalog_metadata") { Execute(connection, "INSERT INTO catalog_metadata SELECT singleton,catalog_id,1,created_utc FROM previous.catalog_metadata;"); continue; }
                if (table == "region_points")
                {
                    var points = new List<(string Id,RegionGeometry Geometry)>();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText="SELECT id,geometry_json FROM previous.regions ORDER BY id;";
                        using var reader=command.ExecuteReader();
                        while(reader.Read()) points.Add((reader.GetString(0),RegionGeometryCodec.Deserialize(reader.GetString(1))));
                    }
                    // Build the legacy fixture as one batch; per-vertex autocommit causes
                    // thousands of durable disk flushes, especially expensive on Windows.
                    using var transaction=connection.BeginTransaction();
                    using var insert=connection.CreateCommand();
                    insert.Transaction=transaction;
                    insert.CommandText="INSERT INTO region_points VALUES($id,$ordinal,$x,$y);";
                    insert.Parameters.Add("$id",SqliteType.Text);
                    insert.Parameters.Add("$ordinal",SqliteType.Integer);
                    insert.Parameters.Add("$x",SqliteType.Real);
                    insert.Parameters.Add("$y",SqliteType.Real);
                    foreach(var region in points)
                        for(var i=0;i<region.Geometry.Points.Count;i++)
                        {
                            insert.Parameters["$id"].Value=region.Id;
                            insert.Parameters["$ordinal"].Value=i;
                            insert.Parameters["$x"].Value=region.Geometry.Points[i].X;
                            insert.Parameters["$y"].Value=region.Geometry.Points[i].Y;
                            insert.ExecuteNonQuery();
                        }
                    transaction.Commit();
                    continue;
                }
                var columns = Columns(connection, table);
                var filter = table switch
                {
                    "tag_groups" => " WHERE is_system=0",
                    "tag_group_translations" => " WHERE group_id IN(SELECT id FROM previous.tag_groups WHERE is_system=0)",
                    _ => ""
                };
                Execute(connection, $"INSERT INTO main.{table}({string.Join(',', columns)}) SELECT {string.Join(',', columns)} FROM previous.{table}{filter};");
            }
        }
        File.Delete(copy);
        return root;
    }

    private static void InsertLegacyGroup(string db, Guid id, string ru, string en)
    {
        Query(db, """
            INSERT INTO tag_groups(id,code,created_utc,modified_utc,origin_catalog_id)
            SELECT $id,$code,created_utc,created_utc,catalog_id FROM catalog_metadata;
            """, ("$id", id.ToString()), ("$code", "legacy-" + id.ToString("N")));
        foreach (var (locale, name) in new[] { ("ru", ru), ("en", en) })
            Query(db, """
                INSERT INTO tag_group_translations(id,group_id,locale,name,created_utc,modified_utc,origin_catalog_id)
                SELECT $translation,$id,$locale,$name,created_utc,created_utc,catalog_id FROM catalog_metadata;
                """, ("$translation", Guid.NewGuid().ToString()), ("$id", id.ToString()), ("$locale", locale), ("$name", name));
    }

    private static void AddLegacyTag(string db, Guid tag, Guid? group, string material)
    {
        Query(db, """
            INSERT INTO tags(id,code,group_id,created_utc,modified_utc,origin_catalog_id)
            SELECT $id,$code,$group,created_utc,created_utc,catalog_id FROM catalog_metadata;
            INSERT INTO tag_translations(id,tag_id,locale,name,created_utc,modified_utc,origin_catalog_id)
            SELECT $translation,$id,'ru','Старый тег',created_utc,created_utc,catalog_id FROM catalog_metadata;
            INSERT INTO material_tags(id,material_id,tag_id,created_utc,modified_utc,origin_catalog_id)
            SELECT $link,$material,$id,created_utc,created_utc,catalog_id FROM catalog_metadata;
            """, ("$id", tag.ToString()), ("$code", "tag-" + tag.ToString("N")), ("$group", group?.ToString()),
            ("$translation", Guid.NewGuid().ToString()), ("$link", Guid.NewGuid().ToString()), ("$material", material));
    }

    private static Dictionary<string, List<string>> LegacyColumns(string db)
    {
        using var connection = Open(db);
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN('schema_migrations','catalog_metadata') ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        return tables.ToDictionary(t => t, t => Columns(connection, t));
    }

    private static string ContentSnapshot(string db, Dictionary<string, List<string>> tables, bool migrated)
    {
        using var connection = Open(db);
        var result = new Dictionary<string, List<object?[]>>();
        foreach (var (table, columns) in tables)
        {
            if(migrated && table=="region_points")
            {
                var pointRows=new List<object?[]>();
                using var geometryCommand=connection.CreateCommand();
                geometryCommand.CommandText="SELECT id,geometry_json FROM regions ORDER BY id;";
                using var geometryReader=geometryCommand.ExecuteReader();
                while(geometryReader.Read())
                {
                    var points=RegionGeometryCodec.Deserialize(geometryReader.GetString(1)).Points;
                    for(var i=0;i<points.Count;i++) pointRows.Add([geometryReader.GetString(0),(long)i,points[i].X,points[i].Y]);
                }
                result[table]=pointRows;
                continue;
            }
            var filter = !migrated ? "" : table switch
            {
                "tag_groups" => " WHERE is_system=0",
                "tag_group_translations" => " WHERE group_id IN(SELECT id FROM tag_groups WHERE is_system=0)",
                _ => ""
            };
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(',', columns)} FROM {table}{filter} ORDER BY 1,2;";
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
            result[table] = rows;
        }
        return JsonSerializer.Serialize(result);
    }

    private static List<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA main.table_info({table});";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }
    private static SqliteConnection Open(string db)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private static object? Query(string db, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open(db);
        return Execute(connection, sql, parameters);
    }
    private static object? Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name,value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar();
    }
    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
}

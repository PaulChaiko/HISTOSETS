using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

public sealed partial class CatalogStore
{
    public static bool IsNativeCatalog(string path)
    {
        using var input = File.OpenRead(path);
        Span<byte> header = stackalloc byte[72];
        return input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && header[..16].SequenceEqual("SQLite format 3\0"u8)
            && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[60..64]) >= CatalogDatabase.Version
            && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[68..72]) == CatalogDatabase.ApplicationId;
    }

    /// <summary>Converts the previous SQLite-with-originals or full backup. The source is never modified.</summary>
    public static void ConvertLegacyCatalog(string source, string destination)
    {
        source = Path.GetFullPath(source);
        var workspace = Path.Combine(Path.GetTempPath(), "histosets-migrate-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (File.Exists(source) && string.Equals(Path.GetExtension(source), ".histosets", StringComparison.OrdinalIgnoreCase))
            {
                if (IsNativeCatalog(source)) throw new CatalogStorageException("Это уже каталог нового формата. Откройте его или сохраните копию.");
                Directory.CreateDirectory(workspace);
                ExtractLegacyBackup(source, workspace);
                source = workspace;
            }
            var database = Directory.Exists(source) ? Path.Combine(source, "catalog.sqlite") : source;
            var originals = Path.GetDirectoryName(database)!;
            PublishFile(destination, partial =>
            {
                using (var old = Open(database))
                {
                    Validate(old, allowLegacy: true);
                    if (SchemaVersion(old) >= CatalogDatabase.Version)
                        throw new CatalogStorageException("Это уже каталог нового формата.");
                    using var snapshot = Open(partial, SqliteOpenMode.ReadWriteCreate);
                    old.BackupDatabase(snapshot);
                }
                using var connection = Open(partial, SqliteOpenMode.ReadWrite);
                Execute(connection, "PRAGMA journal_mode=DELETE;");
                var version = SchemaVersion(connection);
                using var transaction = connection.BeginTransaction();
                if (version < 2) ApplyVersion2(connection, transaction, DefaultCatalogName(originals));
                if (version < 3) ApplyVersion3(connection, transaction);
                if (version < 4) ApplyVersion4(connection, transaction);
                if (version < 5) ApplyVersion5(connection, transaction);
                if (version < 6) ApplyVersion6(connection, transaction);
                ApplyNativeSchema(connection, transaction);
                var images = new List<(string Id, string Key, string Hash, long Size)>();
                using (var command = Command(connection, "SELECT id,storage_key,sha256,byte_size FROM images;", transaction))
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) images.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
                foreach (var (id, key, hash, size) in images)
                {
                    if (!key.StartsWith("SPECIMENS/", StringComparison.Ordinal))
                        throw new CatalogStorageException("Недопустимая ссылка на исходное изображение: " + key);
                    var path = StorageFiles.Resolve(originals, key);
                    if (!File.Exists(path)) throw new CatalogStorageException("Не найден исходный снимок: " + key);
                    using var input = File.OpenRead(path);
                    WriteImageContent(connection, transaction, id, input, size, hash);
                }
                VerifyImageContents(connection, transaction);
                Validate(connection, transaction: transaction, deep: true);
                transaction.Commit();
            });
        }
        finally
        {
            try { if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void ApplyNativeSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, Resource("Migrations.007_single_file_catalog.sql"), transaction);
        Execute(connection, "PRAGMA defer_foreign_keys=ON;", transaction);
        SplitImageCards(connection, transaction);

        // Old XML references are not part of the new document. Retain neutral provenance FKs
        // for graph compatibility; neither the prior XML, file paths nor its manifest enter the file.
        string[] sourceTables = ["materials", "material_translations", "images", "material_images", "elements",
            "element_translations", "regions", "image_calibrations", "tag_groups", "tag_group_translations", "tags",
            "tag_translations", "material_tags", "analog_groups", "analog_group_translations", "training_prompts", "training_prompt_translations"];
        foreach (var table in sourceTables)
            Execute(connection, $"UPDATE {table} SET source_batch_id=NULL WHERE source_batch_id IS NOT NULL;", transaction);
        Execute(connection, """
            DROP TABLE import_batches;
            CREATE TABLE import_batches(id TEXT PRIMARY KEY NOT NULL) STRICT;
            CREATE UNIQUE INDEX ix_one_active_material_image ON material_images(material_id) WHERE deleted_utc IS NULL;
            UPDATE material_images SET is_primary=1 WHERE deleted_utc IS NULL;
            """, transaction);

        var now = StorageFiles.UtcNow();
        Execute(connection, """
            UPDATE tags SET group_id = CASE WHEN group_id IN (
                SELECT g.id FROM tag_groups g LEFT JOIN tag_group_translations t ON t.group_id=g.id
                WHERE g.code IN ('histology','histologia') OR lower(trim(t.name))='histology' OR trim(t.name) IN ('Гистология','гистология')
            ) THEN $subject ELSE $other END
            WHERE group_id IS NULL OR group_id NOT IN (
                SELECT id FROM tag_groups WHERE is_system=1 AND code IN ('author','organization','subject','topic','purpose','other') AND deleted_utc IS NULL
            );
            UPDATE tag_groups SET deleted_utc=$now WHERE deleted_utc IS NULL AND
                (is_system=0 OR code NOT IN ('author','organization','subject','topic','purpose','other'));
            UPDATE tag_group_translations SET deleted_utc=$now WHERE deleted_utc IS NULL AND group_id IN (
                SELECT id FROM tag_groups WHERE deleted_utc IS NOT NULL);
            INSERT INTO schema_migrations VALUES(7,$now);
            PRAGMA user_version=7;
            """, transaction, ("$now", now), ("$subject", StorageFiles.Id("system-tag-type/subject")), ("$other", StorageFiles.Id("system-tag-type/other")));
    }

    private static void SplitImageCards(SqliteConnection connection, SqliteTransaction transaction)
    {
        var extra = new List<(string Material, string Link, int Number)>();
        using (var command = Command(connection, """
            SELECT material_id,id,ordinal FROM (
              SELECT material_id,id,row_number() OVER (PARTITION BY material_id ORDER BY is_primary DESC,sort_order,id) AS ordinal
              FROM material_images WHERE deleted_utc IS NULL
            ) WHERE ordinal>1 ORDER BY material_id,ordinal;
            """, transaction))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) extra.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        if (extra.Count == 0) return;
        connection.CreateFunction<string, string>("histosets_uuid", StorageFiles.Id, isDeterministic: true);
        foreach (var (material, link, number) in extra)
        {
            var card = StorageFiles.Id("split-card/" + link);
            var suffix = " · " + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Keep same material identity on the first image and group former views together for self-testing.
            var group = Scalar(connection, "SELECT analog_group_id FROM materials WHERE id=$id;", transaction, ("$id", material)) as string;
            if (group is null)
            {
                group = StorageFiles.Id("split-analogs/" + material);
                Execute(connection, """
                    INSERT INTO analog_groups(id,created_utc,modified_utc,origin_catalog_id,deleted_utc)
                    SELECT $group,created_utc,modified_utc,origin_catalog_id,deleted_utc FROM materials WHERE id=$old;
                    INSERT INTO analog_group_translations(id,group_id,locale,name,created_utc,modified_utc,origin_catalog_id,deleted_utc)
                    SELECT histosets_uuid($group||'/'||id),$group,locale,name,created_utc,modified_utc,origin_catalog_id,deleted_utc
                    FROM material_translations WHERE material_id=$old AND trim(name)<>'';
                    UPDATE materials SET analog_group_id=$group WHERE id=$old;
                    """, transaction, ("$old", material), ("$group", group));
            }
            var p = new (string,object?)[] { ("$old", material), ("$new", card), ("$link", link), ("$suffix", suffix) };
            const string clone = "histosets_uuid($new||'/'||id)";
            Copy("materials", "id=$old", ("id", "$new"));
            Copy("material_translations", "material_id=$old", ("id", clone), ("material_id", "$new"),
                ("name", "CASE WHEN trim(name)='' THEN name ELSE name||$suffix END"));
            Copy("material_tags", "material_id=$old", ("id", clone), ("material_id", "$new"));
            Copy("elements", "material_id=$old", ("id", clone), ("material_id", "$new"));
            Copy("element_translations", "element_id IN (SELECT id FROM elements WHERE material_id=$old)",
                ("id", clone), ("element_id", "histosets_uuid($new||'/'||element_id)"));
            Copy("training_prompts", "material_id=$old", ("id", clone), ("material_id", "$new"),
                ("element_id", "CASE WHEN element_id IS NULL THEN NULL ELSE histosets_uuid($new||'/'||element_id) END"));
            Copy("training_prompt_translations", "prompt_id IN (SELECT id FROM training_prompts WHERE material_id=$old)",
                ("id", clone), ("prompt_id", "histosets_uuid($new||'/'||prompt_id)"));
            Execute(connection, """
                UPDATE material_images SET material_id=$new,is_primary=1,sort_order=0 WHERE id=$link;
                UPDATE regions SET material_id=$new,element_id=histosets_uuid($new||'/'||element_id) WHERE material_image_id=$link;
                """, transaction, p);

            void Copy(string table, string filter, params (string Name, string Expression)[] values)
            {
                var replacements = values.ToDictionary(v => v.Name, v => v.Expression, StringComparer.Ordinal);
                var columns = new List<string>();
                using (var command = Command(connection, $"PRAGMA table_info({table});", transaction))
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) columns.Add(reader.GetString(1));
                var names = string.Join(",", columns.Select(c => "\"" + c + "\""));
                var projection = string.Join(",", columns.Select(c => replacements.GetValueOrDefault(c) ?? "\"" + c + "\""));
                Execute(connection, $"INSERT INTO {table}({names}) SELECT {projection} FROM {table} WHERE {filter};", transaction, p);
            }
        }
    }
}

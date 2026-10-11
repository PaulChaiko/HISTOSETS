using System.Reflection;
using Microsoft.Data.Sqlite;
using HistOSets.Core;

namespace HistOSets.Storage;

internal static class CatalogDatabase
{
    internal const int ApplicationId = 0x48535453; // HSTS
    internal const int Version = 6;

    internal static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, ForeignKeys = true, Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA trusted_schema = OFF;");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal static void Create(string path, string name)
    {
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        using var transaction = connection.BeginTransaction();
        Execute(connection, Resource("Migrations.001_initial.sql"), transaction);
        Execute(connection, $"PRAGMA application_id = {ApplicationId}; PRAGMA user_version = 1;", transaction);
        Execute(connection, "INSERT INTO schema_migrations VALUES ($version, $now);", transaction,
            ("$version", 1), ("$now", StorageFiles.UtcNow()));
        Execute(connection, "INSERT INTO catalog_metadata VALUES (1, $id, 1, $now);", transaction,
            ("$id", Guid.NewGuid().ToString()), ("$now", StorageFiles.UtcNow()));
        ApplyVersion2(connection, transaction, name);
        ApplyVersion3(connection, transaction);
        ApplyVersion4(connection, transaction);
        ApplyVersion5(connection, transaction);
        ApplyVersion6(connection, transaction);
        Validate(connection, transaction: transaction);
        transaction.Commit();
    }

    internal static int SchemaVersion(SqliteConnection connection) => Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));

    internal static void ApplyVersion2(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        Execute(connection, Resource("Migrations.002_catalog_foundation.sql"), transaction);
        var now = StorageFiles.UtcNow();
        var origin = (string)Scalar(connection, "SELECT catalog_id FROM catalog_metadata;", transaction)!;
        Execute(connection, """
            UPDATE catalog_metadata SET name = $name, modified_utc = $now,
                content_revision = CASE WHEN EXISTS(SELECT 1 FROM materials) OR EXISTS(SELECT 1 FROM tags) THEN 1 ELSE 0 END;
            UPDATE material_images SET is_primary = 1 WHERE deleted_utc IS NULL AND id = (
                SELECT mi.id FROM material_images mi JOIN images i ON i.id = mi.image_id AND i.deleted_utc IS NULL
                WHERE mi.material_id = material_images.material_id AND mi.deleted_utc IS NULL
                ORDER BY mi.sort_order, mi.id LIMIT 1);
            """, transaction, ("$name", name), ("$now", now));
        (string Code, string Ru, string En)[] types = [
            ("author", "Автор", "Author"), ("organization", "Организация", "Organization"),
            ("subject", "Предметная область", "Subject"), ("topic", "Тема", "Topic"),
            ("purpose", "Назначение", "Purpose"), ("other", "Прочее", "Other")];
        foreach (var type in types)
        {
            var id = StorageFiles.Id("system-tag-type/" + type.Code);
            Execute(connection, """
                INSERT INTO tag_groups(id,code,is_system,created_utc,modified_utc,origin_catalog_id)
                VALUES($id,$code,1,$now,$now,$origin);
                """, transaction, ("$id", id), ("$code", type.Code), ("$now", now), ("$origin", origin));
            foreach (var (locale, label) in new[] { ("ru", type.Ru), ("en", type.En) })
                Execute(connection, """
                    INSERT INTO tag_group_translations(id,group_id,locale,name,created_utc,modified_utc,origin_catalog_id)
                    VALUES($id,$group,$locale,$name,$now,$now,$origin);
                    """, transaction, ("$id", StorageFiles.Id("system-tag-type/" + type.Code + "/" + locale)),
                    ("$group", id), ("$locale", locale), ("$name", label), ("$now", now), ("$origin", origin));
        }
        CatalogStore.RefreshImportedStatuses(connection, transaction);
        Execute(connection, "INSERT INTO schema_migrations VALUES (2,$now); PRAGMA user_version = 2;", transaction, ("$now", now));
    }

    internal static void Validate(SqliteConnection connection, bool allowLegacy = false, SqliteTransaction? transaction = null)
    {
        var appId = Convert.ToInt64(Scalar(connection, "PRAGMA application_id;", transaction));
        if (appId != ApplicationId) throw new CatalogStorageException("Выбранный файл не является базой HISTOSETS.");
        var version = Convert.ToInt64(Scalar(connection, "PRAGMA user_version;", transaction));
        if (version != Version && !(allowLegacy && version is 1 or 2 or 3 or 4 or 5))
            throw new CatalogStorageException($"Версия базы {version} не поддерживается этой сборкой (ожидается {Version}). Файл не изменён.");
        if (!Equals(Scalar(connection, "PRAGMA quick_check;", transaction), "ok"))
            throw new CatalogStorageException("Нарушена целостность SQLite. Восстановите резервную копию в другой каталог.");
        using (var command = Command(connection, "PRAGMA foreign_key_check;", transaction))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) throw new CatalogStorageException("В базе обнаружена нарушенная связь между записями.");
        if (Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM catalog_metadata WHERE singleton = 1 AND format_version = $version;", transaction, ("$version", version))) != 1 ||
            Convert.ToInt64(Scalar(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;", transaction)) != version)
            throw new CatalogStorageException("Повреждены сведения о версии каталога.");
        if (!Guid.TryParse(Scalar(connection, "SELECT catalog_id FROM catalog_metadata;", transaction) as string, out _))
            throw new CatalogStorageException("Неверный идентификатор каталога.");
        using (var command = Command(connection, """
            SELECT id FROM materials UNION ALL SELECT id FROM images UNION ALL SELECT id FROM elements
            UNION ALL SELECT id FROM regions UNION ALL SELECT id FROM import_batches
            UNION ALL SELECT id FROM material_translations UNION ALL SELECT id FROM element_translations
            UNION ALL SELECT id FROM material_images UNION ALL SELECT id FROM tag_groups
            UNION ALL SELECT id FROM tag_group_translations UNION ALL SELECT id FROM tags
            UNION ALL SELECT id FROM tag_translations UNION ALL SELECT id FROM material_tags
            """ + (version >= 2 ? " UNION ALL SELECT id FROM analog_groups UNION ALL SELECT id FROM analog_group_translations UNION ALL SELECT id FROM image_calibrations" : "")
                 + (version >= 6 ? " UNION ALL SELECT id FROM training_prompts UNION ALL SELECT id FROM training_prompt_translations;" : ";"), transaction))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (!Guid.TryParseExact(reader.GetString(0), "D", out _))
                    throw new CatalogStorageException("В базе обнаружен неверный UUID записи.");
        // Reject incomplete geometry instead of silently displaying a different atlas.
        if (version < 3 && Convert.ToInt64(Scalar(connection, """
            SELECT COUNT(*) FROM regions r WHERE
              (SELECT COUNT(*) FROM region_points p WHERE p.region_id = r.id) < 3
              OR (SELECT COUNT(*) FROM (SELECT DISTINCT x,y FROM region_points p WHERE p.region_id = r.id)) < 3;
            """, transaction)) != 0)
            throw new CatalogStorageException("В базе обнаружен неполный контур.");
        if (version >= 3)
        {
            using var command = Command(connection, "SELECT geometry_type,geometry_json FROM regions;", transaction);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    if (RegionGeometryCodec.Deserialize(reader.GetString(1)).Kind != reader.GetString(0))
                        throw new FormatException("Geometry type mismatch.");
                }
                catch (FormatException ex) { throw new CatalogStorageException("Повреждено описание области.", ex); }
            }
        }
        if (version >= 2)
        {
            using var command = Command(connection, "SELECT pixel_size_x_m,pixel_size_y_m,source_kind,source_note,input_width,input_height,input_unit FROM image_calibrations;", transaction);
            using var reader = command.ExecuteReader();
            while (reader.Read()) CatalogStore.CalibrationFromReader(reader);
        }
        if (version >= 5)
        {
            using var command = Command(connection, "SELECT source_catalog_id,entity_id,source_fingerprint,local_fingerprint FROM exchange_baselines;", transaction);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (!Guid.TryParseExact(reader.GetString(0), "D", out _) || !Guid.TryParseExact(reader.GetString(1), "D", out _) ||
                    !reader.GetString(2).All(Uri.IsHexDigit) || !reader.GetString(3).All(Uri.IsHexDigit))
                    throw new CatalogStorageException("Повреждена история обмена каталогами.");
        }
        if (version >= 6 && Convert.ToInt64(Scalar(connection, """
            SELECT
              (SELECT COUNT(*) FROM training_prompts p JOIN materials m ON m.id=p.material_id
               LEFT JOIN elements e ON e.id=p.element_id
               WHERE p.deleted_utc IS NULL AND (m.deleted_utc IS NOT NULL OR
                   (p.element_id IS NOT NULL AND (e.id IS NULL OR e.deleted_utc IS NOT NULL))))
              + (SELECT COUNT(*) FROM training_prompt_translations t JOIN training_prompts p ON p.id=t.prompt_id
                 WHERE t.deleted_utc IS NULL AND p.deleted_utc IS NOT NULL);
            """, transaction)) != 0)
            throw new CatalogStorageException("Учебное описание ссылается на удалённый объект или элемент.");
    }

    internal static void ApplyVersion3(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, Resource("Migrations.003_editor_geometry.sql"), transaction);
        var regions = new List<string>();
        using (var command = Command(connection, "SELECT id FROM regions;", transaction))
        using (var reader = command.ExecuteReader()) while (reader.Read()) regions.Add(reader.GetString(0));
        foreach (var id in regions)
        {
            var points = new List<AtlasPoint>();
            using (var command = Command(connection, "SELECT x,y FROM region_points WHERE region_id=$id ORDER BY ordinal;", transaction, ("$id", id)))
            using (var reader = command.ExecuteReader()) while (reader.Read()) points.Add(new(reader.GetDouble(0),reader.GetDouble(1)));
            var json = RegionGeometryCodec.Serialize(RegionGeometry.Polygon(points));
            Execute(connection, """
                INSERT INTO regions_v3(id,material_id,material_image_id,element_id,geometry_type,coordinate_space,geometry_json,sort_order,revision,created_utc,modified_utc,deleted_utc,origin_catalog_id,source_batch_id)
                SELECT id,material_id,material_image_id,element_id,geometry_type,coordinate_space,$json,sort_order,revision,created_utc,modified_utc,deleted_utc,origin_catalog_id,source_batch_id FROM regions WHERE id=$id;
                """, transaction, ("$json", json), ("$id", id));
        }
        Execute(connection, """
            DROP TABLE region_points;
            DROP TABLE regions;
            ALTER TABLE regions_v3 RENAME TO regions;
            CREATE INDEX ix_regions_element ON regions(element_id,sort_order);
            CREATE INDEX ix_regions_image ON regions(material_image_id);
            INSERT INTO schema_migrations VALUES(3,$now);
            PRAGMA user_version=3;
            """, transaction, ("$now", StorageFiles.UtcNow()));
    }

    internal static void ApplyVersion4(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Existing JSON is copied verbatim: changing supported tools must not round,
        // simplify or reserialize scientific contours and their revision history.
        Execute(connection, Resource("Migrations.004_compound_geometry.sql"), transaction);
        Execute(connection, "INSERT INTO schema_migrations VALUES(4,$now); PRAGMA user_version=4;", transaction,
            ("$now", StorageFiles.UtcNow()));
    }

    internal static void ApplyVersion5(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, Resource("Migrations.005_catalog_exchange.sql"), transaction);
        Execute(connection, "INSERT INTO schema_migrations VALUES(5,$now); PRAGMA user_version=5;", transaction,
            ("$now", StorageFiles.UtcNow()));
    }

    internal static void ApplyVersion6(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, Resource("Migrations.006_training_preparation.sql"), transaction);
        Execute(connection, "INSERT INTO schema_migrations VALUES(6,$now); PRAGMA user_version=6;", transaction,
            ("$now", StorageFiles.UtcNow()));
    }

    internal static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HistOSets.Storage." + name)
            ?? throw new InvalidOperationException("Missing resource: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, transaction, parameters);
        command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, transaction, parameters);
        return command.ExecuteScalar();
    }
}

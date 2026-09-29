using System.Reflection;
using Microsoft.Data.Sqlite;

namespace HistOSets.Storage;

internal static class CatalogDatabase
{
    internal const int ApplicationId = 0x48535453; // HSTS
    internal const int Version = 1;

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

    internal static void Create(string path)
    {
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        using var transaction = connection.BeginTransaction();
        Execute(connection, Resource("Migrations.001_initial.sql"), transaction);
        Execute(connection, $"PRAGMA application_id = {ApplicationId}; PRAGMA user_version = {Version};", transaction);
        Execute(connection, "INSERT INTO schema_migrations VALUES ($version, $now);", transaction,
            ("$version", Version), ("$now", StorageFiles.UtcNow()));
        Execute(connection, "INSERT INTO catalog_metadata VALUES (1, $id, 1, $now);", transaction,
            ("$id", Guid.NewGuid().ToString()), ("$now", StorageFiles.UtcNow()));
        transaction.Commit();
    }

    internal static void Validate(SqliteConnection connection)
    {
        var appId = Convert.ToInt64(Scalar(connection, "PRAGMA application_id;"));
        if (appId != ApplicationId) throw new CatalogStorageException("Выбранный файл не является базой HISTOSETS.");
        var version = Convert.ToInt64(Scalar(connection, "PRAGMA user_version;"));
        if (version != Version)
            throw new CatalogStorageException($"Версия базы {version} не поддерживается этой сборкой (ожидается {Version}). Файл не изменён.");
        if (!Equals(Scalar(connection, "PRAGMA quick_check;"), "ok"))
            throw new CatalogStorageException("Нарушена целостность SQLite. Восстановите резервную копию в другой каталог.");
        using (var command = Command(connection, "PRAGMA foreign_key_check;"))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) throw new CatalogStorageException("В базе обнаружена нарушенная связь между записями.");
        if (Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM catalog_metadata WHERE singleton = 1 AND format_version = 1;")) != 1 ||
            Convert.ToInt64(Scalar(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;")) != Version)
            throw new CatalogStorageException("Повреждены сведения о версии каталога.");
        if (!Guid.TryParse(Scalar(connection, "SELECT catalog_id FROM catalog_metadata;") as string, out _))
            throw new CatalogStorageException("Неверный идентификатор каталога.");
        using (var command = Command(connection, """
            SELECT id FROM materials UNION ALL SELECT id FROM images UNION ALL SELECT id FROM elements
            UNION ALL SELECT id FROM regions UNION ALL SELECT id FROM import_batches
            UNION ALL SELECT id FROM material_translations UNION ALL SELECT id FROM element_translations
            UNION ALL SELECT id FROM material_images;
            """))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (!Guid.TryParseExact(reader.GetString(0), "D", out _))
                    throw new CatalogStorageException("В базе обнаружен неверный UUID записи.");
        // Reject incomplete geometry instead of silently displaying a different atlas.
        if (Convert.ToInt64(Scalar(connection, """
            SELECT COUNT(*) FROM regions r WHERE
              (SELECT COUNT(*) FROM region_points p WHERE p.region_id = r.id) < 3
              OR (SELECT COUNT(*) FROM (SELECT DISTINCT x,y FROM region_points p WHERE p.region_id = r.id)) < 3;
            """)) != 0)
            throw new CatalogStorageException("В базе обнаружен неполный контур.");
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

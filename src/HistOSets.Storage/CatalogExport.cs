using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

public sealed partial class CatalogStore
{
    public IReadOnlyList<ExchangeMaterialInfo> LoadExchangeIndex(string locale = "ru", bool includeDeleted = false)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var connection = Open(DatabasePath);
            var predicate = includeDeleted ? "1=1" : "deleted_utc IS NULL";
            var rows = new Dictionary<string, IReadOnlyList<ExchangeRow>> {
                ["materials"] = ReadExchangeRows(connection, "materials", predicate),
                ["material_translations"] = ReadExchangeRows(connection, "material_translations", $"material_id IN (SELECT id FROM materials WHERE {predicate})")
            };
            return ExchangeSchema.MaterialInfo(rows, locale);
        }
    }

    /// <summary>Export a standalone catalog containing only the selected roots and their dependencies.</summary>
    public void ExportMaterials(string destination, IReadOnlyCollection<Guid> materialIds)
    {
        ArgumentNullException.ThrowIfNull(materialIds);
        var selected = materialIds.Distinct().Order().ToArray();
        if (selected.Length is 0 or > 100_000 || selected.Any(id => id == Guid.Empty))
            throw new CatalogStorageException("Выберите от 1 до 100 000 объектов для экспорта.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            destination = Path.GetFullPath(destination);
            if (!Path.GetExtension(destination).Equals(".histosets", StringComparison.OrdinalIgnoreCase))
                throw new CatalogStorageException("Выберите расширение .histosets для каталога.");
            if (File.Exists(destination)) throw new CatalogStorageException("Файл уже существует. Выберите новое имя каталога.");
            PublishFile(destination, partial =>
            {
                using var source = Open(DatabasePath);
                using var snapshot = source.BeginTransaction(deferred: true);
                Validate(source, transaction: snapshot);
                var tables = ReadExchangeSelection(source, selected, snapshot);
                if (tables["materials"].Count != selected.Length)
                    throw new CatalogStorageException("Выбранный объект больше не существует. Обновите список экспорта.");
                var (_, sourceId, sourceName, revision, _) = ReadExchangeOrigin(source, snapshot);
                Create(partial, sourceName);
                using (var target = Open(partial, SqliteOpenMode.ReadWrite))
                using (var transfer = target.BeginTransaction())
                {
                    ExchangePackage.InsertGraph(target, transfer, tables);
                    foreach (var row in tables["images"])
                    {
                        using var original = ReadImageContent(source, snapshot, row.Id);
                        WriteImageContent(target, transfer, row.Id, original, row.Integer("byte_size"), row.String("sha256")!);
                    }
                    Execute(target, """
                        INSERT INTO exchange_origin(singleton,source_catalog_id,source_catalog_name,source_content_revision,exported_utc)
                        VALUES(1,$id,$name,$revision,$now);
                        """, transfer, ("$id", sourceId.ToString()), ("$name", sourceName), ("$revision", revision), ("$now", StorageFiles.UtcNow()));
                    Validate(target, transaction: transfer, deep: true);
                    VerifyImageContents(target, transfer);
                    transfer.Commit();
                }
            });
        }
    }

    internal static (Guid CatalogId, Guid SourceId, string Name, long Revision, string Time) ReadExchangeOrigin(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        Guid catalogId;
        string name, time;
        long revision;
        using (var command = Command(connection, "SELECT catalog_id,name,content_revision,created_utc FROM catalog_metadata WHERE singleton=1;", transaction))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || !ExchangePackage.IsUuid(reader.GetString(0)))
                throw new CatalogStorageException("Не удалось определить источник каталога.");
            catalogId = Guid.Parse(reader.GetString(0));
            name = reader.GetString(1);
            revision = reader.GetInt64(2);
            time = reader.GetString(3);
        }
        var sourceId = catalogId;
        // An unedited export belongs to the sender lineage. After a local edit this
        // standalone catalog has its own lineage and cannot silently replace the sender's revisions.
        if (revision == 0)
        {
            using var command = Command(connection, """
                SELECT source_catalog_id,source_catalog_name,source_content_revision,exported_utc FROM exchange_origin WHERE singleton=1;
                """, transaction);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                if (!ExchangePackage.IsUuid(reader.GetString(0))) throw new CatalogStorageException("Неверный идентификатор источника обмена.");
                sourceId = Guid.Parse(reader.GetString(0));
                name = reader.GetString(1);
                revision = reader.GetInt64(2);
                time = reader.GetString(3);
            }
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || revision < 0 || !ExchangePackage.IsTime(time))
            throw new CatalogStorageException("Повреждены сведения об источнике каталога.");
        return (catalogId, sourceId, name, revision, time);
    }

    internal static List<string> ExchangeReadinessIssues(SqliteConnection connection, string material, SqliteTransaction transaction) =>
        ReadinessIssues(connection, material, transaction);

    internal static IReadOnlyList<ExchangeRow> ReadExchangeRows(SqliteConnection connection, string table,
        string predicate = "1=1", SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        if (!ExchangeSchema.TableNames.Contains(table)) throw new ArgumentException("Unknown exchange table.", nameof(table));
        using var command = Command(connection, $"SELECT * FROM {table} WHERE {predicate} ORDER BY id;", transaction, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<ExchangeRow>();
        while (reader.Read())
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var column = 0; column < reader.FieldCount; column++)
            {
                var name = reader.GetName(column);
                values.Add(name, name == "source_batch_id" || reader.IsDBNull(column) ? null : reader.GetValue(column));
            }
            result.Add(new(values));
        }
        return result.AsReadOnly();
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> ReadExchangeSelection(SqliteConnection connection, IReadOnlyList<Guid> ids, SqliteTransaction transaction)
    {
        const string selected = "(SELECT value FROM json_each($ids))";
        const string tags = "(SELECT tag_id FROM material_tags WHERE material_id IN " + selected + ")";
        const string groups = "(SELECT group_id FROM tags WHERE id IN " + tags + ")";
        var predicates = new Dictionary<string, string> {
            ["materials"] = "id IN " + selected,
            ["material_translations"] = "material_id IN " + selected,
            ["material_images"] = "material_id IN " + selected,
            ["elements"] = "material_id IN " + selected,
            ["regions"] = "material_id IN " + selected,
            ["material_tags"] = "material_id IN " + selected,
            ["training_prompts"] = "material_id IN " + selected,
            ["training_prompt_translations"] = "prompt_id IN (SELECT id FROM training_prompts WHERE material_id IN " + selected + ")",
            ["element_translations"] = "element_id IN (SELECT id FROM elements WHERE material_id IN " + selected + ")",
            ["images"] = "id IN (SELECT image_id FROM material_images WHERE material_id IN " + selected + ")",
            ["image_calibrations"] = "material_image_id IN (SELECT id FROM material_images WHERE material_id IN " + selected + ")",
            ["tags"] = "id IN " + tags,
            ["tag_translations"] = "tag_id IN " + tags,
            ["tag_groups"] = "is_system=1 OR id IN " + groups,
            ["tag_group_translations"] = "group_id IN (SELECT id FROM tag_groups WHERE is_system=1 OR id IN " + groups + ")",
            ["analog_groups"] = "id IN (SELECT analog_group_id FROM materials WHERE id IN " + selected + ")",
            ["analog_group_translations"] = "group_id IN (SELECT analog_group_id FROM materials WHERE id IN " + selected + ")"
        };
        var json = JsonSerializer.Serialize(ids.Select(id => id.ToString()));
        return new ReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>>(ExchangeSchema.TableNames.ToDictionary(table => table,
            table => ReadExchangeRows(connection, table, predicates[table], transaction, ("$ids", json)), StringComparer.Ordinal));
    }
}

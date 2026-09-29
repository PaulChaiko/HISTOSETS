using HistOSets.Core;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

public sealed partial class CatalogStore
{
    public CatalogTaxonomy LoadTaxonomy()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var connection = Open(DatabasePath);
            var groupNames = ReadNames(connection, "tag_group_translations", "group_id");
            var tagNames = ReadNames(connection, "tag_translations", "tag_id");
            var groups = new List<CatalogTagGroup>();
            using (var command = Command(connection, "SELECT id FROM tag_groups WHERE deleted_utc IS NULL ORDER BY rowid;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) groups.Add(new(Guid.Parse(reader.GetString(0)), groupNames.GetValueOrDefault(reader.GetString(0)) ?? new Dictionary<string, string>()));
            var tags = new List<CatalogTag>();
            using (var command = Command(connection, "SELECT id, group_id FROM tags WHERE deleted_utc IS NULL ORDER BY rowid;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) tags.Add(new(Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                    tagNames.GetValueOrDefault(reader.GetString(0)) ?? new Dictionary<string, string>()));
            var assignments = new Dictionary<Guid, HashSet<Guid>>();
            using (var command = Command(connection, """
                SELECT mt.material_id, mt.tag_id FROM material_tags mt
                JOIN tags t ON t.id = mt.tag_id AND t.deleted_utc IS NULL
                JOIN materials m ON m.id = mt.material_id AND m.deleted_utc IS NULL
                WHERE mt.deleted_utc IS NULL;
                """))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var material = Guid.Parse(reader.GetString(0));
                    if (!assignments.TryGetValue(material, out var set)) assignments[material] = set = [];
                    set.Add(Guid.Parse(reader.GetString(1)));
                }
            return new(groups, tags, assignments.ToDictionary(p => p.Key, p => (IReadOnlySet<Guid>)p.Value));
        }
    }

    private static Dictionary<string, Dictionary<string, string>> ReadNames(SqliteConnection connection, string table, string parent)
    {
        var names = new Dictionary<string, Dictionary<string, string>>();
        using var command = Command(connection, $"SELECT {parent}, locale, name FROM {table} WHERE deleted_utc IS NULL;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!names.TryGetValue(reader.GetString(0), out var set)) names[reader.GetString(0)] = set = new();
            set[reader.GetString(1)] = reader.GetString(2);
        }
        return names;
    }

    public Guid SaveTagGroup(Guid? id, string russian, string english)
        => SaveTaxonomyItem(true, id, null, russian, english);

    public Guid SaveTag(Guid? id, Guid? groupId, string russian, string english)
        => SaveTaxonomyItem(false, id, groupId, russian, english);

    private Guid SaveTaxonomyItem(bool group, Guid? existing, Guid? groupId, string russian, string english)
    {
        var names = new Dictionary<string, string> { ["ru"] = russian.Trim(), ["en"] = english.Trim() };
        if (names.Values.All(string.IsNullOrWhiteSpace)) throw new CatalogStorageException("Укажите название хотя бы на одном языке.");
        if (names.Values.Any(n => n.Length > 120 || n.Any(char.IsControl)))
            throw new CatalogStorageException("Название должно быть одной строкой длиной до 120 символов.");
        return WriteTags((connection, transaction, now, origin) =>
        {
            var table = group ? "tag_groups" : "tags";
            var translations = group ? "tag_group_translations" : "tag_translations";
            var parent = group ? "group_id" : "tag_id";
            var id = existing ?? Guid.NewGuid();
            if (existing is not null) RequireActive(connection, transaction, table, id);
            if (!group && groupId is { } g) RequireActive(connection, transaction, "tag_groups", g);
            using (var command = Command(connection, $"""
                SELECT t.id, n.locale, n.name FROM {table} t JOIN {translations} n ON n.{parent} = t.id
                WHERE t.deleted_utc IS NULL AND n.deleted_utc IS NULL AND t.id <> $id
                {(group ? "" : "AND t.group_id IS $group")};
                """, transaction, ("$id", id.ToString()), ("$group", groupId?.ToString())))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    if (names.TryGetValue(reader.GetString(1), out var name) && name.Length > 0 && CatalogBrowser.Normalize(reader.GetString(2)) == CatalogBrowser.Normalize(name))
                        throw new CatalogStorageException(group ? "Группа с таким названием уже есть." : "В этой группе уже есть тег с таким названием.");
            if (existing is null)
                Execute(connection, $"""
                    INSERT INTO {table}(id, code, {(group ? "" : "group_id,")} created_utc, modified_utc, origin_catalog_id)
                    VALUES ($id, $code, {(group ? "" : "$group,")} $now, $now, $origin);
                    """, transaction, ("$id", id.ToString()), ("$code", (group ? "group-" : "tag-") + id.ToString("N")),
                    ("$group", groupId?.ToString()), ("$now", now), ("$origin", origin));
            var changed = false;
            if (!group && existing is not null)
            {
                var previous = Scalar(connection, "SELECT group_id FROM tags WHERE id = $id;", transaction, ("$id", id.ToString())) as string;
                if (previous != groupId?.ToString())
                {
                    Execute(connection, "UPDATE tags SET group_id = $group WHERE id = $id;", transaction, ("$group", groupId?.ToString()), ("$id", id.ToString()));
                    changed = true;
                }
            }
            foreach (var (locale, name) in names)
            {
                var previous = Scalar(connection, $"SELECT name FROM {translations} WHERE {parent} = $id AND locale = $locale AND deleted_utc IS NULL;", transaction,
                    ("$id", id.ToString()), ("$locale", locale)) as string;
                if (previous == name || previous is null && name.Length == 0) continue;
                changed = true;
                if (name.Length == 0)
                    Execute(connection, $"UPDATE {translations} SET deleted_utc = $now, modified_utc = $now, revision = revision + 1 WHERE {parent} = $id AND locale = $locale;",
                        transaction, ("$id", id.ToString()), ("$locale", locale), ("$now", now));
                else
                    Execute(connection, $"""
                        INSERT INTO {translations}(id, {parent}, locale, name, created_utc, modified_utc, origin_catalog_id)
                        VALUES ($translation, $id, $locale, $name, $now, $now, $origin)
                        ON CONFLICT({parent}, locale) DO UPDATE SET name = $name, deleted_utc = NULL, modified_utc = $now, revision = revision + 1;
                        """, transaction, ("$translation", Guid.NewGuid().ToString()), ("$id", id.ToString()), ("$locale", locale),
                        ("$name", name), ("$now", now), ("$origin", origin));
            }
            if (existing is not null && changed) Touch(connection, transaction, table, id, now);
            return id;
        });
    }

    public void SetMaterialTags(Guid materialId, IReadOnlyCollection<Guid> tagIds)
    {
        WriteTags((connection, transaction, now, origin) =>
        {
            RequireActive(connection, transaction, "materials", materialId);
            var desired = tagIds.ToHashSet();
            foreach (var tag in desired) RequireActive(connection, transaction, "tags", tag);
            var previous = new HashSet<Guid>();
            using (var command = Command(connection, "SELECT tag_id FROM material_tags WHERE material_id = $id AND deleted_utc IS NULL;", transaction, ("$id", materialId.ToString())))
            using (var reader = command.ExecuteReader()) while (reader.Read()) previous.Add(Guid.Parse(reader.GetString(0)));
            foreach (var removed in previous.Except(desired))
                Execute(connection, "UPDATE material_tags SET deleted_utc = $now, modified_utc = $now, revision = revision + 1 WHERE material_id = $material AND tag_id = $tag;",
                    transaction, ("$now", now), ("$material", materialId.ToString()), ("$tag", removed.ToString()));
            foreach (var added in desired.Except(previous))
                Execute(connection, """
                    INSERT INTO material_tags(id, material_id, tag_id, created_utc, modified_utc, origin_catalog_id)
                    VALUES ($id, $material, $tag, $now, $now, $origin)
                    ON CONFLICT(material_id, tag_id) DO UPDATE SET deleted_utc = NULL, modified_utc = $now, revision = revision + 1;
                    """, transaction, ("$id", Guid.NewGuid().ToString()), ("$material", materialId.ToString()), ("$tag", added.ToString()), ("$now", now), ("$origin", origin));
            if (!previous.SetEquals(desired)) Touch(connection, transaction, "materials", materialId, now);
            return true;
        });
    }

    public void DeleteTag(Guid id)
    {
        WriteTags((connection, transaction, now, _) =>
        {
            RequireActive(connection, transaction, "tags", id);
            Execute(connection, """
                UPDATE materials SET modified_utc = $now, revision = revision + 1 WHERE id IN
                (SELECT material_id FROM material_tags WHERE tag_id = $id AND deleted_utc IS NULL);
                UPDATE material_tags SET deleted_utc = $now, modified_utc = $now, revision = revision + 1 WHERE tag_id = $id AND deleted_utc IS NULL;
                UPDATE tag_translations SET deleted_utc = $now, modified_utc = $now, revision = revision + 1 WHERE tag_id = $id AND deleted_utc IS NULL;
                UPDATE tags SET deleted_utc = $now, modified_utc = $now, revision = revision + 1 WHERE id = $id;
                """, transaction, ("$id", id.ToString()), ("$now", now));
            return true;
        });
    }

    public void DeleteTagGroup(Guid id)
    {
        WriteTags((connection, transaction, now, _) =>
        {
            RequireActive(connection, transaction, "tag_groups", id);
            if (Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM tags WHERE group_id = $id AND deleted_utc IS NULL;", transaction, ("$id", id.ToString()))) > 0)
                throw new CatalogStorageException("Сначала перенесите или удалите теги этой группы.");
            Execute(connection, """
                UPDATE tag_group_translations SET deleted_utc = $now, modified_utc = $now, revision = revision + 1 WHERE group_id = $id AND deleted_utc IS NULL;
                UPDATE tag_groups SET deleted_utc = $now, modified_utc = $now, revision = revision + 1 WHERE id = $id;
                """, transaction, ("$id", id.ToString()), ("$now", now));
            return true;
        });
    }

    private static void RequireActive(SqliteConnection connection, SqliteTransaction transaction, string table, Guid id)
    {
        if (Convert.ToInt64(Scalar(connection, $"SELECT COUNT(*) FROM {table} WHERE id = $id AND deleted_utc IS NULL;", transaction, ("$id", id.ToString()))) != 1)
            throw new CatalogStorageException("Запись не найдена или удалена. Обновите каталог.");
    }

    private static void Touch(SqliteConnection connection, SqliteTransaction transaction, string table, Guid id, string now)
        => Execute(connection, $"UPDATE {table} SET modified_utc = $now, revision = revision + 1 WHERE id = $id;", transaction, ("$id", id.ToString()), ("$now", now));

    private T WriteTags<T>(Func<SqliteConnection, SqliteTransaction, string, string, T> operation)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                using var connection = Open(DatabasePath, SqliteOpenMode.ReadWrite);
                Validate(connection);
                using var transaction = connection.BeginTransaction();
                var result = operation(connection, transaction, StorageFiles.UtcNow(), (string)Scalar(connection, "SELECT catalog_id FROM catalog_metadata;", transaction)!);
                transaction.Commit();
                return result;
            }
            catch (SqliteException ex) { throw new CatalogStorageException("Не удалось сохранить теги. Изменения отменены.\n" + ex.Message, ex); }
        }
    }
}

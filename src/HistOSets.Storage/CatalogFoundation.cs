using HistOSets.Core;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

public sealed partial class CatalogStore
{
    private static string DefaultCatalogName(string root)
    {
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        name = new string(name.Where(c => !char.IsControl(c)).Take(200).ToArray()).Trim();
        return name.Length == 0 ? "Каталог" : name;
    }

    public CatalogMetadata GetMetadata()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var connection = Open(DatabasePath);
            using var command = Command(connection, "SELECT catalog_id,name,format_version,content_revision,created_utc,modified_utc FROM catalog_metadata;");
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new CatalogStorageException("Отсутствуют сведения о каталоге.");
            return new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3),
                DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public void RenameCatalog(string name)
    {
        name = ValidateName(name, 200);
        WriteTags((connection, transaction, now, _) =>
        {
            if ((string)Scalar(connection, "SELECT name FROM catalog_metadata;", transaction)! != name)
                Execute(connection, "UPDATE catalog_metadata SET name=$name WHERE singleton=1;", transaction, ("$name", name));
            return true;
        });
    }

    private static string ValidateName(string name, int limit)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > limit || name.Any(char.IsControl))
            throw new CatalogStorageException($"Укажите непустое название одной строкой, до {limit} символов.");
        return name;
    }

    public IReadOnlyList<CatalogAnalogGroup> LoadAnalogGroups()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var connection = Open(DatabasePath);
            var names = ReadNames(connection, "analog_group_translations", "group_id");
            var groups = new List<CatalogAnalogGroup>();
            using var command = Command(connection, "SELECT id FROM analog_groups WHERE deleted_utc IS NULL ORDER BY created_utc,id;");
            using var reader = command.ExecuteReader();
            while (reader.Read()) groups.Add(new(Guid.Parse(reader.GetString(0)), names.GetValueOrDefault(reader.GetString(0)) ?? new Dictionary<string,string>()));
            return groups;
        }
    }

    public Guid SaveAnalogGroup(Guid? id, string russian, string english)
    {
        var names = new Dictionary<string,string> { ["ru"] = ValidateName(russian, 200), ["en"] = ValidateName(english, 200) };
        return WriteTags((connection, transaction, now, origin) =>
        {
            var key = id ?? Guid.NewGuid();
            if (id is not null) RequireActive(connection, transaction, "analog_groups", key);
            using (var command = Command(connection, """
                SELECT t.locale,t.name FROM analog_group_translations t JOIN analog_groups g ON g.id=t.group_id
                WHERE g.deleted_utc IS NULL AND t.deleted_utc IS NULL AND g.id<>$id;
                """, transaction, ("$id", key.ToString())))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    if (names.TryGetValue(reader.GetString(0), out var name) && CatalogBrowser.Normalize(name) == CatalogBrowser.Normalize(reader.GetString(1)))
                        throw new CatalogStorageException("Группа аналогов с таким названием уже существует.");
            if (id is null)
                Execute(connection, "INSERT INTO analog_groups(id,created_utc,modified_utc,origin_catalog_id) VALUES($id,$now,$now,$origin);", transaction,
                    ("$id", key.ToString()), ("$now", now), ("$origin", origin));
            var changed = false;
            foreach (var (locale, name) in names)
            {
                var previous = Scalar(connection, "SELECT name FROM analog_group_translations WHERE group_id=$id AND locale=$locale AND deleted_utc IS NULL;", transaction,
                    ("$id", key.ToString()), ("$locale", locale)) as string;
                if (previous == name) continue;
                changed = true;
                Execute(connection, """
                    INSERT INTO analog_group_translations(id,group_id,locale,name,created_utc,modified_utc,origin_catalog_id)
                    VALUES($translation,$id,$locale,$name,$now,$now,$origin)
                    ON CONFLICT(group_id,locale) DO UPDATE SET name=$name,deleted_utc=NULL,modified_utc=$now,revision=revision+1;
                    """, transaction, ("$translation", Guid.NewGuid().ToString()), ("$id", key.ToString()), ("$locale", locale),
                    ("$name", name), ("$now", now), ("$origin", origin));
            }
            if (id is not null && changed) Touch(connection, transaction, "analog_groups", key, now);
            return key;
        });
    }

    public void DeleteAnalogGroup(Guid id)
    {
        WriteTags((connection, transaction, now, _) =>
        {
            RequireActive(connection, transaction, "analog_groups", id);
            if (Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM materials WHERE analog_group_id=$id AND deleted_utc IS NULL;", transaction, ("$id", id.ToString()))) > 0)
                throw new CatalogStorageException("Группа используется объектами. Сначала снимите назначения или перенесите объекты в другую группу.");
            Execute(connection, """
                UPDATE analog_group_translations SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE group_id=$id AND deleted_utc IS NULL;
                UPDATE analog_groups SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE id=$id;
                """, transaction, ("$id", id.ToString()), ("$now", now));
            return true;
        });
    }

    public void SetMaterialAnalogGroup(Guid materialId, Guid? groupId)
    {
        WriteTags((connection, transaction, now, _) =>
        {
            RequireActive(connection, transaction, "materials", materialId);
            if (groupId is { } group) RequireActive(connection, transaction, "analog_groups", group);
            var previous = Scalar(connection, "SELECT analog_group_id FROM materials WHERE id=$id;", transaction, ("$id", materialId.ToString())) as string;
            if (previous != groupId?.ToString())
            {
                Execute(connection, "UPDATE materials SET analog_group_id=$group WHERE id=$id;", transaction, ("$group", groupId?.ToString()), ("$id", materialId.ToString()));
                Touch(connection, transaction, "materials", materialId, now);
            }
            return true;
        });
    }

    public void SetPrimaryImage(Guid materialId, Guid materialImageId)
    {
        WriteTags((connection, transaction, now, _) =>
        {
            RequireActive(connection, transaction, "materials", materialId);
            if (Convert.ToInt64(Scalar(connection, """
                SELECT COUNT(*) FROM material_images mi JOIN images i ON i.id=mi.image_id AND i.deleted_utc IS NULL
                WHERE mi.id=$image AND mi.material_id=$material AND mi.deleted_utc IS NULL;
                """, transaction, ("$image", materialImageId.ToString()), ("$material", materialId.ToString()))) != 1)
                throw new CatalogStorageException("Изображение не принадлежит выбранному объекту или удалено.");
            if (Convert.ToInt64(Scalar(connection, "SELECT is_primary FROM material_images WHERE id=$id;", transaction, ("$id", materialImageId.ToString()))) == 1) return true;
            Execute(connection, "UPDATE material_images SET is_primary=0,modified_utc=$now,revision=revision+1 WHERE material_id=$material AND is_primary=1 AND deleted_utc IS NULL;",
                transaction, ("$material", materialId.ToString()), ("$now", now));
            Execute(connection, "UPDATE material_images SET is_primary=1,modified_utc=$now,revision=revision+1 WHERE id=$id;", transaction,
                ("$id", materialImageId.ToString()), ("$now", now));
            Touch(connection, transaction, "materials", materialId, now);
            return true;
        });
    }

    public MaterialReadiness CheckReadiness(Guid materialId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var connection = Open(DatabasePath);
            var issues = ReadinessIssues(connection, materialId.ToString());
            AddFileIssues(connection, null, materialId, issues);
            return new(materialId, issues.Count == 0, issues);
        }
    }

    public void SetMaterialStatus(Guid materialId, string status)
    {
        if (status is not ("draft" or "ready")) throw new CatalogStorageException("Неизвестное состояние объекта.");
        WriteTags((connection, transaction, now, _) =>
        {
            RequireActive(connection, transaction, "materials", materialId);
            if (status == "ready")
            {
                var issues = ReadinessIssues(connection, materialId.ToString(), transaction);
                AddFileIssues(connection, transaction, materialId, issues);
                if (issues.Count > 0) throw new CatalogStorageException("Объект пока не готов:\n" + string.Join("\n", issues));
            }
            if ((string)Scalar(connection, "SELECT status FROM materials WHERE id=$id;", transaction, ("$id", materialId.ToString()))! != status)
            {
                Execute(connection, "UPDATE materials SET status=$status WHERE id=$id;", transaction, ("$status", status), ("$id", materialId.ToString()));
                Touch(connection, transaction, "materials", materialId, now);
            }
            return true;
        });
    }

    private void AddFileIssues(SqliteConnection connection, SqliteTransaction? transaction, Guid materialId, List<string> issues)
    {
        using var command = Command(connection, """
            SELECT i.storage_key,i.sha256,i.byte_size FROM material_images mi JOIN images i ON i.id=mi.image_id
            WHERE mi.material_id=$id AND mi.deleted_utc IS NULL AND i.deleted_utc IS NULL;
            """, transaction, ("$id", materialId.ToString()));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = StorageFiles.Resolve(RootDirectory, reader.GetString(0));
            if (!File.Exists(path) || new FileInfo(path).Length != reader.GetInt64(2) || StorageFiles.Hash(path) != reader.GetString(1))
                issues.Add("Изображение отсутствует или изменено: " + reader.GetString(0));
        }
    }

    internal static void RefreshImportedStatuses(SqliteConnection connection, SqliteTransaction transaction)
    {
        var ids = new List<string>();
        using (var command = Command(connection, "SELECT id FROM materials WHERE deleted_utc IS NULL;", transaction))
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
        foreach (var id in ids)
            Execute(connection, "UPDATE materials SET status=$status WHERE id=$id;", transaction,
                ("$status", ReadinessIssues(connection, id, transaction).Count == 0 ? "ready" : "draft"), ("$id", id));
    }

    private static void DemoteIncompleteMaterials(SqliteConnection connection, SqliteTransaction transaction, string now)
    {
        var ids = new List<string>();
        using (var command = Command(connection, "SELECT id FROM materials WHERE status='ready' AND deleted_utc IS NULL;", transaction))
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
        foreach (var id in ids)
            if (ReadinessIssues(connection, id, transaction).Count > 0)
                Execute(connection, "UPDATE materials SET status='draft',modified_utc=$now,revision=revision+1 WHERE id=$id;", transaction, ("$now", now), ("$id", id));
    }

    private static List<string> ReadinessIssues(SqliteConnection connection, string material, SqliteTransaction? transaction = null)
    {
        var issues = new List<string>();
        if (Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM materials WHERE id=$id AND deleted_utc IS NULL;", transaction, ("$id", material))) != 1)
            throw new CatalogStorageException("Объект не найден или удалён.");
        if (Convert.ToInt64(Scalar(connection, """
            SELECT COUNT(*) FROM material_images mi JOIN images i ON i.id=mi.image_id AND i.deleted_utc IS NULL
            WHERE mi.material_id=$id AND mi.deleted_utc IS NULL AND mi.is_primary=1;
            """, transaction, ("$id", material))) != 1) issues.Add("Выберите основное изображение.");
        void Texts(string table, string parent, string id, string label, bool descriptions)
        {
            foreach (var locale in new[] { "ru", "en" })
            {
                using var command = Command(connection, $"SELECT name{(descriptions ? ",summary,description" : "")} FROM {table} WHERE {parent}=$id AND locale=$locale AND deleted_utc IS NULL;", transaction,
                    ("$id", id), ("$locale", locale));
                using var reader = command.ExecuteReader();
                if (!reader.Read()) { issues.Add(label + ": отсутствует перевод " + locale.ToUpperInvariant() + "."); continue; }
                for (var n = 0; n < (descriptions ? 3 : 1); n++)
                    if (string.IsNullOrWhiteSpace(reader.GetString(n)))
                        issues.Add(label + " (" + locale.ToUpperInvariant() + "): заполните " + new[] { "название", "краткое описание", "полное описание" }[n] + ".");
            }
        }
        Texts("material_translations", "material_id", material, "Объект", true);
        var elements = new List<string>();
        using (var command = Command(connection, "SELECT id FROM elements WHERE material_id=$id AND deleted_utc IS NULL ORDER BY sort_order,id;", transaction, ("$id", material)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) elements.Add(reader.GetString(0));
        for (var n = 0; n < elements.Count; n++) Texts("element_translations", "element_id", elements[n], "Элемент " + (n + 1), true);
        var group = Scalar(connection, "SELECT analog_group_id FROM materials WHERE id=$id;", transaction, ("$id", material)) as string;
        if (group is not null)
        {
            if (Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM analog_groups WHERE id=$id AND deleted_utc IS NULL;", transaction, ("$id", group))) != 1)
                issues.Add("Группа аналогов удалена.");
            Texts("analog_group_translations", "group_id", group, "Группа аналогов", false);
        }
        var tags = new List<(string Id, string? Group)>();
        using (var command = Command(connection, """
            SELECT t.id,t.group_id FROM material_tags mt JOIN tags t ON t.id=mt.tag_id AND t.deleted_utc IS NULL
            WHERE mt.material_id=$id AND mt.deleted_utc IS NULL;
            """, transaction, ("$id", material)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) tags.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        foreach (var tag in tags)
        {
            Texts("tag_translations", "tag_id", tag.Id, "Тег", false);
            if (tag.Group is null) issues.Add("У тега не выбран тип.");
            else Texts("tag_group_translations", "group_id", tag.Group, "Тип тега", false);
        }
        return issues;
    }

    private static void TouchCatalog(SqliteConnection connection, SqliteTransaction transaction, string now)
        => Execute(connection, "UPDATE catalog_metadata SET modified_utc=$now,content_revision=content_revision+1 WHERE singleton=1;", transaction, ("$now", now));
}

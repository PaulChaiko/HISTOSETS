using System.Collections.ObjectModel;
using System.IO.Compression;
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

    /// <summary>Export only selected roots and their dependencies. Existing destinations are never overwritten.</summary>
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
            if (File.Exists(destination)) throw new CatalogStorageException("Файл уже существует. Выберите новое имя пакета обмена.");
            var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
            var stage = Path.Combine(Path.GetTempPath(), "histosets-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> tables;
                CatalogMetadata metadata;
                using (var connection = Open(DatabasePath))
                using (var transaction = connection.BeginTransaction(deferred: true))
                {
                    Validate(connection, transaction: transaction);
                    tables = ReadExchangeSelection(connection, selected, transaction);
                    using var command = Command(connection, "SELECT catalog_id,name,format_version,content_revision,created_utc,modified_utc FROM catalog_metadata;", transaction);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read()) throw new CatalogStorageException("Отсутствуют сведения о каталоге.");
                    metadata = new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3),
                        DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                        DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture));
                }
                if (tables["materials"].Count != selected.Length)
                    throw new CatalogStorageException("Выбранный объект больше не существует. Обновите список экспорта.");
                if (tables.Sum(t => t.Value.Count) > ExchangePackage.MaxRows) throw new CatalogStorageException("В выбранных объектах слишком много записей для одного пакета.");
                var graphPath = Path.Combine(stage, "graph.json");
                using (var stream = new FileStream(graphPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    foreach (var table in ExchangeSchema.TableNames)
                    {
                        writer.WritePropertyName(table); writer.WriteStartArray();
                        foreach (var row in tables[table]) JsonSerializer.Serialize(writer, row.Values);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                }
                var graphSize = new FileInfo(graphPath).Length;
                if (graphSize > ExchangePackage.MaxGraphBytes) throw new CatalogStorageException("Описания выбранных объектов превышают предел пакета 128 МиБ.");
                var files = new List<ExchangePackage.ExchangeFile> { new("graph.json", StorageFiles.Hash(graphPath), graphSize) };
                files.AddRange(tables["images"].Select(r => new ExchangePackage.ExchangeFile(r.String("storage_key")!, r.String("sha256")!, r.Integer("byte_size"))));
                if (files.Count > ExchangePackage.MaxFiles + 1 || files.Any(f => f.Size <= 0 || f.Size > ExchangePackage.MaxBytes) ||
                    files.Aggregate(0L, (sum, f) => checked(sum + f.Size)) > ExchangePackage.MaxBytes)
                    throw new CatalogStorageException("Выбранные изображения превышают предел пакета 64 ГиБ.");
                var manifest = new ExchangePackage.ExchangeManifest(1, Guid.NewGuid().ToString(), metadata.Id.ToString(), metadata.Name,
                    metadata.ContentRevision, StorageFiles.UtcNow(), files);
                using (var archive = ZipFile.Open(partial, ZipArchiveMode.Create))
                {
                    foreach (var file in files)
                    {
                        var path = file.Path == "graph.json" ? graphPath : StorageFiles.Resolve(RootDirectory, file.Path);
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var output = archive.CreateEntry(file.Path, CompressionLevel.Fastest).Open();
                        ExchangePackage.CopyVerified(input, output, file);
                    }
                    using var outputManifest = archive.CreateEntry("manifest.json").Open();
                    JsonSerializer.Serialize(outputManifest, manifest);
                }
                File.Move(partial, destination);
            }
            finally
            {
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                try { if (File.Exists(partial)) File.Delete(partial); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
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

    private static IReadOnlyDictionary<string, IReadOnlyList<ExchangeRow>> ReadExchangeSelection(SqliteConnection connection, IReadOnlyList<Guid> ids, SqliteTransaction transaction)
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

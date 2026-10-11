using System.Security.Cryptography;
using System.Text;
using HistOSets.Core;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

public sealed partial class CatalogStore
{
    /// <summary>
    /// Captures a read-only, coherent database snapshot, including incomplete cards. Files are checked once
    /// per original, and failures remain per-image eligibility issues rather than hiding the whole card.
    /// Supplying a decoder additionally verifies that the pixels match the stored dimensions and format.
    /// </summary>
    public TrainingCatalog CaptureTrainingCatalog(Func<string, ImageMetadata>? readMetadata = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var connection = Open(DatabasePath);
            using var transaction = connection.BeginTransaction(deferred: true);
            Validate(connection, transaction: transaction);
            using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            FingerprintTrainingRows(connection, transaction, fingerprint);
            Guid catalogId;
            long revision;
            using (var command = Command(connection, "SELECT catalog_id,content_revision FROM catalog_metadata;", transaction))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new CatalogStorageException("Отсутствуют сведения о каталоге.");
                catalogId = Guid.Parse(reader.GetString(0)); revision = reader.GetInt64(1);
            }
            var materialNames = ReadTrainingNames(connection, transaction, "material_translations", "material_id");
            var elementNames = ReadTrainingNames(connection, transaction, "element_translations", "element_id");
            var analogNames = ReadTrainingNames(connection, transaction, "analog_group_translations", "group_id");
            var analogs = new List<TrainingAnalogGroup>();
            using (var command = Command(connection, "SELECT id FROM analog_groups WHERE deleted_utc IS NULL ORDER BY id;", transaction))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) analogs.Add(new(Guid.Parse(reader.GetString(0)), analogNames.GetValueOrDefault(reader.GetString(0)) ?? new()));

            var verified = new Dictionary<string, TrainingAsset>(StringComparer.Ordinal);
            using (var command = Command(connection, """
                SELECT i.id,i.storage_key,i.sha256,i.byte_size,i.pixel_width,i.pixel_height,i.dpi_x,i.dpi_y,i.mime_type,i.orientation,i.deleted_utc
                FROM images i WHERE i.id IN
                    (SELECT mi.image_id FROM material_images mi JOIN materials m ON m.id=mi.material_id
                     WHERE mi.deleted_utc IS NULL AND m.deleted_utc IS NULL)
                ORDER BY i.id;
                """, transaction))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var imageId = reader.GetString(0);
                    var asset = InspectTrainingImage(reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                        new(reader.GetInt32(4), reader.GetInt32(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetString(8), reader.GetInt32(9)),
                        !reader.IsDBNull(10), readMetadata);
                    verified.Add(imageId, asset);
                    AppendTrainingFingerprint(fingerprint, "file", imageId, asset.ObservedHash, asset.Issue);
                }

            var materials = new List<TrainingMaterial>();
            using (var command = Command(connection, "SELECT id,revision,status,analog_group_id FROM materials WHERE deleted_utc IS NULL ORDER BY created_utc,id;", transaction))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var material = reader.GetString(0);
                    var materialId = Guid.Parse(material);
                    var images = new List<TrainingImage>();
                    using (var imageCommand = Command(connection, "SELECT id,image_id,is_primary,sort_order FROM material_images WHERE material_id=$id AND deleted_utc IS NULL ORDER BY sort_order,id;",
                        transaction, ("$id", material)))
                    using (var imageReader = imageCommand.ExecuteReader())
                        while (imageReader.Read())
                        {
                            var asset = verified[imageReader.GetString(1)];
                            images.Add(new(Guid.Parse(imageReader.GetString(0)), Guid.Parse(imageReader.GetString(1)), asset.ExpectedHash,
                                imageReader.GetBoolean(2), imageReader.GetInt32(3), asset.Width, asset.Height,
                                asset.Issue is null, asset.Issue, asset.Path));
                        }
                    var elements = new List<TrainingElement>();
                    using (var elementCommand = Command(connection, "SELECT id,sort_order FROM elements WHERE material_id=$id AND deleted_utc IS NULL ORDER BY sort_order,id;",
                        transaction, ("$id", material)))
                    using (var elementReader = elementCommand.ExecuteReader())
                        while (elementReader.Read())
                        {
                            var element = elementReader.GetString(0);
                            var elementId = Guid.Parse(element);
                            var regions = new List<TrainingRegion>();
                            using (var regionCommand = Command(connection, "SELECT id,material_image_id,geometry_json FROM regions WHERE element_id=$id AND deleted_utc IS NULL ORDER BY sort_order,id;",
                                transaction, ("$id", element)))
                            using (var regionReader = regionCommand.ExecuteReader())
                                while (regionReader.Read()) regions.Add(new(Guid.Parse(regionReader.GetString(0)),
                                    Guid.Parse(regionReader.GetString(1)), RegionGeometryCodec.Deserialize(regionReader.GetString(2))));
                            elements.Add(new(elementId, elementNames.GetValueOrDefault(element) ?? new(), elementReader.GetInt32(1), regions,
                                ReadTrainingDescriptions(connection, transaction, materialId, elementId)));
                        }
                    var tags = new List<Guid>();
                    using (var tagCommand = Command(connection, """
                        SELECT mt.tag_id FROM material_tags mt JOIN tags t ON t.id=mt.tag_id AND t.deleted_utc IS NULL
                        WHERE mt.material_id=$id AND mt.deleted_utc IS NULL ORDER BY mt.tag_id;
                        """, transaction, ("$id", material)))
                    using (var tagReader = tagCommand.ExecuteReader())
                        while (tagReader.Read()) tags.Add(Guid.Parse(tagReader.GetString(0)));
                    materials.Add(new(materialId, reader.GetInt32(1), reader.GetString(2), materialNames.GetValueOrDefault(material) ?? new(),
                        reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), tags, images, elements,
                        ReadTrainingDescriptions(connection, transaction, materialId, null)));
                }
            var hash = Convert.ToHexStringLower(fingerprint.GetHashAndReset());
            transaction.Commit();
            return new(catalogId, revision, hash, materials, analogs);
        }
    }

    private sealed record TrainingAsset(string? Path, string ExpectedHash, string? ObservedHash, int Width, int Height, string? Issue);

    private TrainingAsset InspectTrainingImage(string key, string hash, long size, ImageMetadata expected,
        bool deleted, Func<string, ImageMetadata>? readMetadata)
    {
        string? path = null, observed = null, issue = null;
        try
        {
            path = StorageFiles.Resolve(RootDirectory, key);
            if (deleted) issue = "Изображение удалено из каталога: " + key;
            else if (!File.Exists(path)) issue = "Не найдено изображение: " + key;
            else
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                observed = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (stream.Length != size || !string.Equals(observed, hash, StringComparison.OrdinalIgnoreCase))
                    issue = "Изображение изменено вне HISTOSETS: " + key;
                else if (readMetadata is not null)
                {
                    var actual = readMetadata(path);
                    if (actual.Width != expected.Width || actual.Height != expected.Height || actual.Orientation != 1 ||
                        !string.Equals(actual.MimeType, expected.MimeType, StringComparison.OrdinalIgnoreCase))
                        issue = "Размеры или формат изображения не соответствуют каталогу: " + key;
                    // The decoder opens its own stream. Verify that both reads observed the same bytes.
                    stream.Position = 0;
                    var after = Convert.ToHexStringLower(SHA256.HashData(stream));
                    if (stream.Length != size || after != observed)
                    {
                        observed = after;
                        issue = "Изображение изменилось во время проверки: " + key;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CatalogStorageException or
            ArgumentException or FormatException or NotSupportedException or InvalidOperationException or CryptographicException)
        {
            issue = "Не удалось прочитать изображение «" + key + "»: " + ex.Message;
        }
        return new(path, hash, observed, expected.Width, expected.Height, issue);
    }

    private static IReadOnlyList<TrainingDescription> ReadTrainingDescriptions(SqliteConnection connection,
        SqliteTransaction transaction, Guid materialId, Guid? elementId)
    {
        var descriptions = new List<TrainingDescription>();
        using var command = Command(connection, """
            SELECT id,is_enabled,sort_order FROM training_prompts
            WHERE material_id=$material AND element_id IS $element AND deleted_utc IS NULL ORDER BY sort_order,id;
            """, transaction, ("$material", materialId.ToString()), ("$element", elementId?.ToString()));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var texts = new Dictionary<string, string>();
            using var textCommand = Command(connection, "SELECT locale,text FROM training_prompt_translations WHERE prompt_id=$id AND deleted_utc IS NULL ORDER BY locale;",
                transaction, ("$id", reader.GetString(0)));
            using var textReader = textCommand.ExecuteReader();
            while (textReader.Read()) texts.Add(textReader.GetString(0), textReader.GetString(1));
            descriptions.Add(new(Guid.Parse(reader.GetString(0)), texts, reader.GetBoolean(1), reader.GetInt32(2)));
        }
        return descriptions;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadTrainingNames(SqliteConnection connection,
        SqliteTransaction transaction, string table, string parent)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        using var command = Command(connection, $"SELECT {parent},locale,name FROM {table} WHERE deleted_utc IS NULL ORDER BY {parent},locale;", transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!result.TryGetValue(reader.GetString(0), out var names)) result[reader.GetString(0)] = names = new();
            names.Add(reader.GetString(1), reader.GetString(2));
        }
        return result;
    }

    private static void FingerprintTrainingRows(SqliteConnection connection, SqliteTransaction transaction, IncrementalHash hash)
    {
        // Include the actual values, not just revision counters: an external SQL edit must invalidate a plan too.
        // Identifiers are an application-owned allowlist; no package or user text becomes SQL.
        string[] tables = ["catalog_metadata", "materials", "material_translations", "images", "material_images", "elements",
            "element_translations", "regions", "image_calibrations", "tag_groups", "tag_group_translations", "tags",
            "tag_translations", "material_tags", "analog_groups", "analog_group_translations", "training_prompts", "training_prompt_translations"];
        foreach (var table in tables)
        {
            AppendTrainingFingerprint(hash, "table", table);
            using var command = Command(connection, $"SELECT * FROM {table} ORDER BY {(table == "catalog_metadata" ? "singleton" : "id")};", transaction);
            using var reader = command.ExecuteReader();
            for (var i = 0; i < reader.FieldCount; i++) AppendTrainingFingerprint(hash, "column", reader.GetName(i));
            while (reader.Read())
            {
                AppendTrainingFingerprint(hash, "row");
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.IsDBNull(i)) AppendTrainingFingerprint(hash, "null");
                    else
                    {
                        var value = reader.GetValue(i);
                        AppendTrainingFingerprint(hash, value.GetType().FullName,
                            value is double number ? number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                                : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
        }
    }

    private static void AppendTrainingFingerprint(IncrementalHash hash, params string?[] values)
    {
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = value is null ? null : Encoding.UTF8.GetBytes(value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes?.Length ?? -1);
            hash.AppendData(length);
            if (bytes is not null) hash.AppendData(bytes);
        }
    }
}

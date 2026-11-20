using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HistOSets.Core;
using HistOSets.Storage;
using Microsoft.Data.Sqlite;

internal static class NativeTestFixtures
{
    internal const string BaseName = "Препарат ' Альфа";
    internal const string RussianPrompt = "Эталонный вопрос по форме и цвету";
    internal const string EnglishPrompt = "Reference question about shape and color";
    internal const string Timestamp = "2026-01-02T03:04:05.0000000+00:00";
    internal static readonly string SubjectGroup = "f2b3cb86-ddf0-531b-99dd-650559a8ecb3";
    internal static readonly string OtherGroup = "519d1371-1b72-573a-ba0c-c0f2d93288d4";
    private const string RetiredXmlKey = "SOURCES/retired.xml";
    private static readonly string RetiredXmlDigest = Convert.ToHexStringLower(SHA256.HashData("original-format fixture"u8));

    internal sealed record LegacyCatalog(string Root, int Version, Guid CatalogId, Guid MaterialId, Guid ElementId, Guid TagId,
        Guid CustomTagId, Guid PromptId, Image[] Images)
    {
        internal string Database => Path.Combine(Root, "catalog.sqlite");
    }
    internal sealed record Image(Guid Id, Guid LinkId, Guid RegionId, string Key, string Hash, RegionGeometry Geometry);

    private static readonly string[] PhotoData = [
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAKElEQVR4nO3NsQ0AAAzCMM7u+X0CNkuZ41wybXsHAAAAAAAAAAAAxR6WwOAQWuxVuAAAAABJRU5ErkJggg==",
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAJklEQVR4nO3NsQkAAAjAsJ7t+V7hIASyp6ZbAoFAIBAIBAKB4EuwtqLgEN0giPcAAAAASUVORK5CYII="
    ];

    // Use the shipping historical SQL to make real schemas; construct the rows
    // here so these tests do not call the importer/migrator under test.
    internal static LegacyCatalog CreateLegacy(string directory, int version, bool includeSecondaryImage = false)
    {
        if (version is < 1 or > 6 || (includeSecondaryImage && version != 6))
            throw new ArgumentOutOfRangeException(nameof(version));
        Directory.CreateDirectory(Path.Combine(directory, "SPECIMENS"));
        var seed = new LegacyCatalog(directory, version, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new Image[includeSecondaryImage ? 2 : 1]);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = seed.Database, ForeignKeys = true, Pooling = false
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var files = new[] { "001_initial", "002_catalog_foundation", "003_editor_geometry", "004_compound_geometry",
            "005_catalog_exchange", "006_training_preparation" };
        for (int schema = 1; schema <= version; schema++)
        {
            using var stream = typeof(CatalogStore).Assembly.GetManifestResourceStream("HistOSets.Storage.Migrations." + files[schema - 1] + ".sql")!;
            using var reader = new StreamReader(stream);
            Execute(reader.ReadToEnd());
            if (schema == 1)
                Execute("PRAGMA application_id=1213420627; INSERT INTO catalog_metadata VALUES(1,$origin,1,$time);");
            if (schema == 2)
                Execute("UPDATE catalog_metadata SET name=$name,content_revision=9;", ("$name", "Учебный ' атлас"));
            if (schema == 3)
                Execute("""
                    DROP TABLE region_points;
                    DROP TABLE regions;
                    ALTER TABLE regions_v3 RENAME TO regions;
                    CREATE INDEX ix_regions_element ON regions(element_id,sort_order);
                    CREATE INDEX ix_regions_image ON regions(material_image_id);
                    """);
            Execute("INSERT INTO schema_migrations VALUES($version,$time);", ("$version", schema));
        }
        Execute("PRAGMA user_version=" + version + ";");

        var batch = Guid.NewGuid().ToString();
        Execute("""
            INSERT INTO import_batches(id,source_name,xml_sha256,dataset_sha256,source_key,mapping_kind,imported_utc)
            VALUES($batch,'retired.xml',$digest,$digest,$source,'baseline',$time);
            """, ("$batch", batch), ("$digest", RetiredXmlDigest), ("$source", RetiredXmlKey));
        Execute("""
            INSERT INTO materials(id,author_credit,rights_credit,created_utc,modified_utc,origin_catalog_id,source_batch_id)
            VALUES($material,'Авторы','CC0',$time,$time,$origin,$batch);
            INSERT INTO elements(id,material_id,sort_order,created_utc,modified_utc,origin_catalog_id,source_batch_id)
            VALUES($element,$material,0,$time,$time,$origin,$batch);
            """, ("$batch", batch));
        if (version >= 2) Execute("UPDATE materials SET status='ready';");
        foreach (var (locale, name, element, summary, description) in new[]
        {
            ("ru", BaseName, "Область", "Краткий текст", "Полное описание"),
            ("en", "Specimen Alpha", "Region", "Summary text", "Full description")
        })
        {
            Execute("""
                INSERT INTO material_translations(id,material_id,locale,name,summary,description,sort_order,created_utc,modified_utc,origin_catalog_id,source_batch_id)
                VALUES($text,$material,$locale,$name,$summary,$description,0,$time,$time,$origin,$batch);
                INSERT INTO element_translations(id,element_id,locale,name,summary,description,created_utc,modified_utc,origin_catalog_id,source_batch_id)
                VALUES($elementText,$element,$locale,$elementName,$summary,$description,$time,$time,$origin,$batch);
                """, ("$text", Guid.NewGuid().ToString()), ("$elementText", Guid.NewGuid().ToString()), ("$locale", locale),
                ("$name", name), ("$elementName", element), ("$summary", summary), ("$description", description), ("$batch", batch));
        }
        for (int i = 0; i < seed.Images.Length; i++)
        {
            var id = Guid.NewGuid();
            var link = Guid.NewGuid();
            var region = Guid.NewGuid();
            var bytes = Convert.FromBase64String(PhotoData[i]);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var key = "SPECIMENS/" + hash + ".png";
            File.WriteAllBytes(Path.Combine(directory, key), bytes);
            var geometry = i == 0
                ? RegionGeometry.Polygon([new(1.125, 2.75), new(30, 2.75), new(30, 29.375)])
                : RegionGeometry.Rectangle(4, 5, 12, 13);
            seed.Images[i] = new(id, link, region, key, hash, geometry);
            Execute("""
                INSERT INTO images(id,storage_key,sha256,byte_size,pixel_width,pixel_height,dpi_x,dpi_y,mime_type,orientation,
                    created_utc,modified_utc,origin_catalog_id,source_batch_id)
                VALUES($id,$key,$hash,$size,32,32,96,96,'image/png',1,$time,$time,$origin,$batch);
                INSERT INTO material_images(id,material_id,image_id,sort_order,created_utc,modified_utc,origin_catalog_id,source_batch_id)
                VALUES($link,$material,$id,$ordinal,$time,$time,$origin,$batch);
                """, ("$id", id.ToString()), ("$link", link.ToString()), ("$key", key), ("$hash", hash), ("$size", bytes.Length),
                ("$ordinal", i), ("$batch", batch));
            if (version >= 2)
                Execute("""
                    UPDATE material_images SET is_primary=$primary WHERE id=$link;
                    INSERT INTO image_calibrations(id,material_image_id,pixel_size_x_m,pixel_size_y_m,source_kind,source_note,
                        created_utc,modified_utc,origin_catalog_id,source_batch_id)
                    VALUES($cal,$link,$pixelWidth,0.000002,'scale_bar',$note,$time,$time,$origin,$batch);
                    """, ("$primary", i == 0 ? 1 : 0), ("$link", link.ToString()), ("$cal", Guid.NewGuid().ToString()),
                    ("$pixelWidth", (i + 3) * 1e-6), ("$note", "Scale " + i), ("$batch", batch));
            Execute(version < 3
                    ? """
                        INSERT INTO regions(id,material_id,material_image_id,element_id,geometry_type,coordinate_space,sort_order,
                            created_utc,modified_utc,origin_catalog_id,source_batch_id)
                        VALUES($region,$material,$link,$element,'polygon','pixels',0,$time,$time,$origin,$batch);
                        """
                    : """
                        INSERT INTO regions(id,material_id,material_image_id,element_id,geometry_type,coordinate_space,geometry_json,
                            sort_order,created_utc,modified_utc,origin_catalog_id,source_batch_id)
                        VALUES($region,$material,$link,$element,$kind,'pixels',$geometry,0,$time,$time,$origin,$batch);
                        """,
                ("$region", region.ToString()), ("$link", link.ToString()), ("$kind", geometry.Kind),
                ("$geometry", RegionGeometryCodec.Serialize(geometry)), ("$batch", batch));
            if (version < 3)
                for (int j = 0; j < geometry.Points.Count; j++)
                    Execute("INSERT INTO region_points VALUES($region,$ordinal,$x,$y);", ("$region", region.ToString()),
                        ("$ordinal", j), ("$x", geometry.Points[j].X), ("$y", geometry.Points[j].Y));
        }
        if (version >= 2)
        {
            foreach (var (code, id, ru, en) in new[]
            {
                ("author", "55c60362-9f3c-5415-b146-b74697cdc257", "Автор", "Author"),
                ("organization", "b3736f80-2d03-56ca-897a-6b0887b05803", "Организация", "Organization"),
                ("subject", SubjectGroup, "Предметная область", "Subject"),
                ("topic", "747abbda-2182-5f11-891f-39188bfdbb90", "Тема", "Topic"),
                ("purpose", "dfd8db88-31de-58c3-a7d2-2049bf2ca77f", "Назначение", "Purpose"),
                ("other", OtherGroup, "Прочее", "Other")
            }) AddGroup(code, id, ru, en, true);
        }
        if (version == 6)
        {
            Execute("""
                INSERT INTO training_prompts(id,material_id,element_id,created_utc,modified_utc,origin_catalog_id,source_batch_id)
                VALUES($prompt,$material,$element,$time,$time,$origin,$batch);
                """, ("$prompt", seed.PromptId.ToString()), ("$batch", batch));
            foreach (var (locale, text) in new[] { ("ru", RussianPrompt), ("en", EnglishPrompt) })
                Execute("""
                    INSERT INTO training_prompt_translations(id,prompt_id,locale,text,created_utc,modified_utc,origin_catalog_id,source_batch_id)
                    VALUES($id,$prompt,$locale,$text,$time,$time,$origin,$batch);
                    """, ("$id", Guid.NewGuid().ToString()), ("$prompt", seed.PromptId.ToString()),
                    ("$locale", locale), ("$text", text), ("$batch", batch));
            var group = Guid.NewGuid().ToString();
            var custom = Guid.NewGuid().ToString();
            AddGroup("histology", group, "Гистология", "Histology", false);
            AddGroup("untested-custom", custom, "Специальный", "Custom", false);
            AddTag(group, seed.TagId, "Эпителий", "Epithelium");
            AddTag(custom, seed.CustomTagId, "Внешний", "External");
        }
        if (version >= 5)
            Execute("""
                INSERT INTO exchange_baselines VALUES($source,'material',$material,$digest,$digest,1,$time);
                """, ("$source", Guid.NewGuid().ToString()), ("$digest", RetiredXmlDigest));
        transaction.Commit();
        return seed;

        void AddGroup(string code, string id, string ru, string en, bool system)
        {
            Execute("""
                INSERT INTO tag_groups(id,code,is_system,created_utc,modified_utc,origin_catalog_id)
                VALUES($group,$code,$system,$time,$time,$origin);
                """, ("$group", id), ("$code", code), ("$system", system ? 1 : 0));
            foreach (var (locale, label) in new[] { ("ru", ru), ("en", en) })
                Execute("""
                    INSERT INTO tag_group_translations(id,group_id,locale,name,created_utc,modified_utc,origin_catalog_id)
                    VALUES($id,$group,$locale,$name,$time,$time,$origin);
                    """, ("$id", Guid.NewGuid().ToString()), ("$group", id), ("$locale", locale), ("$name", label));
        }
        void AddTag(string group, Guid tag, string ru, string en)
        {
            Execute("""
                INSERT INTO tags(id,group_id,code,created_utc,modified_utc,origin_catalog_id)
                VALUES($tag,$group,$tag,$time,$time,$origin);
                INSERT INTO material_tags(id,material_id,tag_id,created_utc,modified_utc,origin_catalog_id)
                VALUES($id,$material,$tag,$time,$time,$origin);
                """, ("$id", Guid.NewGuid().ToString()), ("$group", group), ("$tag", tag.ToString()));
            foreach (var (locale, label) in new[] { ("ru", ru), ("en", en) })
                Execute("""
                    INSERT INTO tag_translations(id,tag_id,locale,name,created_utc,modified_utc,origin_catalog_id)
                    VALUES($id,$tag,$locale,$name,$time,$time,$origin);
                    """, ("$id", Guid.NewGuid().ToString()), ("$tag", tag.ToString()), ("$locale", locale), ("$name", label));
        }
        void Execute(string sql, params (string Name, object Value)[] args)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$origin", seed.CatalogId.ToString());
            command.Parameters.AddWithValue("$material", seed.MaterialId.ToString());
            command.Parameters.AddWithValue("$element", seed.ElementId.ToString());
            command.Parameters.AddWithValue("$time", Timestamp);
            foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }
    }

    internal static void MakeLegacyBackup(LegacyCatalog seed, string destination)
    {
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        var files = new[] { "catalog.sqlite" }.Concat(seed.Images.Select(i => i.Key)).Select(key =>
        {
            var path = Path.Combine(seed.Root, key);
            zip.CreateEntryFromFile(path, key);
            return new { Path = key, Sha256 = Hash(path), Size = new FileInfo(path).Length };
        }).ToArray();
        using var manifest = zip.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(manifest, new { FormatVersion = 1, CreatedUtc = Timestamp, Files = files });
    }

    internal static string Hash(string path) { using var stream = File.OpenRead(path); return Hash(stream); }
    internal static string Hash(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));
}

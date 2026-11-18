using HistOSets.Core;
using HistOSets.Storage;
using Microsoft.Data.Sqlite;
using static NativeTestFixtures;

internal static class FoundationChecks
{
    internal static int Run(string temp)
    {
        var count = 0;
        for (int version = 1; version <= 6; version++)
        {
            var legacy = CreateLegacy(Path.Combine(temp, "Legacy" + version), version, includeSecondaryImage: version == 6);
            var sourceHash = Hash(legacy.Database);
            var destination = Path.Combine(temp, "Converted" + version + ".histosets");
            CatalogStore.ConvertLegacyCatalog(version == 1 ? legacy.Database : legacy.Root, destination);
            Check(Hash(legacy.Database) == sourceHash && legacy.Images.All(i => Hash(Path.Combine(legacy.Root, i.Key)) == i.Hash),
                "Converting schema " + version + " preserves the original SQLite and image bytes");
            CheckMigrated(legacy, destination);

            if (version != 6) continue;
            var oldBackup = Path.Combine(temp, "Legacy without XML.histosets");
            MakeLegacyBackup(legacy, oldBackup);
            var archiveHash = Hash(oldBackup);
            Directory.Delete(legacy.Root, recursive: true);
            var fromArchive = Path.Combine(temp, "Converted archive.histosets");
            CatalogStore.ConvertLegacyCatalog(oldBackup, fromArchive);
            Check(Hash(oldBackup) == archiveHash, "Converting a legacy ZIP with no XML preserves the original archive");
            var elsewhere = Path.Combine(temp, "Only the catalog"); Directory.CreateDirectory(elsewhere);
            var moved = Path.Combine(elsewhere, "Moved.histosets");
            File.Move(fromArchive, moved);
            File.Delete(oldBackup);
            CheckMigrated(legacy, moved);
            Check(Directory.EnumerateFileSystemEntries(elsewhere).SequenceEqual([moved]),
                "A converted legacy backup opens after moving its single file and deleting all inputs");
        }

        var failing = CreateLegacy(Path.Combine(temp, "Failed migration source"), 6, includeSecondaryImage: true);
        var image = Path.Combine(failing.Root, failing.Images[1].Key);
        var bytes = File.ReadAllBytes(image);
        var originalDatabase = Hash(failing.Database);
        var failedResult = Path.Combine(temp, "Failed conversion.histosets");
        File.Delete(image);
        Reject<CatalogStorageException>(() => CatalogStore.ConvertLegacyCatalog(failing.Root, failedResult),
            "A missing second image prevents publishing an incomplete catalog");
        Check(ErrorHasNoOutput() && Hash(failing.Database) == originalDatabase, "Failed conversion leaves source SQLite and destination untouched");
        bytes[bytes.Length / 2] ^= 0x1;
        File.WriteAllBytes(image, bytes);
        Reject<CatalogStorageException>(() => CatalogStore.ConvertLegacyCatalog(failing.Root, failedResult),
            "A same-size damaged image cannot pass the legacy conversion hash check");
        Check(ErrorHasNoOutput(), "An invalid original leaves no staged catalog behind");
        return count;

        bool ErrorHasNoOutput() => !File.Exists(failedResult) && !Directory.EnumerateFiles(temp, "Failed conversion.histosets.partial-*").Any();

        void CheckMigrated(LegacyCatalog seed, string path)
        {
            using var store = new CatalogStore(path);
            var data = store.GetMetadata();
            var cards = store.Load();
            Check(data.Id == seed.CatalogId && data.FormatVersion == 7 && CatalogStore.IsNativeCatalog(path) &&
                Convert.ToInt64(Sql(path, "SELECT COUNT(*) FROM schema_migrations;")) == 7 &&
                Convert.ToInt64(Sql(path, "SELECT COUNT(*) FROM pragma_table_info('import_batches') WHERE name='xml_sha256';")) == 0 &&
                Convert.ToInt64(Sql(path, "SELECT COUNT(*) FROM materials WHERE source_batch_id IS NOT NULL;")) == 0,
                "Schema " + seed.Version + " becomes native SQLite without legacy XML references");
            Check(cards.Warnings.Count == 0 && cards.Specimens.Count == 2 * seed.Images.Length &&
                cards.Specimens.All(s => s.IsPrimaryImage && s.CoordinateSpace == CoordinateSpace.Pixels),
                "Every migrated image has one card with both language views");
            var main = store.LoadMaterialDraft(seed.MaterialId);
            Check(main.Texts["ru"].Name == BaseName && main.Texts["en"].Description == "Full description" &&
                main.Images.Single().Id == seed.Images[0].LinkId && main.Elements.Single().Id == seed.ElementId &&
                main.Elements.Single().Texts["ru"].Summary == "Краткий текст",
                "The original card's and element's identities, credit, and translations stay intact");
            for (int i = 0; i < seed.Images.Length; i++)
            {
                var expected = seed.Images[i];
                var actual = cards.Specimens.Single(c => c.ImageId == expected.Id && c.Locale == "ru");
                var element = actual.Elements.Single();
                var actualRegion = element.Regions.Single();
                using var photo = store.OpenImage(expected.Id);
                Check(actual.MaterialImageId == expected.LinkId && actualRegion.Id == expected.RegionId &&
                    RegionGeometryCodec.Serialize(actualRegion.Geometry) == RegionGeometryCodec.Serialize(expected.Geometry) &&
                    Hash(photo) == expected.Hash,
                    "Original image bytes and exact region geometry survive conversion (image " + (i + 1) + ")");
                if (seed.Version >= 2)
                    Check(actual.Calibration is { } calibration && calibration.PixelSizeXMetres == (i + 3) * 1e-6 &&
                        calibration.PixelSizeYMetres == 2e-6 && calibration.SourceNote == "Scale " + i,
                        "Independent physical calibration axes and provenance stay on the correct image");
            }
            if (seed.Version >= 5)
                Check(Convert.ToInt64(Sql(path, "SELECT COUNT(*) FROM exchange_baselines;")) == 1,
                    "Existing acknowledged exchange history is retained");
            if (seed.Version != 6) return;
            var taxonomy = store.LoadTaxonomy();
            var subjectTag = taxonomy.Tags.Single(t => t.Id == seed.TagId);
            var customTag = taxonomy.Tags.Single(t => t.Id == seed.CustomTagId);
            Check(taxonomy.Groups.Count == 6 && subjectTag.GroupId == Guid.Parse(SubjectGroup) &&
                customTag.GroupId == Guid.Parse(OtherGroup) && subjectTag.Names["en"] == "Epithelium" &&
                taxonomy.MaterialTags[seed.MaterialId].SetEquals([seed.TagId, seed.CustomTagId]),
                "Legacy tag types map to the six system types without losing the tags and assignments");
            var mainPrompt = main.Elements.Single().TrainingPrompts.Single();
            Check(mainPrompt.Id == seed.PromptId && mainPrompt.Texts["ru"] == RussianPrompt && mainPrompt.Texts["en"] == EnglishPrompt,
                "Training descriptions survive conversion independently of ordinary card descriptions");
            var secondary = cards.Specimens.Single(c => c.ImageId == seed.Images[1].Id && c.Locale == "ru");
            var secondDraft = store.LoadMaterialDraft(secondary.MaterialId!.Value);
            Check(secondDraft.Images.Single().Id != main.Images.Single().Id && secondDraft.Id != main.Id &&
                secondDraft.AnalogGroupId is not null && secondDraft.AnalogGroupId == main.AnalogGroupId &&
                secondDraft.Elements.Single().TrainingPrompts.Single().Texts["en"] == EnglishPrompt &&
                secondDraft.TagIds.SetEquals(main.TagIds),
                "A former extra image becomes an analog card with its own geometry and retained descriptions and tags");
        }
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            count++;
            Console.WriteLine("PASS " + name);
        }
        void Reject<T>(Action action, string name) where T : Exception
        {
            try { action(); }
            catch (T) { Check(true, name); return; }
            throw new Exception("Expected " + typeof(T).Name + ": " + name);
        }
    }

    internal static object? Sql(string path, string query)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = query;
        return command.ExecuteScalar();
    }
}

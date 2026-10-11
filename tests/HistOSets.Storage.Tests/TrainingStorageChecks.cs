using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HistOSets.Core;
using HistOSets.Storage;
using Microsoft.Data.Sqlite;

internal static class TrainingStorageChecks
{
    internal static int Run(string source, string baselineBackup, string temp, Func<string, ImageMetadata> metadata)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
        void Reject(Action action, string name)
        {
            try { action(); } catch (CatalogStorageException) { Check(true, name); return; }
            throw new Exception("Expected CatalogStorageException: " + name);
        }
        var folder = Path.Combine(temp, "Training"); Directory.CreateDirectory(folder);
        var image = AtlasLoader.Load(source).Specimens.Select(s => s.ImagePath).Distinct().MinBy(p => new FileInfo(p).Length)!;
        var root = Path.Combine(folder, "Source");
        using var sender = new CatalogStore(root);
        Check(sender.GetMetadata().FormatVersion == 6 && Convert.ToInt64(Sql(sender.DatabasePath,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';")) == 21,
            "New schema6 has exactly 21 application tables including two independent training-description tables");
        var draft = Card(image, "Образец", "Specimen");
        draft.Status = "ready";
        draft = sender.SaveMaterialDraft(draft, metadata);
        Check(draft.TrainingPrompts.Count == 0 && draft.Elements.Single().TrainingPrompts.Count == 0 && draft.Status == "ready",
            "A complete ready atlas card needs no training descriptions and never copies its ordinary descriptions");
        draft.TrainingPrompts.Add(Prompt("Слоистый препарат с прозрачной поверхностью.", "A layered specimen with a transparent surface."));
        draft.TrainingPrompts.Add(Prompt("Дополнительный вариант только на русском.", "", false));
        draft.Elements[0].TrainingPrompts.Add(Prompt("Округлая структура в центре поля.", "A rounded structure at the field centre."));
        var materialPrompt = draft.TrainingPrompts[0].Id;
        var disabledPrompt = draft.TrainingPrompts[1].Id;
        var elementPrompt = draft.Elements[0].TrainingPrompts[0].Id;
        var originalElement = draft.Elements[0].Id;
        var clone = draft.Clone();
        clone.TrainingPrompts[0].Texts["ru"] = "Изолированная правка";
        clone.Elements[0].TrainingPrompts[0].IsEnabled = false;
        Check(draft.TrainingPrompts[0].Texts["ru"] != clone.TrainingPrompts[0].Texts["ru"] && draft.Elements[0].TrainingPrompts[0].IsEnabled,
            "Cloning an editor card isolates all material and element training variants and their language dictionaries");
        var revision = sender.GetMetadata().ContentRevision;
        draft = sender.SaveMaterialDraft(draft, metadata);
        Check(draft.TrainingPrompts.Select(p => p.Id).SequenceEqual(new[] { materialPrompt, disabledPrompt })
            && draft.Elements[0].TrainingPrompts[0].Id == elementPrompt && !draft.TrainingPrompts[1].IsEnabled
            && draft.TrainingPrompts[0].Texts["en"] == "A layered specimen with a transparent surface."
            && draft.Texts["ru"].Description == "Обычное описание Образец." && sender.GetMetadata().ContentRevision == revision + 1,
            "One card save atomically persists ordered bilingual variants, enabled states, independent scopes and one content revision");
        var hash = Hash(sender.DatabasePath);
        try
        {
            Sql(sender.DatabasePath, "UPDATE training_prompts SET material_id=$id WHERE id=$prompt;", ("$id", Guid.NewGuid().ToString()), ("$prompt", elementPrompt.ToString()));
            throw new Exception("Training ownership foreign key unexpectedly accepted");
        }
        catch (SqliteException) { Check(true, "SQLite enforces that element learning descriptions belong to the same existing material"); }
        Check(Hash(sender.DatabasePath) == hash, "Rejected training foreign-key update preserves database bytes");
        draft = sender.SaveMaterialDraft(draft.Clone(), metadata);
        Check(Hash(sender.DatabasePath) == hash, "Saving unchanged training variants is a byte-for-byte database no-op");
        var stale = draft.Clone();
        draft.TrainingPrompts.Reverse();
        draft = sender.SaveMaterialDraft(draft, metadata);
        Check(draft.TrainingPrompts[0].Id == disabledPrompt && draft.TrainingPrompts[1].Id == materialPrompt,
            "Reordering training variants preserves UUIDs and persists their new order");
        Reject(() => sender.SaveMaterialDraft(stale, metadata), "A stale working copy cannot overwrite a newer training-description edit");
        var bad = draft.Clone(); bad.Elements[0].TrainingPrompts.Add(bad.TrainingPrompts[0].Clone());
        hash = Hash(sender.DatabasePath);
        Reject(() => sender.SaveMaterialDraft(bad, metadata), "The same training UUID cannot occur under two owners in one card");
        var another = Card(image, "Другой", "Other"); another.TrainingPrompts.Add(draft.TrainingPrompts[0].Clone());
        Reject(() => sender.SaveMaterialDraft(another, metadata), "A new material cannot steal a persisted training-description UUID");
        var moved = draft.Clone(); var transfer = moved.TrainingPrompts[0]; moved.TrainingPrompts.RemoveAt(0); moved.Elements[0].TrainingPrompts.Add(transfer);
        Reject(() => sender.SaveMaterialDraft(moved, metadata), "A persisted training variant cannot silently change from material scope to element scope");
        Check(Hash(sender.DatabasePath) == hash, "Rejected training ownership changes leave every database byte untouched");
        Sql(sender.DatabasePath, "CREATE TRIGGER fail_training BEFORE UPDATE ON training_prompt_translations BEGIN SELECT RAISE(ABORT,'training transaction failed'); END;");
        hash = Hash(sender.DatabasePath);
        var fail = draft.Clone(); fail.Texts["ru"].Summary = "This edit must roll back"; fail.TrainingPrompts[1].Texts["ru"] += " Изменение.";
        Reject(() => sender.SaveMaterialDraft(fail, metadata), "A late training-translation write failure aborts the complete card save");
        Check(Hash(sender.DatabasePath) == hash && sender.LoadMaterialDraft(draft.Id).Texts["ru"].Summary != fail.Texts["ru"].Summary,
            "Training failure rolls back earlier atlas text updates, revisions and training changes together");
        Sql(sender.DatabasePath, "DROP TRIGGER fail_training;");
        var snapshot = sender.CaptureTrainingCatalog(metadata); hash = Hash(sender.DatabasePath);
        var captured = snapshot.Materials.Single(m => m.Id == draft.Id);
        Check(captured.Descriptions.Select(p => p.Id).SequenceEqual(draft.TrainingPrompts.Select(p => p.Id))
            && captured.Elements[0].Descriptions.Single().Id == elementPrompt && captured.Images.Single().IsUsable,
            "Training snapshot contains independent material and element variants plus a verified usable source image");
        Check(Hash(sender.DatabasePath) == hash && sender.CaptureTrainingCatalog(metadata).SourceFingerprint == snapshot.SourceFingerprint,
            "Repeated training snapshots are read-only and retain a stable content fingerprint");
        var dimensionMismatch = sender.CaptureTrainingCatalog(path => metadata(path) with { Width = metadata(path).Width + 1 });
        Check(!dimensionMismatch.Materials.Single().Images.Single().IsUsable && Hash(sender.DatabasePath) == hash,
            "Training preparation rejects decoded image dimensions that disagree with stored scientific geometry");
        var fingerprintBeforeSql = snapshot.SourceFingerprint;
        Sql(sender.DatabasePath, "UPDATE training_prompt_translations SET text=text || ' SQL' WHERE prompt_id=$id AND locale='ru';", ("$id", materialPrompt.ToString()));
        Check(sender.CaptureTrainingCatalog(metadata).SourceFingerprint != fingerprintBeforeSql,
            "An external SQL edit invalidates training data even when revision counters were not advanced");
        draft = sender.LoadMaterialDraft(draft.Id);
        draft.TrainingPrompts[0].Texts["ru"] += " Новый вариант.";
        draft = sender.SaveMaterialDraft(draft, metadata);
        Check(sender.CaptureTrainingCatalog(metadata).SourceFingerprint != snapshot.SourceFingerprint
            && captured.Descriptions[0].Texts["ru"] != draft.TrainingPrompts[0].Texts["ru"],
            "Prompt changes invalidate the training fingerprint while earlier captured descriptions remain immutable");
        var asset = draft.Images[0].ManagedPath!; var bytes = File.ReadAllBytes(asset);
        var cleanSnapshot = sender.CaptureTrainingCatalog(metadata); hash = Hash(sender.DatabasePath);
        File.WriteAllBytes(asset, bytes[..^1]);
        var damaged = sender.CaptureTrainingCatalog(metadata);
        Check(!damaged.Materials.Single().Images.Single().IsUsable && !string.IsNullOrWhiteSpace(damaged.Materials.Single().Images.Single().Issue)
            && damaged.SourceFingerprint != cleanSnapshot.SourceFingerprint && Hash(sender.DatabasePath) == hash,
            "A changed image becomes explicitly unavailable for training and invalidates the snapshot without writing the catalog");
        File.WriteAllBytes(asset, bytes);
        File.Delete(asset);
        Check(!sender.CaptureTrainingCatalog(metadata).Materials.Single().Images.Single().IsUsable,
            "A missing original produces a training image issue instead of silently preparing a broken question");
        File.WriteAllBytes(asset, bytes);
        var retained = draft.TrainingPrompts.Single(p => p.Id == disabledPrompt).Clone();
        draft.TrainingPrompts.RemoveAll(p => p.Id == disabledPrompt); draft = sender.SaveMaterialDraft(draft, metadata);
        Check(Convert.ToInt64(Sql(sender.DatabasePath, "SELECT COUNT(*) FROM training_prompts WHERE id=$id AND deleted_utc IS NOT NULL;", ("$id", disabledPrompt.ToString()))) == 1
            && Convert.ToInt64(Sql(sender.DatabasePath, "SELECT COUNT(*) FROM training_prompt_translations WHERE prompt_id=$id AND deleted_utc IS NULL;", ("$id", disabledPrompt.ToString()))) == 0,
            "Removing a variant retains its tombstone and deactivates every translation");
        draft.TrainingPrompts.Add(retained); draft = sender.SaveMaterialDraft(draft, metadata);
        Check(draft.TrainingPrompts.Any(p => p.Id == disabledPrompt), "A removed training variant can be restored with the same UUID under its original owner");
        var backup = Path.Combine(folder, "training.histosets"); sender.CreateBackup(backup);
        var restoredRoot = Path.Combine(folder, "Restored"); CatalogStore.RestoreBackup(backup, restoredRoot);
        using (var restored = new CatalogStore(restoredRoot))
            Check(PromptContent(restored.LoadMaterialDraft(draft.Id)) == PromptContent(draft),
                "Full backup and restore preserve every training UUID, language text, order, enabled state and owner");
        count += ExchangeChecks(sender, draft, metadata, folder);
        draft = sender.LoadMaterialDraft(draft.Id);
        draft.Elements.RemoveAll(e => e.Id == originalElement); draft = sender.SaveMaterialDraft(draft, metadata);
        Check(Convert.ToInt64(Sql(sender.DatabasePath, "SELECT COUNT(*) FROM training_prompts WHERE id=$id AND deleted_utc IS NOT NULL;", ("$id", elementPrompt.ToString()))) == 1
            && Convert.ToInt64(Sql(sender.DatabasePath, "SELECT COUNT(*) FROM training_prompt_translations WHERE prompt_id=$id AND deleted_utc IS NULL;", ("$id", elementPrompt.ToString()))) == 0,
            "Removing an element soft-deletes its learning descriptions and translations in the same card transaction");
        sender.DeleteMaterial(draft.Id, draft.Revision);
        Check(Convert.ToInt64(Sql(sender.DatabasePath, "SELECT COUNT(*) FROM training_prompts WHERE deleted_utc IS NULL;")) == 0
            && Convert.ToInt64(Sql(sender.DatabasePath, "SELECT COUNT(*) FROM training_prompt_translations WHERE deleted_utc IS NULL;")) == 0
            && sender.CaptureTrainingCatalog(metadata).Materials.Count == 0,
            "Deleting a material retires all remaining learning descriptions and removes it from the training snapshot");
        count += MigrationChecks(source, baselineBackup, folder);
        return count;
    }

    private static int ExchangeChecks(CatalogStore sender, MaterialDraft draft, Func<string, ImageMetadata> metadata, string folder)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
        void Reject(Action action, string name)
        { try { action(); } catch (CatalogStorageException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
        var export = Path.Combine(folder, "with-training.exchange"); sender.ExportMaterials(export, [draft.Id]);
        using var package = ExchangePackage.Open(export);
        using (var zip = ZipFile.OpenRead(export))
        {
            using var stream = zip.GetEntry("manifest.json")!.Open(); var manifest = JsonNode.Parse(stream)!;
            Check(manifest["FormatVersion"]!.GetValue<int>() == 2, "Training-aware material export explicitly writes exchange format2");
        }
        using var target = new CatalogStore(Path.Combine(folder, "Receiver"));
        var preview = target.PreviewExchange(package); var result = target.ApplyExchange(preview, Choices(preview), metadata);
        Check(result.Changed && PromptContent(target.LoadMaterialDraft(draft.Id)) == PromptContent(draft),
            "Exchange format2 roundtrip preserves training variants on both material and element owners");
        var hash = Hash(target.DatabasePath);
        preview = target.PreviewExchange(package); result = target.ApplyExchange(preview, Choices(preview), metadata);
        Check(!result.Changed && Hash(target.DatabasePath) == hash, "Repeating a training-aware exchange is a byte-exact no-op");
        var local = target.LoadMaterialDraft(draft.Id); local.TrainingPrompts[0].Texts["ru"] = "Локальное учебное описание."; target.SaveMaterialDraft(local, metadata);
        draft.TrainingPrompts[0].Texts["ru"] = "Учебное описание отправителя."; draft = sender.SaveMaterialDraft(draft, metadata);
        var newer = Path.Combine(folder, "training-both-changed.exchange"); sender.ExportMaterials(newer, [draft.Id]);
        using var changed = ExchangePackage.Open(newer); preview = target.PreviewExchange(changed);
        Check(Material(preview, draft.Id).Status == ExchangeChangeKind.Conflict,
            "Independent edits confined to learning descriptions are detected as a material-content conflict");
        target.ApplyExchange(preview, Choices(preview), metadata);
        Check(PromptContent(target.LoadMaterialDraft(draft.Id)) == PromptContent(draft),
            "Explicit UseIncoming resolves a learning-description-only conflict with the complete reviewed graph");
        var legacyFile = RewritePackage(newer, folder, "legacy-with-text-change", g =>
        {
            g.AsObject().Remove("training_prompts"); g.AsObject().Remove("training_prompt_translations");
            g["material_translations"]!.AsArray().Single(t => t!["locale"]!.GetValue<string>() == "ru")!["summary"] = "Из старого пакета";
        }, 1);
        var retained = PromptRows(target.DatabasePath);
        using (var legacy = ExchangePackage.Open(legacyFile))
        {
            preview = target.PreviewExchange(legacy); target.ApplyExchange(preview, Choices(preview), metadata);
            Check(target.LoadMaterialDraft(draft.Id).Texts["ru"].Summary == "Из старого пакета" && PromptRows(target.DatabasePath) == retained,
                "UseIncoming from legacy format1 updates its atlas fields but preserves byte-exact local learning rows for retained owners");
        }
        var other = Card(draft.Images[0].ManagedPath!, "Другой владелец", "Other owner");
        other.TrainingPrompts.Add(new() { Id = draft.TrainingPrompts[0].Id, Texts = new() { ["ru"] = "UUID collision", ["en"] = "Collision" } });
        using (var collision = new CatalogStore(Path.Combine(folder, "PromptCollision")))
        {
            collision.SaveMaterialDraft(other, metadata); hash = Hash(collision.DatabasePath);
            Reject(() => { var plan = collision.PreviewExchange(changed); collision.ApplyExchange(plan, Choices(plan), metadata); },
                "An imported training UUID cannot steal a learning description already owned by another local material");
            Check(Hash(collision.DatabasePath) == hash, "Imported training ownership collision leaves the target database byte-identical");
        }
        var legacyDeleteElement = RewritePackage(newer, folder, "legacy-delete-element", g =>
        {
            g.AsObject().Remove("training_prompts"); g.AsObject().Remove("training_prompt_translations");
            foreach (var table in new[] { "elements", "element_translations", "regions" })
                foreach (var row in g[table]!.AsArray()) row!["deleted_utc"] = "2026-10-11T00:00:00Z";
        }, 1);
        using (var legacy = ExchangePackage.Open(legacyDeleteElement))
        {
            preview = target.PreviewExchange(legacy); target.ApplyExchange(preview, Choices(preview), metadata);
            Check(target.LoadMaterialDraft(draft.Id).Elements.Count == 0
                && Convert.ToInt64(Sql(target.DatabasePath, "SELECT COUNT(*) FROM training_prompts WHERE element_id IS NOT NULL AND deleted_utc IS NULL;")) == 0
                && target.LoadMaterialDraft(draft.Id).TrainingPrompts.Count == draft.TrainingPrompts.Count,
                "Legacy format1 element deletion retires only that owner's learning descriptions and retains material descriptions");
        }
        var divergedLegacyTarget = Path.Combine(folder, "LegacyBaseline");
        using (var legacyTarget = new CatalogStore(divergedLegacyTarget))
        {
            preview = legacyTarget.PreviewExchange(changed); legacyTarget.ApplyExchange(preview, Choices(preview), metadata);
            using (var oldPackage = ExchangePackage.Open(legacyFile))
            { preview = legacyTarget.PreviewExchange(oldPackage); legacyTarget.ApplyExchange(preview, Choices(preview), metadata); }
            var noPrompts = RewritePackage(newer, folder, "format2-after-legacy", graph =>
            {
                graph["training_prompts"] = new JsonArray(); graph["training_prompt_translations"] = new JsonArray();
                graph["materials"]![0]!["revision"] = draft.Revision + 10;
                graph["material_translations"]!.AsArray().Single(t => t!["locale"]!.GetValue<string>() == "ru")!["summary"] = "Новая правка без учебных описаний";
            }, 2);
            using var next = ExchangePackage.Open(noPrompts); preview = legacyTarget.PreviewExchange(next);
            Check(Material(preview, draft.Id).Status == ExchangeChangeKind.Conflict,
                "A newer format2 package cannot automatically erase local descriptions previously preserved across format1 exchange");
        }
        // A format2 sender intentionally omitting active variants is authoritative for that graph.
        draft.TrainingPrompts.Clear(); draft.Elements[0].TrainingPrompts.Clear(); draft = sender.SaveMaterialDraft(draft, metadata);
        var removed = Path.Combine(folder, "removed-training.exchange"); sender.ExportMaterials(removed, [draft.Id]);
        using (var removePackage = ExchangePackage.Open(removed))
        {
            preview = target.PreviewExchange(removePackage); target.ApplyExchange(preview, Choices(preview), metadata);
            Check(target.LoadMaterialDraft(draft.Id).TrainingPrompts.Count == 0 && target.LoadMaterialDraft(draft.Id).Elements[0].TrainingPrompts.Count == 0,
                "Explicit format2 replacement applies learning-description deletions without treating them as absent legacy capability");
        }
        var localBeforeDeletion = target.LoadMaterialDraft(draft.Id);
        localBeforeDeletion.TrainingPrompts.Add(Prompt("Только у получателя", "Receiver-only learning description"));
        target.SaveMaterialDraft(localBeforeDeletion, metadata);
        var legacyDeleteRoot = RewritePackage(removed, folder, "legacy-delete-material", graph =>
        {
            graph.AsObject().Remove("training_prompts"); graph.AsObject().Remove("training_prompt_translations");
            foreach (var table in new[] { "materials", "material_translations", "material_images", "elements", "element_translations", "regions", "image_calibrations", "material_tags" })
                foreach (var row in graph[table]!.AsArray()) row!["deleted_utc"] = "2026-10-11T00:00:00Z";
        }, 1);
        using (var legacy = ExchangePackage.Open(legacyDeleteRoot))
        {
            preview = target.PreviewExchange(legacy);
            Check(Material(preview, draft.Id).Status == ExchangeChangeKind.Deletion,
                "Legacy whole-card deletion is presented as an explicit deletion decision");
            target.ApplyExchange(preview, Choices(preview), metadata);
            Check(target.LoadMaterialIndex().Count == 0
                && Convert.ToInt64(Sql(target.DatabasePath, "SELECT COUNT(*) FROM training_prompts WHERE deleted_utc IS NULL;")) == 0
                && Convert.ToInt64(Sql(target.DatabasePath, "SELECT COUNT(*) FROM training_prompt_translations WHERE deleted_utc IS NULL;")) == 0,
                "Accepted legacy whole-card deletion retires receiver-only learning descriptions with their owner");
        }
        return count;
    }

    private static int MigrationChecks(string source, string baselineBackup, string folder)
    {
        var root = Path.Combine(folder, "ActualSchema5"); CatalogStore.RestoreBackup(baselineBackup, root); var db = Path.Combine(root, "catalog.sqlite");
        Sql(db, "DROP TABLE training_prompt_translations; DROP TABLE training_prompts; DROP TABLE exchange_baselines;"
            + File.ReadAllText(Path.Combine(source, "src", "HistOSets.Storage", "Migrations", "005_catalog_exchange.sql"))
            + "DELETE FROM schema_migrations WHERE version>5; PRAGMA user_version=5;");
        var oldRows = ExistingRows(db); var hash = Hash(db); string before;
        using (var migrated = new CatalogStore(root))
        {
            if (migrated.GetMetadata().FormatVersion != 6 || ExistingRows(db) != oldRows
                || Convert.ToInt64(Sql(db, "SELECT COUNT(*) FROM training_prompts;")) != 0 || Convert.ToInt64(Sql(db, "SELECT COUNT(*) FROM schema_migrations;")) != 6)
                throw new Exception("Actual schema5 migration changed original rows or invented prompts");
            before = migrated.LastMigrationBackupPath ?? throw new Exception("No schema6 backup");
            if (!Path.GetFileName(before).StartsWith("before-schema6-")) throw new Exception("Wrong schema6 backup name");
        }
        Console.WriteLine("PASS Actual schema5 migrates to schema6 once with exact original business rows and no invented learning descriptions");
        var restored = Path.Combine(folder, "OriginalSchema5"); CatalogStore.RestoreBackup(before, restored); var restoredDb = Path.Combine(restored, "catalog.sqlite");
        if (Convert.ToInt64(Sql(restoredDb, "PRAGMA user_version;")) != 5 || ExistingRows(restoredDb) != oldRows)
            throw new Exception("Pre-schema6 backup failed to restore actual schema5 data");
        Console.WriteLine("PASS Pre-schema6 recovery archive restores actual schema5 and all original records and files");
        Sql(restoredDb, "CREATE TRIGGER fail_schema6 BEFORE INSERT ON schema_migrations WHEN NEW.version=6 BEGIN SELECT RAISE(ABORT,'schema6 failure'); END;");
        hash = Hash(restoredDb);
        try { using var failed = new CatalogStore(restored); throw new Exception("Schema6 failure was not reported"); } catch (CatalogStorageException) { }
        if (Hash(restoredDb) != hash || Convert.ToInt64(Sql(restoredDb, "PRAGMA user_version;")) != 5
            || Convert.ToInt64(Sql(restoredDb, "SELECT COUNT(*) FROM sqlite_master WHERE name IN('training_prompts','training_prompt_translations');")) != 0)
            throw new Exception("Schema6 migration failure did not roll back new DDL and metadata");
        Console.WriteLine("PASS Failed schema6 migration rolls back both new tables and preserves original database bytes");
        return 3;
    }

    private static MaterialDraft Card(string image, string ru, string en)
    {
        var draft = new MaterialDraft();
        draft.Texts["ru"] = new() { Name = ru, Summary = "Краткое описание", Description = "Обычное описание " + ru + "." };
        draft.Texts["en"] = new() { Name = en, Summary = "Ordinary summary", Description = "Ordinary description " + en + "." };
        var picture = new MaterialImageDraft { SourcePath = image, SourceSha256 = Hash(image), IsPrimary = true }; draft.Images.Add(picture);
        var element = new MaterialElementDraft();
        element.Texts["ru"] = new() { Name = "Структура", Summary = "Описание элемента", Description = "Полное описание структуры" };
        element.Texts["en"] = new() { Name = "Structure", Summary = "Element summary", Description = "Full structural description" };
        element.Regions.Add(new() { MaterialImageId = picture.Id, Geometry = RegionGeometry.Rectangle(10, 20, 30, 40) }); draft.Elements.Add(element);
        return draft;
    }
    private static TrainingPromptDraft Prompt(string ru, string en, bool enabled = true) => new() { IsEnabled = enabled, Texts = new() { ["ru"] = ru, ["en"] = en } };
    private static string PromptContent(MaterialDraft draft) => JsonSerializer.Serialize(new { draft.TrainingPrompts, Elements = draft.Elements.Select(e => new { e.Id, e.TrainingPrompts }) });
    private static string PromptRows(string db) => JsonSerializer.Serialize(new[] { Snapshot(db, "training_prompts"), Snapshot(db, "training_prompt_translations") });
    private static string ExistingRows(string db) => JsonSerializer.Serialize(new[] { "images", "materials", "material_translations", "material_images", "elements", "element_translations", "regions", "image_calibrations", "tag_groups", "tag_group_translations", "tags", "tag_translations", "material_tags", "analog_groups", "analog_group_translations", "import_batches", "exchange_baselines" }.Select(t => Snapshot(db, t)));
    private static string Snapshot(string db, string table)
    {
        using var connection = Open(db); using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM " + table + (table == "exchange_baselines" ? " ORDER BY source_catalog_id,entity_kind,entity_id;" : " ORDER BY id;");
        using var reader = command.ExecuteReader(); var rows = new List<object?[]>();
        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
        return JsonSerializer.Serialize(rows);
    }
    private static ExchangePreviewItem Material(ExchangePreview preview, Guid id) => preview.Items.Single(i => i.Kind == ExchangeEntityKind.Material && i.Id == id);
    private static Dictionary<string, ExchangeResolution> Choices(ExchangePreview preview) => preview.Items.ToDictionary(i => i.Key, i => i.Status == ExchangeChangeKind.Unchanged ? ExchangeResolution.KeepLocal : ExchangeResolution.UseIncoming);
    private static string RewritePackage(string source, string folder, string name, Action<JsonNode> mutate, int format)
    {
        var path = Path.Combine(folder, name + ".exchange"); using var input = ZipFile.OpenRead(source); using var output = ZipFile.Open(path, ZipArchiveMode.Create);
        JsonNode manifest, graph;
        using (var stream = input.GetEntry("manifest.json")!.Open()) manifest = JsonNode.Parse(stream)!;
        using (var stream = input.GetEntry("graph.json")!.Open()) graph = JsonNode.Parse(stream)!;
        mutate(graph); manifest["FormatVersion"] = format; var bytes = JsonSerializer.SerializeToUtf8Bytes(graph);
        var record = manifest["Files"]!.AsArray().Single(f => f!["Path"]!.GetValue<string>() == "graph.json")!;
        record["Sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)); record["Size"] = bytes.Length;
        using (var stream = output.CreateEntry("graph.json").Open()) stream.Write(bytes);
        using (var stream = output.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(stream, manifest);
        foreach (var entry in input.Entries.Where(e => e.FullName.StartsWith("SPECIMENS/")))
        { using var from = entry.Open(); using var to = output.CreateEntry(entry.FullName).Open(); from.CopyTo(to); }
        return path;
    }
    private static SqliteConnection Open(string db)
    { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, ForeignKeys = true, Pooling = false }.ToString()); c.Open(); return c; }
    private static object? Sql(string db, string sql, params (string Name, object? Value)[] parameters)
    { using var c = Open(db); using var command = c.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value); return command.ExecuteScalar(); }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
}

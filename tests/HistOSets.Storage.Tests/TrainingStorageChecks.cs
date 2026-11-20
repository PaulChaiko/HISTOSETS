using System.Security.Cryptography;
using System.Text.Json;
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
        var image = Directory.GetFiles(Path.Combine(source, "SPECIMENS")).MinBy(p => new FileInfo(p).Length)!;
        var root = Path.Combine(folder, "Source.histosets");
        CatalogStore.CreateCatalog(root, "Training");
        using var sender = new CatalogStore(root);
        Check(sender.GetMetadata().FormatVersion == 7 && Convert.ToInt64(Sql(sender.DatabasePath,
            "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('training_prompts','training_prompt_translations');")) == 2,
            "Native schema 7 retains both independent training description tables");
        ImageMetadata Decode(Stream stream) => metadata(image);
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
        var snapshot = sender.CaptureTrainingCatalog(Decode); hash = Hash(sender.DatabasePath);
        var captured = snapshot.Materials.Single(m => m.Id == draft.Id);
        Check(captured.Descriptions.Select(p => p.Id).SequenceEqual(draft.TrainingPrompts.Select(p => p.Id))
            && captured.Elements[0].Descriptions.Single().Id == elementPrompt && captured.Images.Single().IsUsable,
            "Training snapshot contains independent material and element variants plus a verified usable source image");
        Check(Hash(sender.DatabasePath) == hash && sender.CaptureTrainingCatalog(Decode).SourceFingerprint == snapshot.SourceFingerprint,
            "Repeated training snapshots are read-only and retain a stable content fingerprint");
        var dimensionMismatch = sender.CaptureTrainingCatalog(_ => metadata(image) with { Width = metadata(image).Width + 1 });
        Check(!dimensionMismatch.Materials.Single().Images.Single().IsUsable && Hash(sender.DatabasePath) == hash,
            "Training preparation rejects decoded image dimensions that disagree with stored scientific geometry");
        var fingerprintBeforeSql = snapshot.SourceFingerprint;
        Sql(sender.DatabasePath, "UPDATE training_prompt_translations SET text=text || ' SQL' WHERE prompt_id=$id AND locale='ru';", ("$id", materialPrompt.ToString()));
        Check(sender.CaptureTrainingCatalog(Decode).SourceFingerprint != fingerprintBeforeSql,
            "An external SQL edit invalidates training data even when revision counters were not advanced");
        draft = sender.LoadMaterialDraft(draft.Id);
        draft.TrainingPrompts[0].Texts["ru"] += " Новый вариант.";
        draft = sender.SaveMaterialDraft(draft, metadata);
        Check(sender.CaptureTrainingCatalog(Decode).SourceFingerprint != snapshot.SourceFingerprint
            && captured.Descriptions[0].Texts["ru"] != draft.TrainingPrompts[0].Texts["ru"],
            "Prompt changes invalidate the training fingerprint while earlier captured descriptions remain immutable");
        var asset = draft.Images[0].ImageId!.Value;
        var bytes = File.ReadAllBytes(image);
        var cleanSnapshot = sender.CaptureTrainingCatalog(Decode);
        Sql(sender.DatabasePath, "UPDATE image_contents SET data=zeroblob(length(data)) WHERE image_id=$id;", ("$id", asset.ToString()));
        hash = Hash(sender.DatabasePath);
        var damaged = sender.CaptureTrainingCatalog(Decode);
        Check(!damaged.Materials.Single().Images.Single().IsUsable && !string.IsNullOrWhiteSpace(damaged.Materials.Single().Images.Single().Issue)
            && damaged.SourceFingerprint != cleanSnapshot.SourceFingerprint && Hash(sender.DatabasePath) == hash,
            "Verified training preparation detects a same-size damaged embedded original without writing the catalog");
        Sql(sender.DatabasePath, "DELETE FROM image_contents WHERE image_id=$id;", ("$id", asset.ToString()));
        Check(!sender.CaptureTrainingCatalog().Materials.Single().Images.Single().IsUsable,
            "A missing embedded original produces a training image issue instead of preparing a broken question");
        Sql(sender.DatabasePath, "INSERT INTO image_contents(image_id,data) VALUES($id,$data);", ("$data", bytes), ("$id", asset.ToString()));
        var retained = draft.TrainingPrompts.Single(p => p.Id == disabledPrompt).Clone();
        draft.TrainingPrompts.RemoveAll(p => p.Id == disabledPrompt); draft = sender.SaveMaterialDraft(draft, metadata);
        Check(Convert.ToInt64(Sql(sender.DatabasePath, "SELECT COUNT(*) FROM training_prompts WHERE id=$id AND deleted_utc IS NOT NULL;", ("$id", disabledPrompt.ToString()))) == 1
            && Convert.ToInt64(Sql(sender.DatabasePath, "SELECT COUNT(*) FROM training_prompt_translations WHERE prompt_id=$id AND deleted_utc IS NULL;", ("$id", disabledPrompt.ToString()))) == 0,
            "Removing a variant retains its tombstone and deactivates every translation");
        draft.TrainingPrompts.Add(retained); draft = sender.SaveMaterialDraft(draft, metadata);
        Check(draft.TrainingPrompts.Any(p => p.Id == disabledPrompt), "A removed training variant can be restored with the same UUID under its original owner");
        var backup = Path.Combine(folder, "training.histosets"); sender.CreateBackup(backup);
        var restoredRoot = Path.Combine(folder, "Restored.histosets"); CatalogStore.CopyCatalog(backup, restoredRoot);
        using (var restored = new CatalogStore(restoredRoot))
            Check(PromptContent(restored.LoadMaterialDraft(draft.Id)) == PromptContent(draft),
                "Full backup and restore preserve every training UUID, language text, order, enabled state and owner");
        count += ExchangeChecks(sender, draft, image, metadata, folder);
        draft = sender.LoadMaterialDraft(draft.Id);
        draft.Elements.RemoveAll(e => e.Id == originalElement); draft = sender.SaveMaterialDraft(draft, metadata);
        Check(Convert.ToInt64(Sql(sender.DatabasePath, "SELECT COUNT(*) FROM training_prompts WHERE id=$id AND deleted_utc IS NOT NULL;", ("$id", elementPrompt.ToString()))) == 1
            && Convert.ToInt64(Sql(sender.DatabasePath, "SELECT COUNT(*) FROM training_prompt_translations WHERE prompt_id=$id AND deleted_utc IS NULL;", ("$id", elementPrompt.ToString()))) == 0,
            "Removing an element soft-deletes its learning descriptions and translations in the same card transaction");
        sender.DeleteMaterial(draft.Id, draft.Revision);
        Check(Convert.ToInt64(Sql(sender.DatabasePath, "SELECT COUNT(*) FROM training_prompts WHERE deleted_utc IS NULL;")) == 0
            && Convert.ToInt64(Sql(sender.DatabasePath, "SELECT COUNT(*) FROM training_prompt_translations WHERE deleted_utc IS NULL;")) == 0
            && sender.CaptureTrainingCatalog(Decode).Materials.Count == 0,
            "Deleting a material retires all remaining learning descriptions and removes it from the training snapshot");
        return count;
    }

    private static int ExchangeChecks(CatalogStore sender, MaterialDraft draft, string image, Func<string, ImageMetadata> metadata, string folder)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
        void Reject(Action action, string name)
        { try { action(); } catch (CatalogStorageException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
        var export = Path.Combine(folder, "with-training.histosets"); sender.ExportMaterials(export, [draft.Id]);
        using var package = ExchangePackage.Open(export);
        Check(CatalogStore.IsNativeCatalog(export), "Training-aware material export is a directly openable catalog");
        var targetPath = Path.Combine(folder, "Receiver.histosets");
        CatalogStore.CreateCatalog(targetPath, "Receiver");
        using var target = new CatalogStore(targetPath);
        var preview = target.PreviewExchange(package); var result = target.ApplyExchange(preview, Choices(preview));
        Check(result.Changed && PromptContent(target.LoadMaterialDraft(draft.Id)) == PromptContent(draft),
            "Native exchange roundtrip preserves training variants on both material and element owners");
        var hash = Hash(target.DatabasePath);
        preview = target.PreviewExchange(package); result = target.ApplyExchange(preview, Choices(preview));
        Check(!result.Changed && Hash(target.DatabasePath) == hash, "Repeating a training-aware exchange is a byte-exact no-op");
        var local = target.LoadMaterialDraft(draft.Id); local.TrainingPrompts[0].Texts["ru"] = "Локальное учебное описание."; target.SaveMaterialDraft(local, metadata);
        draft.TrainingPrompts[0].Texts["ru"] = "Учебное описание отправителя."; draft = sender.SaveMaterialDraft(draft, metadata);
        var newer = Path.Combine(folder, "training-both-changed.histosets"); sender.ExportMaterials(newer, [draft.Id]);
        using var changed = ExchangePackage.Open(newer); preview = target.PreviewExchange(changed);
        Check(Material(preview, draft.Id).Status == ExchangeChangeKind.Conflict,
            "Independent edits confined to learning descriptions are detected as a material-content conflict");
        target.ApplyExchange(preview, Choices(preview));
        Check(PromptContent(target.LoadMaterialDraft(draft.Id)) == PromptContent(draft),
            "Explicit UseIncoming resolves a learning-description-only conflict with the complete reviewed graph");
        var other = Card(image, "Другой владелец", "Other owner");
        other.TrainingPrompts.Add(new() { Id = draft.TrainingPrompts[0].Id, Texts = new() { ["ru"] = "UUID collision", ["en"] = "Collision" } });
        var collisionPath = Path.Combine(folder, "PromptCollision.histosets");
        CatalogStore.CreateCatalog(collisionPath, "Prompt collision");
        using (var collision = new CatalogStore(collisionPath))
        {
            collision.SaveMaterialDraft(other, metadata); hash = Hash(collision.DatabasePath);
            Reject(() => { var plan = collision.PreviewExchange(changed); collision.ApplyExchange(plan, Choices(plan)); },
                "An imported training UUID cannot steal a learning description already owned by another local material");
            Check(Hash(collision.DatabasePath) == hash, "Imported training ownership collision leaves the target database byte-identical");
        }
        draft.TrainingPrompts.Clear(); draft.Elements[0].TrainingPrompts.Clear(); draft = sender.SaveMaterialDraft(draft, metadata);
        var removed = Path.Combine(folder, "removed-training.histosets"); sender.ExportMaterials(removed, [draft.Id]);
        using (var removePackage = ExchangePackage.Open(removed))
        {
            preview = target.PreviewExchange(removePackage); target.ApplyExchange(preview, Choices(preview));
            Check(target.LoadMaterialDraft(draft.Id).TrainingPrompts.Count == 0 && target.LoadMaterialDraft(draft.Id).Elements[0].TrainingPrompts.Count == 0,
                "Explicit replacement applies learning-description deletions");
        }
        var localBeforeDeletion = target.LoadMaterialDraft(draft.Id);
        localBeforeDeletion.TrainingPrompts.Add(Prompt("Только у получателя", "Receiver-only learning description"));
        target.SaveMaterialDraft(localBeforeDeletion, metadata);
        var sourceCopy = Path.Combine(folder, "BeforeDeletion.histosets");
        sender.CreateBackup(sourceCopy);
        using var deletionSource = new CatalogStore(sourceCopy);
        deletionSource.DeleteMaterial(draft.Id, draft.Revision);
        var deleted = Path.Combine(folder, "deleted-training.histosets");
        deletionSource.ExportMaterials(deleted, [draft.Id]);
        using (var deletion = ExchangePackage.Open(deleted))
        {
            preview = target.PreviewExchange(deletion);
            Check(Material(preview, draft.Id).Status == ExchangeChangeKind.Deletion,
                "Whole-card deletion is presented as an explicit deletion decision");
            target.ApplyExchange(preview, Choices(preview));
            Check(target.LoadMaterialIndex().Count == 0
                && Convert.ToInt64(Sql(target.DatabasePath, "SELECT COUNT(*) FROM training_prompts WHERE deleted_utc IS NULL;")) == 0,
                "Accepted deletion retires receiver-only learning descriptions with their owner");
        }
        return count;
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
    private static ExchangePreviewItem Material(ExchangePreview preview, Guid id) => preview.Items.Single(i => i.Kind == ExchangeEntityKind.Material && i.Id == id);
    private static Dictionary<string, ExchangeResolution> Choices(ExchangePreview preview) => preview.Items.ToDictionary(i => i.Key, i => i.Status == ExchangeChangeKind.Unchanged ? ExchangeResolution.KeepLocal : ExchangeResolution.UseIncoming);
    private static SqliteConnection Open(string db)
    { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, ForeignKeys = true, Pooling = false }.ToString()); c.Open(); return c; }
    private static object? Sql(string db, string sql, params (string Name, object? Value)[] parameters)
    { using var c = Open(db); using var command = c.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value); return command.ExecuteScalar(); }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
}

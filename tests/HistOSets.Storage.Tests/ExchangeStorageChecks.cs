using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HistOSets.Core;
using HistOSets.Storage;
using Microsoft.Data.Sqlite;

internal static class ExchangeStorageChecks
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
        var folder = Path.Combine(temp, "Exchange"); Directory.CreateDirectory(folder);
        var originals = AtlasLoader.Load(source).Specimens.Select(s => s.ImagePath).Distinct().OrderBy(p => new FileInfo(p).Length).Take(2).ToArray();
        var sourceRoot = Path.Combine(folder, "Source"); CatalogStore.RestoreBackup(baselineBackup, sourceRoot);
        using var sender = new CatalogStore(sourceRoot);
        sender.RenameCatalog("Каталог обмена ' RU / EN");
        var type = sender.LoadTaxonomy().Groups.Single(g => g.Code == "author").Id;
        var tag = sender.SaveTag(null, type, "Автор обмена", "Exchange author");
        var unusedTag = sender.SaveTag(null, type, "Не выбранный автор", "Unselected author");
        var analog = sender.SaveAnalogGroup(null, "Аналоги обмена", "Exchange analogs");
        var unusedAnalog = sender.SaveAnalogGroup(null, "Не выбранные аналоги", "Unselected analogs");
        var draft = NewMaterial("Выбранный объект", originals[0]);
        draft.AuthorCredit = "И. Автор / I. Author"; draft.RightsCredit = "Scientific collection";
        draft.AnalogGroupId = analog; draft.TagIds.Add(tag);
        draft.Texts["und"] = Text("Исходный язык", "Original summary", "Original description");
        draft.Images.Add(new() { SourcePath = originals[1], SourceSha256 = Hash(originals[1]) });
        var dimensions = metadata(originals[0]);
        draft.Images[0].Calibration = ImageCalibration.FromDimensions(dimensions.Width * 2, dimensions.Height * 3, "um", dimensions.Width, dimensions.Height) with { SourceNote = "Source calibration" };
        var element = new MaterialElementDraft();
        element.Texts["ru"] = Text("Элемент", "Кратко", "Подробно"); element.Texts["en"] = Text("Element", "Summary", "Description");
        element.Texts["fr"] = Text("Élément", "Résumé", "Description française");
        element.Regions.Add(new() { MaterialImageId = draft.Images[0].Id, Geometry = RegionGeometryCodec.Deserialize("""
            {"version":2,"kind":"compound","coordinateSpace":"pixels","fillRule":"evenOdd","rings":[[[0,0],[40,0],[40,40],[0,40]],[[5,5],[10,5],[10,10],[5,10]],[[60,0],[70,0],[70,10],[60,10]]]}
            """) });
        element.Regions.Add(new() { MaterialImageId = draft.Images[1].Id, Geometry = RegionGeometry.Ellipse(11.5, 12.25, 30, 40) });
        draft.Elements.Add(element);
        var retired = new MaterialElementDraft(); retired.Texts["ru"].Name = "Исторический элемент"; draft.Elements.Add(retired);
        draft = sender.SaveMaterialDraft(draft, metadata);
        draft.Elements.RemoveAll(e => e.Id == retired.Id); draft = sender.SaveMaterialDraft(draft, metadata);
        draft.Status = "ready"; draft = sender.SaveMaterialDraft(draft, metadata);
        var id = draft.Id;
        Sql(sender.DatabasePath, """
            INSERT INTO image_calibrations
            SELECT $id,material_image_id,pixel_size_x_m,pixel_size_y_m,source_kind,'Retired calibration',input_width,input_height,input_unit,
                revision,created_utc,'2099-01-01T00:00:00+00:00','2099-01-01T00:00:00+00:00',origin_catalog_id,source_batch_id
            FROM image_calibrations WHERE material_image_id=$image AND deleted_utc IS NULL;
            """, ("$id", Guid.NewGuid().ToString()), ("$image", draft.Images[0].Id.ToString()));
        var calibrationId = Sql(sender.DatabasePath, "SELECT id FROM image_calibrations WHERE material_image_id=$image AND deleted_utc IS NULL;", ("$image", draft.Images[0].Id.ToString()));
        var export = Path.Combine(folder, "selected.histosets-exchange");
        var sourceHash = Hash(sender.DatabasePath); sender.ExportMaterials(export, [id, id]);
        Check(Hash(sender.DatabasePath) == sourceHash, "Selected export is read-only, including catalog revision and source provenance");
        Reject(() => sender.ExportMaterials(export, [id]), "Exchange export never overwrites an existing destination");
        Reject(() => sender.ExportMaterials(Path.Combine(folder, "empty.exchange"), []), "Exchange export requires an explicit nonempty selection");
        Reject(() => sender.ExportMaterials(Path.Combine(folder, "missing.exchange"), [Guid.NewGuid()]), "A missing selected material cannot produce a misleading partial package");
        using (var zip = ZipFile.OpenRead(export))
        {
            Check(zip.Entries.Count == 4 && zip.Entries.All(e => e.FullName is "manifest.json" or "graph.json" || e.FullName.StartsWith("SPECIMENS/")),
                "A selected package contains only its JSON graph and two original images, with no SQLite or source XML");
            var graph = ReadGraph(zip);
            Check(graph["materials"]!.AsArray().Count == 1 && graph["tags"]!.AsArray().Count == 1 && graph["analog_groups"]!.AsArray().Count == 1 &&
                !graph.ToJsonString().Contains(unusedTag.ToString()) && !graph.ToJsonString().Contains(unusedAnalog.ToString()),
                "Subset export excludes unselected materials and unrelated tag or analog dictionary values");
            Check(graph.AsObject().All(t => t.Value!.AsArray().All(row => row!["source_batch_id"] is null)),
                "Portable rows retain content but contain no dangling original XML import-batch references");
            Check(graph["elements"]!.AsArray().Any(e => e!["id"]!.GetValue<string>() == retired.Id.ToString() && e["deleted_utc"] is not null),
                "Selected material history retains child UUIDs and logical deletion markers");
        }
        using var package = ExchangePackage.Open(export);
        package.ValidateImages(metadata);
        Reject(() => package.ValidateImages(path => metadata(path) with { Width = metadata(path).Width + 1 }),
            "Decoded image dimensions must agree with package metadata before scientific geometry is accepted");
        Check(package.Materials.Count == 1 && package.Materials[0].Id == id && package.Materials[0].Names["und"] == "Исходный язык" &&
            package.SourceCatalogId == sender.GetMetadata().Id && package.SourceCatalogName == sender.GetMetadata().Name,
            "Package inspection exposes stable identity, all names and exact source catalog identity");
        Check(sender.PreviewExchange(package).Items.All(i => i.Status == ExchangeChangeKind.Unchanged),
            "An exported package may be inspected in its source catalog and compares equal despite SQLite REAL versus JSON integral numbers");

        using var target = new CatalogStore(Path.Combine(folder, "Target"));
        var hash = Hash(target.DatabasePath); var files = Files(target.RootDirectory);
        var preview = target.PreviewExchange(package);
        Check(Hash(target.DatabasePath) == hash && Files(target.RootDirectory).SequenceEqual(files), "Import preview makes no database, image or backup writes");
        Check(Material(preview, id).Status == ExchangeChangeKind.Add && preview.Items.Where(i => i.Kind == ExchangeEntityKind.TagGroup).All(i => i.Status == ExchangeChangeKind.Unchanged),
            "A new selected object is Add while independently created identical system types are Unchanged");
        var applied = target.ApplyExchange(preview, Choices(preview), metadata);
        Check(applied.Changed && applied.Added >= 1 && applied.BackupPath is not null && File.Exists(applied.BackupPath), "Import creates a complete before-exchange recovery archive before adding content");
        var restoredBefore = Path.Combine(folder, "BeforeImport"); CatalogStore.RestoreBackup(applied.BackupPath!, restoredBefore);
        using (var before = new CatalogStore(restoredBefore)) Check(before.GetStatistics().Materials == 0, "Before-exchange backup restores the actual pre-import empty target");
        var imported = target.LoadMaterialDraft(id);
        Check(Content(imported) == Content(draft), "Exchange preserves multilingual descriptions, credits, image order, compound holes, calibration, tags and analog assignment");
        Check(imported.Images.Select(i => Hash(i.ManagedPath!)).SequenceEqual(draft.Images.Select(i => Hash(i.ManagedPath!))), "Every transferred scientific original is byte-for-byte identical");
        Check(target.GetStatistics().Materials == 1 && target.LoadTaxonomy().Tags.Single().Id == tag && target.LoadAnalogGroups().Single().Id == analog,
            "Import installs only the selected root and required dictionary identities");
        Check((long)Sql(target.DatabasePath, "SELECT COUNT(*) FROM elements WHERE id=$id AND deleted_utc IS NOT NULL;", ("$id", retired.Id.ToString()))! == 1 &&
            (long)Sql(target.DatabasePath, "SELECT COUNT(*) FROM import_batches;")! == 0,
            "Imported child deletion history is preserved without inventing a legacy import batch");
        Check(SelectedRows(sender.DatabasePath, id) == SelectedRows(target.DatabasePath, id),
            "Initial import retains every selected scientific row UUID, revision, timestamp and provenance field");
        hash = Hash(target.DatabasePath); files = Files(target.RootDirectory); preview = target.PreviewExchange(package);
        applied = target.ApplyExchange(preview, Choices(preview), metadata);
        Check(!applied.Changed && applied.BackupPath is null && Hash(target.DatabasePath) == hash && Files(target.RootDirectory).SequenceEqual(files),
            "Reimporting identical content is a byte-for-byte catalog no-op with no extra backup");

        // No previously acknowledged source content: a larger counter alone is not proof of ancestry.
        using (var independent = new CatalogStore(Path.Combine(folder, "Independent")))
        {
            var independentlyEdited = NewMaterial("Другая правка", originals[0]); independentlyEdited.Id = id;
            independentlyEdited = independent.SaveMaterialDraft(independentlyEdited, metadata);
            var first = independent.PreviewExchange(package);
            Check(Material(first, id).Status == ExchangeChangeKind.Conflict && Material(first, id).RequiresDecision,
                "First encounter with the same UUID and different content is a conflict even if the source revision is higher");
            hash = Hash(independent.DatabasePath);
            Reject(() => independent.ApplyExchange(first, new Dictionary<string, ExchangeResolution>(), metadata), "An unresolved conflict cannot be applied by default");
            Check(Hash(independent.DatabasePath) == hash, "An unresolved conflict leaves receiving catalog bytes unchanged");
            var skipped = independent.ApplyExchange(first, Choices(first, ExchangeResolution.Skip), metadata);
            Check(!skipped.Changed && skipped.BackupPath is null && Hash(independent.DatabasePath) == hash,
                "Explicitly skipping every incoming change leaves content and acknowledgement history untouched");
            independent.ApplyExchange(first, Choices(first, ExchangeResolution.KeepLocal), metadata);
            Check(independent.LoadMaterialDraft(id).Texts["ru"].Name == "Другая правка", "KeepLocal explicitly preserves independently edited material content");
            var changedAgain = Mutate(export, folder, "unacknowledged-source-change", g =>
                g["material_translations"]!.AsArray().Single(t => t!["locale"]!.GetValue<string>() == "ru")!["summary"] = "Another source edit");
            using (var changed = ExchangePackage.Open(changedAgain))
                Check(Material(independent.PreviewExchange(changed), id).Status == ExchangeChangeKind.Conflict,
                    "KeepLocal never acknowledges rejected source content as a common baseline for a later source edit");
            var next = independent.PreviewExchange(package);
            var explicitIncoming = Choices(next); explicitIncoming[Material(next, id).Key] = ExchangeResolution.UseIncoming;
            independent.ApplyExchange(next, explicitIncoming, metadata);
            Check(Content(independent.LoadMaterialDraft(id)) == Content(draft), "UseIncoming explicitly installs the reviewed whole incoming material graph");
        }

        // The target has a baseline now: exercise all three branches of content comparison.
        draft = sender.LoadMaterialDraft(id); draft.Texts["ru"].Summary = "Правка отправителя"; draft = sender.SaveMaterialDraft(draft, metadata);
        using var changedPackage = ExportOpen(sender, folder, "source-change", id);
        preview = target.PreviewExchange(changedPackage);
        Check(Material(preview, id).Status == ExchangeChangeKind.Update, "Acknowledged unchanged local content plus changed source content proposes Update");
        var staleEditor = target.LoadMaterialDraft(id);
        target.ApplyExchange(preview, Choices(preview), metadata);
        Check(target.LoadMaterialDraft(id).Texts["ru"].Summary == "Правка отправителя" && target.LoadMaterialDraft(id).Revision > staleEditor.Revision,
            "Accepted incoming content advances the local material revision and persists source edits");
        Check(Equals(calibrationId, Sql(target.DatabasePath, "SELECT id FROM image_calibrations WHERE material_image_id=$image AND deleted_utc IS NULL;", ("$image", draft.Images[0].Id.ToString()))) &&
            (long)Sql(target.DatabasePath, "SELECT COUNT(*) FROM image_calibrations;")! == 2,
            "Incoming updates preserve the active calibration UUID even when a newer historical tombstone exists for the same image");
        Check(Material(target.PreviewExchange(package), id).Status == ExchangeChangeKind.Conflict,
            "An older divergent package cannot silently roll back a newer accepted source revision");
        var sameRevision = Mutate(Path.Combine(folder, "source-change.exchange"), folder, "divergent-equal-revision", graph =>
            graph["material_translations"]!.AsArray().Single(t => t!["locale"]!.GetValue<string>() == "ru")!["description"] = "Different content at the accepted revision");
        using (var divergent = ExchangePackage.Open(sameRevision))
            Check(Material(target.PreviewExchange(divergent), id).Status == ExchangeChangeKind.Conflict,
                "Divergent source content at the already accepted revision requires explicit conflict resolution");
        Reject(() => target.SaveMaterialDraft(staleEditor, metadata), "An editor opened before exchange cannot overwrite the imported revision");
        var local = target.LoadMaterialDraft(id); local.Texts["ru"].Summary = "Местная правка"; local = target.SaveMaterialDraft(local, metadata);
        preview = target.PreviewExchange(changedPackage);
        Check(Material(preview, id).Status == ExchangeChangeKind.LocalChanged && Material(preview, id).SuggestedResolution == ExchangeResolution.KeepLocal,
            "Unchanged acknowledged source plus local edits proposes KeepLocal");
        target.ApplyExchange(preview, Choices(preview), metadata);
        Check(target.LoadMaterialDraft(id).Texts["ru"].Summary == "Местная правка", "Repeated source import does not erase local-only changes");
        draft.Texts["ru"].Summary = "Одновременная правка отправителя"; draft = sender.SaveMaterialDraft(draft, metadata);
        using var bothPackage = ExportOpen(sender, folder, "both-change", id);
        preview = target.PreviewExchange(bothPackage);
        Check(Material(preview, id).Status == ExchangeChangeKind.Conflict, "Different changes on both sides of the acknowledged baseline require conflict resolution");
        target.ApplyExchange(preview, Choices(preview), metadata);
        Check(target.LoadMaterialDraft(id).Texts["ru"].Summary == draft.Texts["ru"].Summary, "Explicit conflict resolution applies the selected incoming version");

        // Higher-level catalog revision and raw row changes both invalidate preview safely.
        preview = target.PreviewExchange(bothPackage); target.RenameCatalog("Changed after preview"); hash = Hash(target.DatabasePath);
        Reject(() => target.ApplyExchange(preview, Choices(preview), metadata), "Catalog changes after preview require a fresh plan");
        Check(Hash(target.DatabasePath) == hash, "Rejected stale preview preserves every subsequent local change");
        preview = target.PreviewExchange(bothPackage);
        Sql(target.DatabasePath, "UPDATE material_translations SET summary='External row change' WHERE material_id=$id AND locale='ru';", ("$id", id.ToString()));
        hash = Hash(target.DatabasePath);
        Reject(() => target.ApplyExchange(preview, Choices(preview), metadata), "Content fingerprint rejects stale plans even when an external writer fails to increment catalog revision");
        Check(Hash(target.DatabasePath) == hash, "Fingerprint rejection is read-only");

        // Shared dictionaries may affect a ready card not included in the package.
        var external = NewMaterial("Местный объект вне пакета", originals[0]); external.TagIds.Add(tag); external.Status = "ready";
        external = target.SaveMaterialDraft(external, metadata);
        sender.SaveTag(tag, type, "Автор без перевода", "");
        using var sharedPackage = ExportOpen(sender, folder, "shared-change", id);
        preview = target.PreviewExchange(sharedPackage);
        var tagItem = preview.Items.Single(i => i.Kind == ExchangeEntityKind.Tag && i.Id == tag);
        Check(tagItem.AffectedMaterialCount >= 2 && tagItem.Warning.Length > 0 && tagItem.RequiresDecision,
            "Shared dictionary preview identifies affected cards outside the selected material and requires a decision");
        applied = target.ApplyExchange(preview, Choices(preview), metadata);
        Check(applied.DemotedMaterials >= 1 && target.LoadMaterialDraft(external.Id).Status == "draft" &&
            target.LoadMaterialDraft(external.Id).Texts["ru"].Name == "Местный объект вне пакета",
            "Accepting an incomplete shared tag demotes affected ready cards atomically without overwriting their content");

        var deleted = sender.LoadMaterialDraft(id); sender.DeleteMaterial(id, deleted.Revision);
        Check(!sender.LoadExchangeIndex().Any(i => i.Id == id) && sender.LoadExchangeIndex(includeDeleted: true).Single(i => i.Id == id).IsDeleted,
            "Export selection hides deleted roots unless deletion markers are explicitly requested");
        using var deletionPackage = ExportOpen(sender, folder, "deletion", id);
        preview = target.PreviewExchange(deletionPackage);
        Check(Material(preview, id).Status == ExchangeChangeKind.Deletion && Material(preview, id).RequiresDecision, "A received tombstone is an explicit deletion decision");
        hash = Hash(target.DatabasePath);
        Reject(() => target.ApplyExchange(preview, new Dictionary<string, ExchangeResolution>(), metadata), "Import never silently applies a received deletion marker");
        Check(Hash(target.DatabasePath) == hash, "Unresolved deletion leaves target bytes unchanged");
        target.ApplyExchange(preview, Choices(preview), metadata);
        Check(!target.LoadMaterialIndex().Any(i => i.Id == id) && target.LoadMaterialIndex().Any(i => i.Id == external.Id) &&
            target.LoadExchangeIndex(includeDeleted: true).Single(i => i.Id == id).IsDeleted,
            "Accepted tombstone deletes only its selected UUID and preserves unselected material and recovery history");
        Check(external.Images.All(i => File.Exists(i.ManagedPath)), "Logical deletion retains original files still shared by other material");

        count += CheckImageDeduplication(export, originals[0], metadata, folder);
        count += CheckAtomicFailure(export, metadata, folder);
        count += CheckChildOwnership(export, originals[0], metadata, folder);
        count += CheckInvalidPackages(export, folder);
        count += CheckSchema4Migration(source, baselineBackup, folder);
        return count;
    }

    private static int CheckImageDeduplication(string export, string original, Func<string, ImageMetadata> metadata, string folder)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
        var alias = Mutate(export, folder, "image-alias", graph =>
        {
            var row = graph["images"]!.AsArray().Single(i => i!["sha256"]!.GetValue<string>() == Hash(original))!;
            var old = row["id"]!.GetValue<string>(); var replacement = Guid.NewGuid().ToString(); row["id"] = replacement;
            foreach (var link in graph["material_images"]!.AsArray().Where(i => i!["image_id"]!.GetValue<string>() == old)) link!["image_id"] = replacement;
        });
        using var target = new CatalogStore(Path.Combine(folder, "ImageDeduplication"));
        var existing = target.SaveMaterialDraft(NewMaterial("Выбранный объект", original), metadata);
        using var package = ExchangePackage.Open(alias); var preview = target.PreviewExchange(package);
        target.ApplyExchange(preview, Choices(preview), metadata);
        var imported = target.LoadMaterialDraft(package.Materials.Single().Id);
        Check(imported.Images.Single(i => Hash(i.ManagedPath!) == Hash(original)).ImageId == existing.Images[0].ImageId &&
            (long)Sql(target.DatabasePath, "SELECT COUNT(*) FROM images;")! == 2,
            "Equal original bytes under different source UUIDs reuse the local image row and remap material-image references");
        Check(imported.Id != existing.Id && target.LoadMaterialIndex().Count(i => i.Name == "Выбранный объект") == 2,
            "Equal display names under different UUIDs remain separate material identities");
        var hash = Hash(target.DatabasePath); preview = target.PreviewExchange(package);
        Check(Material(preview, imported.Id).Status == ExchangeChangeKind.Unchanged && !target.ApplyExchange(preview, Choices(preview), metadata).Changed && Hash(target.DatabasePath) == hash,
            "SHA image remapping remains idempotent on later previews and imports");
        var collision = Mutate(alias, folder, "image-collision", graph =>
        {
            var rows = graph["images"]!.AsArray(); var first = rows[0]!["id"]!.GetValue<string>(); var second = rows[1]!["id"]!.GetValue<string>();
            rows[0]!["id"] = second; rows[1]!["id"] = first;
            foreach (var link in graph["material_images"]!.AsArray()) link!["image_id"] = link["image_id"]!.GetValue<string>() == first ? second : first;
        });
        using var invalid = ExchangePackage.Open(collision);
        try { target.PreviewExchange(invalid); throw new Exception("Expected immutable image collision rejection"); }
        catch (CatalogStorageException) { Check(Hash(target.DatabasePath) == hash, "An immutable image UUID reused for different bytes is rejected without catalog writes"); }
        return count;
    }

    private static int CheckAtomicFailure(string export, Func<string, ImageMetadata> metadata, string folder)
    {
        using var target = new CatalogStore(Path.Combine(folder, "AtomicFailure")); using var package = ExchangePackage.Open(export);
        Sql(target.DatabasePath, "CREATE TRIGGER fail_exchange BEFORE INSERT ON regions BEGIN SELECT RAISE(ABORT,'simulated late exchange write failure'); END;");
        var preview = target.PreviewExchange(package); var hash = Hash(target.DatabasePath);
        try { target.ApplyExchange(preview, Choices(preview), metadata); throw new Exception("Expected late exchange transaction failure"); }
        catch (CatalogStorageException) { }
        if (Hash(target.DatabasePath) != hash || target.GetStatistics().Materials != 0 ||
            (Directory.Exists(Path.Combine(target.RootDirectory, "SPECIMENS")) && Directory.GetFiles(Path.Combine(target.RootDirectory, "SPECIMENS")).Length != 0))
            throw new Exception("Failed exchange must roll back SQL and only newly installed originals");
        Console.WriteLine("PASS Late exchange failure rolls back every row, revision, baseline and newly installed original");
        var backups = Directory.GetFiles(Path.Combine(target.RootDirectory, "BACKUPS"), "before-exchange-*.histosets");
        if (backups.Length != 1) throw new Exception("Expected one recovery backup after exchange failure");
        var restored = Path.Combine(folder, "FailedExchangeRecovery"); CatalogStore.RestoreBackup(backups[0], restored);
        using (var previous = new CatalogStore(restored)) if (previous.GetStatistics().Materials != 0) throw new Exception("Failure backup did not preserve actual old catalog");
        Console.WriteLine("PASS A complete pre-exchange backup remains usable after transaction rollback");
        Sql(target.DatabasePath, "DROP TRIGGER fail_exchange;"); preview = target.PreviewExchange(package);
        if (!target.ApplyExchange(preview, Choices(preview), metadata).Changed) throw new Exception("Exchange retry did not complete");
        Console.WriteLine("PASS The same verified package can retry successfully after transaction rollback");
        return 3;
    }

    private static int CheckInvalidPackages(string export, string folder)
    {
        var count = 0;
        void Reject(string path, string name)
        {
            try { using var invalid = ExchangePackage.Open(path); } catch (CatalogStorageException) { count++; Console.WriteLine("PASS " + name); return; }
            throw new Exception("Expected invalid exchange rejection: " + name);
        }
        Reject(Mutate(export, folder, "invalid-uuid", g => g["materials"]![0]!["id"] = "not-a-uuid"), "Package graph rejects invalid stable identifiers");
        Reject(Mutate(export, folder, "invalid-reference", g => g["regions"]![0]!["material_image_id"] = Guid.NewGuid().ToString()), "Package graph rejects dangling or cross-owner region references");
        Reject(Mutate(export, folder, "unknown-column", g => g["materials"]![0]!["future_field"] = "unexpected"), "Package graph rejects unsupported columns instead of dropping content");
        Reject(Mutate(export, folder, "invalid-geometry", g => g["regions"]![0]!["geometry_json"] = "{\"version\":999,\"kind\":\"polygon\"}"), "Package graph rejects unsupported geometry before target writes");
        Reject(Mutate(export, folder, "inconsistent-calibration", g => g["image_calibrations"]![0]!["pixel_size_x_m"] = 0.25),
            "Package rejects a physical pixel scale inconsistent with its recorded whole-image dimensions");
        Reject(Mutate(export, folder, "legacy-reference", g => g["materials"]![0]!["source_batch_id"] = Guid.NewGuid().ToString()), "Package cannot smuggle a dangling source XML batch reference");
        Reject(Mutate(export, folder, "system-tag-spoof", g => g["tag_groups"]![0]!["is_system"] = 0), "Reserved system tag types cannot be disguised as imported custom dictionaries");
        Reject(Mutate(export, folder, "future-format", null, m => m["FormatVersion"] = 999), "A future exchange format is rejected without attempting partial interpretation");
        Reject(Mutate(export, folder, "unsafe-path", null, m => m["Files"]![1]!["Path"] = "SPECIMENS/../../outside.bin"), "Exchange manifest rejects directory traversal paths");
        Reject(Mutate(export, folder, "hash-mismatch", null, m => m["Files"]![0]!["Sha256"] = new string('0', 64)), "Exchange validates the graph checksum before reading scientific content");
        var damaged = Path.Combine(folder, "damaged-image.exchange"); File.Copy(export, damaged);
        using (var zip = ZipFile.Open(damaged, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.First(e => e.FullName.StartsWith("SPECIMENS/")); var name = entry.FullName; byte[] bytes;
            using (var input = entry.Open()) { using var memory = new MemoryStream(); input.CopyTo(memory); bytes = memory.ToArray(); }
            bytes[bytes.Length / 2] ^= 0x01; entry.Delete(); using var output = zip.CreateEntry(name).Open(); output.Write(bytes);
        }
        Reject(damaged, "Exchange checksums catch modified original bytes even when byte length is unchanged");
        var extra = Path.Combine(folder, "unexpected-file.exchange"); File.Copy(export, extra);
        using (var zip = ZipFile.Open(extra, ZipArchiveMode.Update)) { using var output = new StreamWriter(zip.CreateEntry("SOURCES/hidden.xml").Open()); output.Write("unselected private data"); }
        Reject(extra, "Exchange rejects undeclared source XML or other extra archive entries");
        var duplicate = Path.Combine(folder, "duplicate-entry.exchange"); File.Copy(export, duplicate);
        using (var zip = ZipFile.Open(duplicate, ZipArchiveMode.Update)) { using var output = new StreamWriter(zip.CreateEntry("GRAPH.JSON").Open()); output.Write("{}"); }
        Reject(duplicate, "Exchange rejects case-insensitive duplicate archive paths on every platform");
        return count;
    }

    private static int CheckChildOwnership(string export, string original, Func<string, ImageMetadata> metadata, string folder)
    {
        using var target = new CatalogStore(Path.Combine(folder, "ChildOwnership"));
        Guid existingChild;
        using (var zip = ZipFile.OpenRead(export)) existingChild = Guid.Parse(ReadGraph(zip)["elements"]![0]!["id"]!.GetValue<string>());
        var other = NewMaterial("Unselected local owner", original);
        other.Elements.Add(new() { Id = existingChild }); other = target.SaveMaterialDraft(other, metadata);
        using var package = ExchangePackage.Open(export); var hash = Hash(target.DatabasePath);
        try
        {
            var preview = target.PreviewExchange(package); target.ApplyExchange(preview, Choices(preview), metadata);
            throw new Exception("Expected rejection of child UUID already owned by another material");
        }
        catch (CatalogStorageException) { }
        if (Hash(target.DatabasePath) != hash || target.LoadMaterialDraft(other.Id).Elements.Single().Id != existingChild || target.GetStatistics().Materials != 1)
            throw new Exception("Child ownership collision changed existing catalog");
        Console.WriteLine("PASS Imported child UUID cannot steal or modify an element already owned by another local material");
        return 1;
    }

    private static int CheckSchema4Migration(string source, string backup, string folder)
    {
        var root = Path.Combine(folder, "ActualSchema4"); CatalogStore.RestoreBackup(backup, root); var db = Path.Combine(root, "catalog.sqlite");
        Sql(db, File.ReadAllText(Path.Combine(source, "src", "HistOSets.Storage", "Migrations", "004_compound_geometry.sql")) +
            "DROP TABLE exchange_baselines; DELETE FROM schema_migrations WHERE version>4; PRAGMA user_version=4;");
        var rows = BusinessRows(db); var catalogId = Sql(db, "SELECT catalog_id FROM catalog_metadata;"); var revision = Sql(db, "SELECT content_revision FROM catalog_metadata;");
        string before;
        using (var store = new CatalogStore(root))
        {
            if (store.GetMetadata().FormatVersion != 5 || (long)Sql(db, "SELECT COUNT(*) FROM schema_migrations;")! != 5 ||
                (long)Sql(db, "SELECT COUNT(*) FROM exchange_baselines;")! != 0 || BusinessRows(db) != rows ||
                !Equals(catalogId, Sql(db, "SELECT catalog_id FROM catalog_metadata;")) || !Equals(revision, Sql(db, "SELECT content_revision FROM catalog_metadata;")))
                throw new Exception("Schema4-to-5 migration lost content or identity");
            before = store.LastMigrationBackupPath!;
            if (!Path.GetFileName(before).StartsWith("before-schema5-")) throw new Exception("Missing named schema5 migration backup");
        }
        Console.WriteLine("PASS Actual schema4 upgrades once to schema5 with an empty exchange ledger and byte-exact business rows");
        var restored = Path.Combine(folder, "OriginalSchema4"); CatalogStore.RestoreBackup(before, restored); var restoredDb = Path.Combine(restored, "catalog.sqlite");
        if ((long)Sql(restoredDb, "PRAGMA user_version;")! != 4 || BusinessRows(restoredDb) != rows) throw new Exception("Pre-schema5 backup did not restore real schema4");
        Console.WriteLine("PASS Pre-schema5 backup restores the original schema4 metadata, images and business records");
        Sql(restoredDb, "CREATE TRIGGER fail_schema5 BEFORE INSERT ON schema_migrations WHEN NEW.version=5 BEGIN SELECT RAISE(ABORT,'schema5 failure'); END;");
        var hash = Hash(restoredDb);
        try { using var rejected = new CatalogStore(restored); throw new Exception("Expected schema5 migration failure"); } catch (CatalogStorageException) { }
        if (Hash(restoredDb) != hash || (long)Sql(restoredDb, "PRAGMA user_version;")! != 4 ||
            (long)Sql(restoredDb, "SELECT COUNT(*) FROM sqlite_master WHERE name='exchange_baselines';")! != 0) throw new Exception("Schema5 migration failure did not roll back DDL");
        Console.WriteLine("PASS Failed schema5 migration preserves original database bytes and rolls back new ledger DDL");
        return 3;
    }

    private static MaterialDraft NewMaterial(string name, string image)
    {
        var result = new MaterialDraft(); result.Texts["ru"] = Text(name, "Краткое описание", "Полное описание");
        result.Texts["en"] = Text("English " + name, "Summary", "Description");
        result.Images.Add(new() { SourcePath = image, SourceSha256 = Hash(image), IsPrimary = true }); return result;
    }
    private static MaterialTextDraft Text(string name, string summary, string description) => new() { Name = name, Summary = summary, Description = description };
    private static string Content(MaterialDraft value) => JsonSerializer.Serialize(new {
        value.Id, value.Kind, value.Status, value.AuthorCredit, value.RightsCredit, value.AnalogGroupId,
        Texts = value.Texts.OrderBy(p => p.Key), Images = value.Images.Select(i => new { Hash = Hash(i.ManagedPath!), i.IsPrimary, i.Calibration }),
        Elements = value.Elements.Select(e => new { e.Id, Texts = e.Texts.OrderBy(p => p.Key), Regions = e.Regions.Select(r => new { r.Id, ImageHash = Hash(value.Images.Single(i => i.Id == r.MaterialImageId).ManagedPath!), Geometry = RegionGeometryCodec.Serialize(r.Geometry) }) }),
        Tags = value.TagIds.Order()
    });
    private static ExchangePreviewItem Material(ExchangePreview preview, Guid id) => preview.Items.Single(i => i.Kind == ExchangeEntityKind.Material && i.Id == id);
    private static Dictionary<string, ExchangeResolution> Choices(ExchangePreview preview, ExchangeResolution resolution = ExchangeResolution.UseIncoming) =>
        preview.Items.ToDictionary(i => i.Key, i => i.Status == ExchangeChangeKind.Unchanged ? ExchangeResolution.KeepLocal :
            i.Status == ExchangeChangeKind.LocalChanged && resolution == ExchangeResolution.UseIncoming ? ExchangeResolution.KeepLocal : resolution);
    private static ExchangePackage ExportOpen(CatalogStore store, string folder, string name, Guid id)
    { var path = Path.Combine(folder, name + ".exchange"); store.ExportMaterials(path, [id]); return ExchangePackage.Open(path); }
    private static JsonNode ReadGraph(ZipArchive archive)
    { using var stream = archive.GetEntry("graph.json")!.Open(); return JsonNode.Parse(stream)!; }
    private static string Mutate(string source, string folder, string name, Action<JsonNode>? graphChange, Action<JsonNode>? manifestChange = null)
    {
        var path = Path.Combine(folder, name + ".exchange");
        using var input = ZipFile.OpenRead(source); using var output = ZipFile.Open(path, ZipArchiveMode.Create);
        JsonNode manifest; using (var stream = input.GetEntry("manifest.json")!.Open()) manifest = JsonNode.Parse(stream)!;
        var graph = ReadGraph(input); graphChange?.Invoke(graph); var bytes = JsonSerializer.SerializeToUtf8Bytes(graph);
        var record = manifest["Files"]!.AsArray().Single(f => f!["Path"]!.GetValue<string>() == "graph.json")!;
        record["Sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)); record["Size"] = bytes.Length;
        manifestChange?.Invoke(manifest);
        using (var stream = output.CreateEntry("graph.json").Open()) stream.Write(bytes);
        using (var stream = output.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(stream, manifest);
        foreach (var entry in input.Entries.Where(e => e.FullName.StartsWith("SPECIMENS/")))
        { using var from = entry.Open(); using var to = output.CreateEntry(entry.FullName, CompressionLevel.Fastest).Open(); from.CopyTo(to); }
        return path;
    }
    private static string SelectedRows(string db, Guid id)
    {
        var key = id.ToString();
        var predicates = new Dictionary<string, string> {
            ["materials"] = "id=$id", ["material_translations"] = "material_id=$id", ["material_images"] = "material_id=$id",
            ["elements"] = "material_id=$id", ["regions"] = "material_id=$id", ["material_tags"] = "material_id=$id",
            ["element_translations"] = "element_id IN(SELECT id FROM elements WHERE material_id=$id)",
            ["image_calibrations"] = "material_image_id IN(SELECT id FROM material_images WHERE material_id=$id)"
        };
        return JsonSerializer.Serialize(predicates.Select(p => Snapshot(db, p.Key, p.Value, ("$id", key))));
    }
    private static string BusinessRows(string db) => JsonSerializer.Serialize(new[] { "import_batches", "images", "materials", "material_images", "material_translations", "elements", "element_translations", "regions", "tag_groups", "tag_group_translations", "tags", "tag_translations", "material_tags", "analog_groups", "analog_group_translations", "image_calibrations" }.Select(t => Snapshot(db, t)));
    private static string Snapshot(string db, string table, string predicate = "1=1", params (string Name, object? Value)[] parameters)
    {
        using var connection = Open(db); using var command = connection.CreateCommand(); command.CommandText = $"SELECT * FROM {table} WHERE {predicate} ORDER BY id;";
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = command.ExecuteReader(); var rows = new List<object?[]>();
        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
        return JsonSerializer.Serialize(rows);
    }
    private static string[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p)).Order().ToArray();
    private static SqliteConnection Open(string db)
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, ForeignKeys = true, Pooling = false }.ToString()); connection.Open(); return connection; }
    private static object? Sql(string db, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open(db); using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value); return command.ExecuteScalar();
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}

using System.Security.Cryptography;
using System.Text.Json;
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
        var originals = Directory.EnumerateFiles(Path.Combine(source, "SPECIMENS"), "*.jpg", SearchOption.AllDirectories)
            .OrderBy(p => new FileInfo(p).Length).Take(2).ToArray();
        var byHash = originals.ToDictionary(Hash, file => metadata(file));
        ImageMetadata Decode(Stream stream) => byHash[Convert.ToHexStringLower(SHA256.HashData(stream))];
        var sourceRoot = Path.Combine(folder, "Source.histosets"); File.Copy(baselineBackup, sourceRoot);
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
        var dimensions = metadata(originals[0]);
        draft.Images[0].Calibration = ImageCalibration.FromDimensions(dimensions.Width * 2, dimensions.Height * 3, "um", dimensions.Width, dimensions.Height) with { SourceNote = "Source calibration" };
        var element = new MaterialElementDraft();
        element.Texts["ru"] = Text("Элемент", "Кратко", "Подробно"); element.Texts["en"] = Text("Element", "Summary", "Description");
        element.Texts["fr"] = Text("Élément", "Résumé", "Description française");
        element.Regions.Add(new() { MaterialImageId = draft.Images[0].Id, Geometry = RegionGeometryCodec.Deserialize("""
            {"version":2,"kind":"compound","coordinateSpace":"pixels","fillRule":"evenOdd","rings":[[[0,0],[40,0],[40,40],[0,40]],[[5,5],[10,5],[10,10],[5,10]],[[60,0],[70,0],[70,10],[60,10]]]}
            """) });
        element.Regions.Add(new() { MaterialImageId = draft.Images[0].Id, Geometry = RegionGeometry.Ellipse(11.5, 12.25, 30, 40) });
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
        var export = Path.Combine(folder, "selected.histosets");
        var sourceHash = Hash(sender.DatabasePath); sender.ExportMaterials(export, [id, id]);
        Check(Hash(sender.DatabasePath) == sourceHash, "Selected export is read-only, including catalog revision and source provenance");
        Reject(() => sender.ExportMaterials(export, [id]), "Exchange export never overwrites an existing destination");
        Reject(() => sender.ExportMaterials(Path.Combine(folder, "empty.histosets"), []), "Exchange export requires an explicit nonempty selection");
        Reject(() => sender.ExportMaterials(Path.Combine(folder, "missing.histosets"), [Guid.NewGuid()]), "A missing selected material cannot produce a misleading partial package");
        using (var selected = new CatalogStore(export))
        {
            var graph = selected.LoadMaterialDraft(id);
            Check(selected.GetMetadata().Id != sender.GetMetadata().Id && selected.GetStatistics().Materials == 1 &&
                HashOriginal(selected, graph.Images.Single().ImageId!.Value) == Hash(originals[0]),
                "Exported .histosets opens directly as a distinct, self-contained working catalog");
            Check(selected.LoadTaxonomy().Tags.Single().Id == tag && selected.LoadAnalogGroups().Single().Id == analog &&
                !selected.LoadTaxonomy().Tags.Any(t => t.Id == unusedTag) && !selected.LoadAnalogGroups().Any(g => g.Id == unusedAnalog),
                "Subset export excludes unselected materials and unrelated tag or analog dictionary values");
            Check((long)Sql(export, "SELECT COUNT(*) FROM import_batches;")! == 0,
                "Portable rows contain no dangling legacy source references");
            Check((long)Sql(export, "SELECT COUNT(*) FROM elements WHERE id=$id AND deleted_utc IS NOT NULL;", ("$id", retired.Id.ToString()))! == 1,
                "Selected material history retains child UUIDs and logical deletion markers");
        }
        using var package = ExchangePackage.Open(export);
        package.ValidateImages(Decode);
        Reject(() => package.ValidateImages(stream => { var decoded = Decode(stream); return decoded with { Width = decoded.Width + 1 }; }),
            "Decoded image dimensions must agree with package metadata before scientific geometry is accepted");
        Check(package.Materials.Count == 1 && package.Materials[0].Id == id && package.Materials[0].Names["und"] == "Исходный язык" &&
            package.SourceCatalogId == sender.GetMetadata().Id && package.SourceCatalogName == sender.GetMetadata().Name,
            "Package inspection exposes stable identity, all names and exact source catalog identity");
        Check(sender.PreviewExchange(package).Items.All(i => i.Status == ExchangeChangeKind.Unchanged),
            "An exported package may be inspected in its source catalog and compares equal to the selected source data");

        using var target = NewCatalog(folder, "Target");
        var hash = Hash(target.DatabasePath); var files = Files(target.RootDirectory);
        var preview = target.PreviewExchange(package);
        Check(Hash(target.DatabasePath) == hash && Files(target.RootDirectory).SequenceEqual(files), "Import preview makes no database, image or backup writes");
        Check(Material(preview, id).Status == ExchangeChangeKind.Add && preview.Items.Where(i => i.Kind == ExchangeEntityKind.TagGroup).All(i => i.Status == ExchangeChangeKind.Unchanged),
            "A new selected object is Add while independently created identical system types are Unchanged");
        var applied = target.ApplyExchange(preview, Choices(preview), Decode);
        Check(applied.Changed && applied.Added >= 1 && applied.BackupPath is not null && File.Exists(applied.BackupPath), "Import creates a complete before-exchange recovery archive before adding content");
        var restoredBefore = applied.BackupPath!;
        using (var before = new CatalogStore(restoredBefore)) Check(before.GetStatistics().Materials == 0, "Before-exchange backup restores the actual pre-import empty target");
        var imported = target.LoadMaterialDraft(id);
        Check(Content(imported) == Content(draft), "Exchange preserves multilingual descriptions, credits, compound holes, calibration, tags and analog assignment");
        Check(HashOriginal(target, imported.Images.Single().ImageId!.Value) == Hash(originals[0]), "Every transferred scientific original is byte-for-byte identical");
        Check(target.GetStatistics().Materials == 1 && target.LoadTaxonomy().Tags.Single().Id == tag && target.LoadAnalogGroups().Single().Id == analog,
            "Import installs only the selected root and required dictionary identities");
        Check((long)Sql(target.DatabasePath, "SELECT COUNT(*) FROM elements WHERE id=$id AND deleted_utc IS NOT NULL;", ("$id", retired.Id.ToString()))! == 1 &&
            (long)Sql(target.DatabasePath, "SELECT COUNT(*) FROM import_batches;")! == 0,
            "Imported child deletion history is preserved without inventing a legacy import batch");
        Check(SelectedRows(sender.DatabasePath, id) == SelectedRows(target.DatabasePath, id),
            "Initial import retains every selected scientific row UUID, revision, timestamp and provenance field");
        hash = Hash(target.DatabasePath); files = Files(target.RootDirectory); preview = target.PreviewExchange(package);
        applied = target.ApplyExchange(preview, Choices(preview), Decode);
        Check(!applied.Changed && applied.BackupPath is null && Hash(target.DatabasePath) == hash && Files(target.RootDirectory).SequenceEqual(files),
            "Reimporting identical content is a byte-for-byte catalog no-op with no extra backup");

        // No previously acknowledged source content: a larger counter alone is not proof of ancestry.
        using (var independent = NewCatalog(folder, "Independent"))
        {
            var independentlyEdited = NewMaterial("Другая правка", originals[0]); independentlyEdited.Id = id;
            independentlyEdited = independent.SaveMaterialDraft(independentlyEdited, metadata);
            var first = independent.PreviewExchange(package);
            Check(Material(first, id).Status == ExchangeChangeKind.Conflict && Material(first, id).RequiresDecision,
                "First encounter with the same UUID and different content is a conflict even if the source revision is higher");
            hash = Hash(independent.DatabasePath);
            Reject(() => independent.ApplyExchange(first, new Dictionary<string, ExchangeResolution>(), Decode), "An unresolved conflict cannot be applied by default");
            Check(Hash(independent.DatabasePath) == hash, "An unresolved conflict leaves receiving catalog bytes unchanged");
            var skipped = independent.ApplyExchange(first, Choices(first, ExchangeResolution.Skip), Decode);
            Check(!skipped.Changed && skipped.BackupPath is null && Hash(independent.DatabasePath) == hash,
                "Explicitly skipping every incoming change leaves content and acknowledgement history untouched");
            independent.ApplyExchange(first, Choices(first, ExchangeResolution.KeepLocal), Decode);
            Check(independent.LoadMaterialDraft(id).Texts["ru"].Name == "Другая правка", "KeepLocal explicitly preserves independently edited material content");
            var changedAgain = Mutate(export, folder, "unacknowledged-source-change",
                "UPDATE material_translations SET summary='Another source edit' WHERE locale='ru';");
            using (var changed = ExchangePackage.Open(changedAgain))
                Check(Material(independent.PreviewExchange(changed), id).Status == ExchangeChangeKind.Conflict,
                    "KeepLocal never acknowledges rejected source content as a common baseline for a later source edit");
            var next = independent.PreviewExchange(package);
            var explicitIncoming = Choices(next); explicitIncoming[Material(next, id).Key] = ExchangeResolution.UseIncoming;
            independent.ApplyExchange(next, explicitIncoming, Decode);
            Check(Content(independent.LoadMaterialDraft(id)) == Content(draft), "UseIncoming explicitly installs the reviewed whole incoming material graph");
        }

        // The target has a baseline now: exercise all three branches of content comparison.
        draft = sender.LoadMaterialDraft(id); draft.Texts["ru"].Summary = "Правка отправителя"; draft = sender.SaveMaterialDraft(draft, metadata);
        using var changedPackage = ExportOpen(sender, folder, "source-change", id);
        preview = target.PreviewExchange(changedPackage);
        Check(Material(preview, id).Status == ExchangeChangeKind.Update, "Acknowledged unchanged local content plus changed source content proposes Update");
        var staleEditor = target.LoadMaterialDraft(id);
        target.ApplyExchange(preview, Choices(preview), Decode);
        Check(target.LoadMaterialDraft(id).Texts["ru"].Summary == "Правка отправителя" && target.LoadMaterialDraft(id).Revision > staleEditor.Revision,
            "Accepted incoming content advances the local material revision and persists source edits");
        Check(Equals(calibrationId, Sql(target.DatabasePath, "SELECT id FROM image_calibrations WHERE material_image_id=$image AND deleted_utc IS NULL;", ("$image", draft.Images[0].Id.ToString()))) &&
            (long)Sql(target.DatabasePath, "SELECT COUNT(*) FROM image_calibrations;")! == 2,
            "Incoming updates preserve the active calibration UUID even when a newer historical tombstone exists for the same image");
        Check(Material(target.PreviewExchange(package), id).Status == ExchangeChangeKind.Conflict,
            "An older divergent package cannot silently roll back a newer accepted source revision");
        var sameRevision = Mutate(Path.Combine(folder, "source-change.histosets"), folder, "divergent-equal-revision",
            "UPDATE material_translations SET description='Different content at the accepted revision' WHERE locale='ru';");
        using (var divergent = ExchangePackage.Open(sameRevision))
            Check(Material(target.PreviewExchange(divergent), id).Status == ExchangeChangeKind.Conflict,
                "Divergent source content at the already accepted revision requires explicit conflict resolution");
        Reject(() => target.SaveMaterialDraft(staleEditor, metadata), "An editor opened before exchange cannot overwrite the imported revision");
        var local = target.LoadMaterialDraft(id); local.Texts["ru"].Summary = "Местная правка"; local = target.SaveMaterialDraft(local, metadata);
        preview = target.PreviewExchange(changedPackage);
        Check(Material(preview, id).Status == ExchangeChangeKind.LocalChanged && Material(preview, id).SuggestedResolution == ExchangeResolution.KeepLocal,
            "Unchanged acknowledged source plus local edits proposes KeepLocal");
        target.ApplyExchange(preview, Choices(preview), Decode);
        Check(target.LoadMaterialDraft(id).Texts["ru"].Summary == "Местная правка", "Repeated source import does not erase local-only changes");
        draft.Texts["ru"].Summary = "Одновременная правка отправителя"; draft = sender.SaveMaterialDraft(draft, metadata);
        using var bothPackage = ExportOpen(sender, folder, "both-change", id);
        preview = target.PreviewExchange(bothPackage);
        Check(Material(preview, id).Status == ExchangeChangeKind.Conflict, "Different changes on both sides of the acknowledged baseline require conflict resolution");
        target.ApplyExchange(preview, Choices(preview), Decode);
        Check(target.LoadMaterialDraft(id).Texts["ru"].Summary == draft.Texts["ru"].Summary, "Explicit conflict resolution applies the selected incoming version");

        // Higher-level catalog revision and raw row changes both invalidate preview safely.
        preview = target.PreviewExchange(bothPackage); target.RenameCatalog("Changed after preview"); hash = Hash(target.DatabasePath);
        Reject(() => target.ApplyExchange(preview, Choices(preview), Decode), "Catalog changes after preview require a fresh plan");
        Check(Hash(target.DatabasePath) == hash, "Rejected stale preview preserves every subsequent local change");
        preview = target.PreviewExchange(bothPackage);
        Sql(target.DatabasePath, "UPDATE material_translations SET summary='External row change' WHERE material_id=$id AND locale='ru';", ("$id", id.ToString()));
        hash = Hash(target.DatabasePath);
        Reject(() => target.ApplyExchange(preview, Choices(preview), Decode), "Content fingerprint rejects stale plans even when an external writer fails to increment catalog revision");
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
        applied = target.ApplyExchange(preview, Choices(preview), Decode);
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
        Reject(() => target.ApplyExchange(preview, new Dictionary<string, ExchangeResolution>(), Decode), "Import never silently applies a received deletion marker");
        Check(Hash(target.DatabasePath) == hash, "Unresolved deletion leaves target bytes unchanged");
        target.ApplyExchange(preview, Choices(preview), Decode);
        Check(!target.LoadMaterialIndex().Any(i => i.Id == id) && target.LoadMaterialIndex().Any(i => i.Id == external.Id) &&
            target.LoadExchangeIndex(includeDeleted: true).Single(i => i.Id == id).IsDeleted,
            "Accepted tombstone deletes only its selected UUID and preserves unselected material and recovery history");
        Check(HashOriginal(target, external.Images.Single().ImageId!.Value) == Hash(originals[0]), "Logical deletion retains original bytes still shared by other material");

        count += CheckImageDeduplication(export, originals[0], metadata, Decode, folder);
        count += CheckAtomicFailure(export, Decode, folder);
        count += CheckChildOwnership(export, originals[0], metadata, Decode, folder);
        count += CheckInvalidPackages(export, folder);
        count += CheckImageReplacement(export, originals[1], metadata, Decode, folder);
        return count;
    }

    private static int CheckImageDeduplication(string export, string original, Func<string, ImageMetadata> metadata,
        Func<Stream, ImageMetadata> decode, string folder)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
        var alias = Mutate(export, folder, "image-alias", """
            UPDATE image_contents SET image_id=$alias;
            UPDATE material_images SET image_id=$alias;
            UPDATE images SET id=$alias;
            """, ("$alias", Guid.NewGuid().ToString()));
        using var target = NewCatalog(folder, "ImageDeduplication");
        var existing = target.SaveMaterialDraft(NewMaterial("Выбранный объект", original), metadata);
        using var package = ExchangePackage.Open(alias); var preview = target.PreviewExchange(package);
        target.ApplyExchange(preview, Choices(preview), decode);
        var imported = target.LoadMaterialDraft(package.Materials.Single().Id);
        Check(imported.Images.Single().ImageId == existing.Images.Single().ImageId &&
            (long)Sql(target.DatabasePath, "SELECT COUNT(*) FROM image_contents;")! == 1,
            "Equal original bytes under different source UUIDs reuse the local image row and remap material-image references");
        Check(imported.Id != existing.Id && target.LoadMaterialIndex().Count(i => i.Name == "Выбранный объект") == 2,
            "Equal display names under different UUIDs remain separate material identities");
        var hash = Hash(target.DatabasePath); preview = target.PreviewExchange(package);
        Check(Material(preview, imported.Id).Status == ExchangeChangeKind.Unchanged &&
            !target.ApplyExchange(preview, Choices(preview), decode).Changed && Hash(target.DatabasePath) == hash,
            "SHA image remapping remains idempotent on later previews and imports");
        var collision = Mutate(export, folder, "image-collision", """
            UPDATE image_contents SET data=zeroblob(length(data));
            UPDATE images SET sha256=$hash,storage_key=$key;
            """, ("$hash", ZeroHash(export)), ("$key", "SPECIMENS/" + ZeroHash(export) + ".jpg"));
        using var invalid = ExchangePackage.Open(collision);
        try { target.PreviewExchange(invalid); throw new Exception("Expected immutable image collision rejection"); }
        catch (CatalogStorageException) { Check(Hash(target.DatabasePath) == hash, "An immutable image UUID reused for different bytes is rejected without catalog writes"); }
        return count;
    }

    private static int CheckAtomicFailure(string export, Func<Stream, ImageMetadata> decode, string folder)
    {
        using var target = NewCatalog(folder, "AtomicFailure"); using var package = ExchangePackage.Open(export);
        Sql(target.DatabasePath, "CREATE TRIGGER fail_exchange BEFORE INSERT ON regions BEGIN SELECT RAISE(ABORT,'simulated late exchange write failure'); END;");
        var preview = target.PreviewExchange(package); var hash = Hash(target.DatabasePath);
        try { target.ApplyExchange(preview, Choices(preview), decode); throw new Exception("Expected late exchange transaction failure"); }
        catch (CatalogStorageException) { }
        if (Hash(target.DatabasePath) != hash || target.GetStatistics().Materials != 0 ||
            (long)Sql(target.DatabasePath, "SELECT COUNT(*) FROM image_contents;")! != 0)
            throw new Exception("Failed exchange must roll back SQL and only newly installed originals");
        Console.WriteLine("PASS Late exchange failure rolls back every row, revision, baseline and newly installed original");
        var backups = Directory.GetFiles(Path.Combine(target.RootDirectory, "BACKUPS"), "before-exchange-*.histosets");
        if (backups.Length != 1) throw new Exception("Expected one recovery backup after exchange failure");
        var restored = backups[0];
        using (var previous = new CatalogStore(restored)) if (previous.GetStatistics().Materials != 0) throw new Exception("Failure backup did not preserve actual old catalog");
        Console.WriteLine("PASS A complete pre-exchange backup remains usable after transaction rollback");
        Sql(target.DatabasePath, "DROP TRIGGER fail_exchange;"); preview = target.PreviewExchange(package);
        if (!target.ApplyExchange(preview, Choices(preview), decode).Changed) throw new Exception("Exchange retry did not complete");
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
        Reject(Mutate(export, folder, "invalid-uuid", "UPDATE materials SET id='not-a-uuid';"), "Exchange rejects invalid identifiers");
        Reject(Mutate(export, folder, "invalid-reference", "UPDATE regions SET material_image_id=$id;", ("$id", Guid.NewGuid().ToString())),
            "Exchange rejects dangling region references");
        Reject(Mutate(export, folder, "unknown-column", "ALTER TABLE materials ADD COLUMN future_field TEXT;"), "Exchange rejects unsupported columns");
        Reject(Mutate(export, folder, "invalid-geometry", "UPDATE regions SET geometry_json=$value;", ("$value", """{"version":999,"kind":"polygon"}""")),
            "Exchange rejects unsupported geometry before target writes");
        Reject(Mutate(export, folder, "inconsistent-calibration", "UPDATE image_calibrations SET pixel_size_x_m=0.25;"),
            "Exchange rejects physical pixel scales inconsistent with recorded dimensions");
        Reject(Mutate(export, folder, "system-tag-spoof", "UPDATE tag_groups SET is_system=0;"), "Reserved system types cannot be imported as custom dictionaries");
        Reject(Mutate(export, folder, "future-format", "PRAGMA user_version=999;"), "A future catalog schema is rejected");
        Reject(Mutate(export, folder, "bad-image-key", "UPDATE images SET storage_key='SPECIMENS/../../outside.bin';"), "Exchange rejects invalid original identifiers");
        Reject(Mutate(export, folder, "damaged-image", "UPDATE image_contents SET data=zeroblob(length(data));"), "Exchange detects changed original bytes of equal length");
        Reject(Mutate(export, folder, "missing-image", "DELETE FROM image_contents;"), "Exchange rejects an image without original bytes");
        var oldPackage = Path.Combine(folder, "old.histosets-exchange");
        File.WriteAllBytes(oldPackage, new byte[] { (byte)'P', (byte)'K', 3, 4 });
        try { using var rejected = ExchangePackage.Open(oldPackage); throw new Exception("Expected old exchange rejection"); }
        catch (CatalogStorageException ex) when (ex.Message.Contains("0.7.3", StringComparison.Ordinal))
        { count++; Console.WriteLine("PASS Old ZIP exchange receives upgrade instructions"); }
        return count;
    }

    private static int CheckChildOwnership(string export, string original, Func<string, ImageMetadata> metadata, Func<Stream, ImageMetadata> decode, string folder)
    {
        using var target = NewCatalog(folder, "ChildOwnership");
        var existingChild = Guid.Parse((string)Sql(export, "SELECT id FROM elements WHERE deleted_utc IS NULL;")!);
        var other = NewMaterial("Unselected local owner", original);
        other.Elements.Add(new() { Id = existingChild }); other = target.SaveMaterialDraft(other, metadata);
        using var package = ExchangePackage.Open(export); var hash = Hash(target.DatabasePath);
        try
        {
            var preview = target.PreviewExchange(package); target.ApplyExchange(preview, Choices(preview), decode);
            throw new Exception("Expected rejection of child UUID already owned by another material");
        }
        catch (CatalogStorageException) { }
        if (Hash(target.DatabasePath) != hash || target.LoadMaterialDraft(other.Id).Elements.Single().Id != existingChild || target.GetStatistics().Materials != 1)
            throw new Exception("Child ownership collision changed existing catalog");
        Console.WriteLine("PASS Imported child UUID cannot steal or modify an element already owned by another local material");
        return 1;
    }

    private static int CheckImageReplacement(string originalExport, string replacement, Func<string, ImageMetadata> metadata,
        Func<Stream, ImageMetadata> decode, string folder)
    {
        using var recipient = NewCatalog(folder, "ReplaceImage");
        var sourcePath = Path.Combine(folder, "edited-copy.histosets");
        File.Copy(originalExport, sourcePath);
        using var source = new CatalogStore(sourcePath);
        Guid id;
        using (var first = ExchangePackage.Open(originalExport))
        {
            id = first.Materials.Single().Id;
            var preview = recipient.PreviewExchange(first);
            recipient.ApplyExchange(preview, Choices(preview), decode);
        }
        var draft = source.LoadMaterialDraft(id);
        draft.Images.Clear();
        draft.Images.Add(new() { SourcePath = replacement, SourceSha256 = Hash(replacement), IsPrimary = true });
        foreach (var element in draft.Elements) element.Regions.Clear();
        draft = source.SaveMaterialDraft(draft, metadata);
        using var full = ExchangePackage.Open(sourcePath);
        if (full.SourceCatalogId != source.GetMetadata().Id)
            throw new Exception("Edited exported catalog incorrectly kept the sender's lineage");
        var plan = recipient.PreviewExchange(full);
        if (Material(plan, id).Status != ExchangeChangeKind.Conflict)
            throw new Exception("A local edit to an exported file did not demand review before overwriting another catalog");
        recipient.ApplyExchange(plan, Choices(plan), decode);
        var imported = recipient.LoadMaterialDraft(id);
        if (imported.Images.Count != 1 || imported.Images.Single().Id != draft.Images.Single().Id ||
            HashOriginal(recipient, imported.Images.Single().ImageId!.Value) != Hash(replacement))
            throw new Exception("Exchange did not replace the active image with the chosen original");
        Console.WriteLine("PASS Direct exchange from edited .histosets uses its own lineage and atomically replaces the sole original");
        return 1;
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
        Texts = value.Texts.OrderBy(p => p.Key), Images = value.Images.Select(i => new { i.ImageId, i.IsPrimary, i.Calibration }),
        Elements = value.Elements.Select(e => new { e.Id, Texts = e.Texts.OrderBy(p => p.Key), Regions = e.Regions.Select(r => new { r.Id, TargetImageId = value.Images.Single(i => i.Id == r.MaterialImageId).ImageId, Geometry = RegionGeometryCodec.Serialize(r.Geometry) }) }),
        Tags = value.TagIds.Order()
    });
    private static ExchangePreviewItem Material(ExchangePreview preview, Guid id) => preview.Items.Single(i => i.Kind == ExchangeEntityKind.Material && i.Id == id);
    private static Dictionary<string, ExchangeResolution> Choices(ExchangePreview preview, ExchangeResolution resolution = ExchangeResolution.UseIncoming) =>
        preview.Items.ToDictionary(i => i.Key, i => i.Status == ExchangeChangeKind.Unchanged ? ExchangeResolution.KeepLocal :
            i.Status == ExchangeChangeKind.LocalChanged && resolution == ExchangeResolution.UseIncoming ? ExchangeResolution.KeepLocal : resolution);
    private static ExchangePackage ExportOpen(CatalogStore store, string folder, string name, Guid id)
    { var path = Path.Combine(folder, name + ".histosets"); store.ExportMaterials(path, [id]); return ExchangePackage.Open(path); }
    private static CatalogStore NewCatalog(string directory, string name)
    {
        var folder = Path.Combine(directory, name);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".histosets");
        CatalogStore.CreateCatalog(path, name);
        return new(path);
    }
    private static string Mutate(string source, string folder, string name, string sql, params (string Name, object? Value)[] args)
    {
        var path = Path.Combine(folder, name + ".histosets");
        File.Copy(source, path);
        Sql(path, "PRAGMA foreign_keys=OFF; " + sql, args);
        return path;
    }
    private static string HashOriginal(CatalogStore store, Guid imageId)
    { using var stream = store.OpenImage(imageId); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string ZeroHash(string path) => Convert.ToHexStringLower(
        SHA256.HashData(new byte[Convert.ToInt32(Sql(path, "SELECT byte_size FROM images;"))]));
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

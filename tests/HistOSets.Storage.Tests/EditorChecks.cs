using System.IO.Compression;
using System.Security.Cryptography;
using HistOSets.Core;
using HistOSets.Storage;
using Microsoft.Data.Sqlite;

internal static class EditorChecks
{
    internal static int Run(string source,string backup,string temp,Func<string,ImageMetadata> metadata)
    {
        var count=0;
        void Check(bool value,string name) { if(!value) throw new Exception(name); count++; Console.WriteLine("PASS "+name); }
        void Reject(Action action,string name)
        {
            try { action(); } catch(CatalogStorageException) { Check(true,name); return; }
            throw new Exception("Expected CatalogStorageException: "+name);
        }
        var original=AtlasLoader.Load(source);
        var root=Path.Combine(temp,"EditorCatalog");
        var copy=Path.Combine(temp,"editor.histosets");
        Guid materialId;
        using(var store=new CatalogStore(root))
        {
            var initialHash=Hash(store.DatabasePath);
            var abandoned=new MaterialDraft();
            abandoned.Texts["ru"].Name="Unsaved";
            abandoned.Images.Add(new() { SourcePath=original.Specimens[0].ImagePath,SourceSha256=Hash(original.Specimens[0].ImagePath),IsPrimary=true });
            var workingCopy=abandoned.Clone(); workingCopy.Texts["ru"].Name="Changed clone";
            Check(abandoned.Texts["ru"].Name=="Unsaved" && Hash(store.DatabasePath)==initialHash && !Directory.Exists(Path.Combine(root,"SPECIMENS")),"Canceled editor copies change neither database nor managed files");
            var empty=store.SaveMaterialDraft(new MaterialDraft(),metadata);
            Check(empty.Revision==1 && store.LoadMaterialIndex("en").Single().Name=="Untitled" && store.LoadMaterialIndex().Single().ImagePath is null,"Blank draft without an image or useful title persists and remains in the editor index");
            var draft=new MaterialDraft { Status="ready" };
            draft.Texts["ru"]=new() { Name="Объект",Summary="Кратко",Description="Подробно" };
            draft.Texts["en"]=new() { Name="Object",Summary="Summary",Description="Description" };
            draft.Texts["und"]=new() { Name="Legacy",Summary="Legacy summary",Description="Legacy details" };
            var image=new MaterialImageDraft { SourcePath=original.Specimens[0].ImagePath,SourceSha256=Hash(original.Specimens[0].ImagePath),IsPrimary=true };
            draft.Images.Add(image);
            var element=new MaterialElementDraft();
            element.Texts["ru"]=new() { Name="Элемент",Summary="Краткое описание",Description="Полное описание" };
            element.Texts["en"]=new() { Name="Element",Summary="Element summary",Description="Element details" };
            element.Regions.Add(new() { MaterialImageId=image.Id,Geometry=RegionGeometry.Polygon([new(0,0),new(100.12345678901235,0),new(40,80)]) });
            element.Regions.Add(new() { MaterialImageId=image.Id,Geometry=RegionGeometry.Rectangle(10.25,20.75,80.5,60.125) });
            element.Regions.Add(new() { MaterialImageId=image.Id,Geometry=RegionGeometry.Ellipse(120.25,150.75,40.125,25.875) });
            draft.Elements.Add(element);
            var type=store.LoadTaxonomy().Groups.Single(g=>g.Code=="topic").Id;
            var tag=store.SaveTag(null,type,"Учебная тема","Teaching topic");
            draft.TagIds.Add(tag);
            draft.AnalogGroupId=store.SaveAnalogGroup(null,"Аналоги","Analogs");
            var saved=store.SaveMaterialDraft(draft,metadata);
            materialId=saved.Id;
            Check(saved.Id==draft.Id && saved.Elements[0].Id==element.Id && saved.Elements[0].Regions.Select(r=>r.Id).SequenceEqual(element.Regions.Select(r=>r.Id)) && saved.Revision==1,
                "Whole material graph keeps stable object, element and region UUIDs on first save");
            Check(draft.Revision==0 && draft.Images[0].SourcePath is not null && saved.Images[0].SourcePath is null && File.Exists(saved.Images[0].ManagedPath),"Save returns a fresh managed working copy and does not mutate the editor's source draft");
            Check(saved.Status=="ready" && saved.Texts["und"].Description=="Legacy details" && saved.TagIds.Contains(tag) && saved.AnalogGroupId==draft.AnalogGroupId,
                "Whole-graph transaction persists readiness, extra locales, staged tags and analog membership");
            Check(saved.Elements[0].Regions.Select(r=>RegionGeometryCodec.Serialize(r.Geometry)).SequenceEqual(element.Regions.Select(r=>RegionGeometryCodec.Serialize(r.Geometry))),
                "Polygon, rectangle and ellipse parameters round-trip exactly through canonical JSON");
            Check(Convert.ToInt64(Sql(store.DatabasePath,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='region_points';"))==0,
                "Schema3 has one authoritative geometry representation and no region_points table");
            var noOpHash=Hash(store.DatabasePath); var revision=store.GetMetadata().ContentRevision;
            var same=store.SaveMaterialDraft(saved.Clone(),_=>throw new Exception("Existing image must not be decoded again"));
            Check(same.Revision==saved.Revision && store.GetMetadata().ContentRevision==revision && Hash(store.DatabasePath)==noOpHash,
                "Unchanged saved draft is a byte-for-byte no-op including revisions");
            var edited=saved.Clone(); edited.Elements[0].Texts["en"].Description="Changed details";
            edited.Elements[0].Regions[2].Geometry=RegionGeometry.Ellipse(120.5,150.75,40.125,25.875);
            var updated=store.SaveMaterialDraft(edited,metadata);
            Check(updated.Revision==saved.Revision+1 && updated.Elements[0].Texts["en"].Description=="Changed details" && Hash(updated.Images[0].ManagedPath!)==Hash(original.Specimens[0].ImagePath),
                "Editing a child updates the material revision once while original image bytes remain unchanged");
            var stableHash=Hash(store.DatabasePath);
            Reject(()=>store.SaveMaterialDraft(saved,metadata),"Stale editor save cannot overwrite a newer material revision");
            Reject(()=>store.DeleteMaterial(saved.Id,saved.Revision),"Stale index deletion cannot delete a newer material revision");
            Check(Hash(store.DatabasePath)==stableHash,"Rejected stale save and delete preserve all database bytes");
            Sql(store.DatabasePath,"CREATE TRIGGER fail_editor BEFORE UPDATE ON regions BEGIN SELECT RAISE(ABORT,'fail after text writes'); END;");
            var failing=updated.Clone(); failing.Texts["ru"].Description="Must roll back"; failing.Elements[0].Regions[0].Geometry=RegionGeometry.Rectangle(2,3,4,5);
            stableHash=Hash(store.DatabasePath);
            Reject(()=>store.SaveMaterialDraft(failing,metadata),"Save handles a SQL failure after earlier graph updates");
            Check(Hash(store.DatabasePath)==stableHash && store.LoadMaterialDraft(updated.Id).Texts["ru"].Description=="Подробно","Failed graph save rolls back translations, regions, metadata and revisions atomically");
            Sql(store.DatabasePath,"DROP TRIGGER fail_editor;");
            var incomplete=updated.Clone(); incomplete.Texts["en"].Summary="";
            Reject(()=>store.SaveMaterialDraft(incomplete,metadata),"Ready graph cannot be saved with a missing required bilingual field");
            incomplete.Status="draft";
            updated=store.SaveMaterialDraft(incomplete,metadata);
            Check(updated.Status=="draft" && updated.Texts["en"].Summary=="","Incomplete content can be deliberately saved as draft");
            var cross=updated.Clone(); cross.Elements[0].Regions[0].MaterialImageId=Guid.NewGuid();
            Reject(()=>store.SaveMaterialDraft(cross,metadata),"Region cannot refer to an image absent from the same working graph");
            var sourceChanged=Path.Combine(temp,"ChangedAfterPreview.jpg"); File.Copy(original.Specimens[0].ImagePath,sourceChanged);
            var changedDraft=new MaterialDraft(); changedDraft.Images.Add(new() { SourcePath=sourceChanged,SourceSha256=Hash(sourceChanged),IsPrimary=true });
            File.WriteAllBytes(sourceChanged,File.ReadAllBytes(original.Specimens[1].ImagePath));
            var countBefore=store.GetStatistics().Materials;
            Reject(()=>store.SaveMaterialDraft(changedDraft,metadata),"A source file changed after preview cannot receive contours for different bytes");
            Check(store.GetStatistics().Materials==countBefore,"Changed-source rejection creates no partial material");
            store.CreateBackup(copy);

            var imageLink=updated.Images[0].Id;
            var removed=updated.Clone(); removed.Images.Clear(); foreach(var e in removed.Elements) e.Regions.Clear();
            removed=store.SaveMaterialDraft(removed,metadata);
            Check(removed.Images.Count==0 && removed.Elements[0].Regions.Count==0,"Explicit image and region removal persists as tombstones");
            var readded=removed.Clone(); var newImage=new MaterialImageDraft { SourcePath=original.Specimens[0].ImagePath,SourceSha256=Hash(original.Specimens[0].ImagePath),IsPrimary=true };
            readded.Images.Add(newImage); readded.Elements[0].Regions.Add(new() { MaterialImageId=newImage.Id,Geometry=RegionGeometry.Rectangle(1,2,3,4) });
            readded=store.SaveMaterialDraft(readded,metadata);
            Check(readded.Images[0].Id==imageLink && readded.Elements[0].Regions[0].MaterialImageId==imageLink,"Re-adding a removed original revives its stable association UUID and remaps new contours");

            var shared=new MaterialDraft(); shared.Images.Add(new() { ImageId=readded.Images[0].ImageId,IsPrimary=true });
            shared=store.SaveMaterialDraft(shared,metadata);
            var imagePath=shared.Images[0].ManagedPath!;
            store.DeleteMaterial(readded.Id,readded.Revision);
            Check(!store.LoadMaterialIndex().Any(m=>m.Id==readded.Id) && File.Exists(imagePath) && store.LoadMaterialDraft(shared.Id).Images.Count==1,
                "Deleting a material soft-deletes its graph and preserves a shared original and its other material");
            Check(Convert.ToInt64(Sql(store.DatabasePath,"SELECT COUNT(*) FROM regions WHERE material_id=$id AND deleted_utc IS NULL;",("$id",readded.Id.ToString())))==0 &&
                Convert.ToInt64(Sql(store.DatabasePath,"SELECT COUNT(*) FROM elements WHERE material_id=$id AND deleted_utc IS NULL;",("$id",readded.Id.ToString())))==0,
                "Material deletion cascades active elements and regions to deletion markers");
        }
        var restored=Path.Combine(temp,"EditorRestored"); CatalogStore.RestoreBackup(copy,restored);
        using(var store=new CatalogStore(restored))
        {
            var draft=store.LoadMaterialDraft(materialId);
            Check(draft.Elements[0].Regions.Count==3 && draft.Elements[0].Regions[2].Geometry.Kind=="ellipse" && draft.Texts["und"].Name=="Legacy",
                "Schema3 backup restores editable typed geometry and all language records");
        }

        var oldTwo=FoundationChecks.MakeSchema1(source,backup,Path.Combine(temp,"ActualSchema2"));
        var db=Path.Combine(oldTwo,"catalog.sqlite");
        Sql(db,File.ReadAllText(Path.Combine(source,"src","HistOSets.Storage","Migrations","002_catalog_foundation.sql")));
        var seed=Path.Combine(temp,"schema2-reference.sqlite");
        using(var archive=ZipFile.OpenRead(backup)) archive.GetEntry("catalog.sqlite")!.ExtractToFile(seed);
        Sql(db,"""
            ATTACH DATABASE $source AS seed;
            INSERT INTO tag_groups SELECT * FROM seed.tag_groups;
            INSERT INTO tag_group_translations SELECT * FROM seed.tag_group_translations;
            """,("$source",seed));
        File.Delete(seed);
        Sql(db,"""
            UPDATE catalog_metadata SET name='Actual old catalog',content_revision=17;
            UPDATE material_images SET is_primary=1;
            INSERT INTO schema_migrations VALUES(2,'2026-10-01T00:00:00+00:00'); PRAGMA user_version=2;
            """);
        var oldPoint=Convert.ToDouble(Sql(db,"SELECT x FROM region_points WHERE x>1968 LIMIT 1;"));
        using(var store=new CatalogStore(oldTwo))
        {
            Check(store.GetMetadata().FormatVersion==3 && store.GetMetadata().Name=="Actual old catalog" && store.GetMetadata().ContentRevision==17,
                "Real schema2 upgrades to schema3 without changing catalog name or content revision");
            Check(store.Load().Specimens.SelectMany(s=>s.Elements).SelectMany(e=>e.Polygons).SelectMany(p=>p.Points).Any(p=>p.X==oldPoint),
                "Schema2-to-3 migration retains an original out-of-image vertex exactly");
            var oldBackup=store.LastMigrationBackupPath!;
            var originalTwo=Path.Combine(temp,"RestoreActualSchema2"); CatalogStore.RestoreBackup(oldBackup,originalTwo);
            Check(Convert.ToInt64(Sql(Path.Combine(originalTwo,"catalog.sqlite"),"PRAGMA user_version;"))==2 &&
                Convert.ToInt64(Sql(Path.Combine(originalTwo,"catalog.sqlite"),"SELECT COUNT(*) FROM region_points;"))>0,
                "Automatic pre-JSON backup retains actual schema2 and its complete point table");
        }
        return count;
    }

    private static object? Sql(string db,string sql,params (string Name,object? Value)[] parameters)
    {
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=db,ForeignKeys=true,Pooling=false}.ToString());
        connection.Open(); using var command=connection.CreateCommand(); command.CommandText=sql;
        foreach(var (name,value) in parameters) command.Parameters.AddWithValue(name,value??DBNull.Value);
        return command.ExecuteScalar();
    }
    private static string Hash(string path) { using var input=File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input)); }
}

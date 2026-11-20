using System.Security.Cryptography;
using HistOSets.Core;
using HistOSets.Storage;
using Microsoft.Data.Sqlite;

internal static class EditorChecks
{
    internal static int Run(string source,string temp,Func<string,ImageMetadata> metadata)
    {
        var count=0;
        void Check(bool value,string name) { if(!value) throw new Exception(name); count++; Console.WriteLine("PASS "+name); }
        void Reject(Action action,string name)
        {
            try { action(); } catch(CatalogStorageException) { Check(true,name); return; }
            throw new Exception("Expected CatalogStorageException: "+name);
        }
        var originals=Directory.EnumerateFiles(Path.Combine(source,"SPECIMENS"),"*.jpg",SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Take(2).ToArray();
        var original=originals[0];
        var differentOriginal=originals[1];
        var expectedImageHash=Hash(original);
        var catalog=Path.Combine(temp,"EditorCatalog.histosets");
        CatalogStore.CreateCatalog(catalog,"Editor");
        var copy=Path.Combine(temp,"editor.histosets");
        Guid materialId;
        using(var store=new CatalogStore(catalog))
        {
            var initialHash=Hash(store.DatabasePath);
            var abandoned=new MaterialDraft();
            abandoned.Texts["ru"].Name="Unsaved";
            abandoned.Images.Add(new() { SourcePath=original,SourceSha256=Hash(original),IsPrimary=true });
            var workingCopy=abandoned.Clone(); workingCopy.Texts["ru"].Name="Changed clone";
            Check(abandoned.Texts["ru"].Name=="Unsaved" && Hash(store.DatabasePath)==initialHash && ActiveContents(store.DatabasePath)==0,"Canceled editor copies add neither catalog changes nor image bytes");
            var empty=store.SaveMaterialDraft(new MaterialDraft(),metadata);
            Check(empty.Revision==1 && store.LoadMaterialIndex("en").Single().Name=="Untitled" && store.LoadMaterialIndex().Single().ImageId is null,"Blank draft without an image or useful title persists and remains in the editor index");
            var draft=new MaterialDraft { Status="ready" };
            draft.Texts["ru"]=new() { Name="Объект",Summary="Кратко",Description="Подробно" };
            draft.Texts["en"]=new() { Name="Object",Summary="Summary",Description="Description" };
            draft.Texts["und"]=new() { Name="Legacy",Summary="Legacy summary",Description="Legacy details" };
            var image=new MaterialImageDraft { SourcePath=original,SourceSha256=Hash(original),IsPrimary=true };
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
            Check(draft.Revision==0 && draft.Images[0].SourcePath is not null && saved.Images[0].SourcePath is null
                && saved.Images[0].Sha256==expectedImageHash && ImageHash(store,saved.Images[0].ImageId)==expectedImageHash,
                "Save returns a fresh working copy with its original embedded and does not mutate the editor's source draft");
            Check(saved.Status=="ready" && saved.Texts["und"].Description=="Legacy details" && saved.TagIds.Contains(tag) && saved.AnalogGroupId==draft.AnalogGroupId,
                "Whole-graph transaction persists readiness, extra locales, staged tags and analog membership");
            Check(saved.Elements[0].Regions.Select(r=>RegionGeometryCodec.Serialize(r.Geometry)).SequenceEqual(element.Regions.Select(r=>RegionGeometryCodec.Serialize(r.Geometry))),
                "Polygon, rectangle and ellipse parameters round-trip exactly through canonical JSON");
            Check(Convert.ToInt64(Sql(store.DatabasePath,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='region_points';"))==0,
                "The native catalog has one authoritative geometry representation and no region_points table");
            var withTwoImages=saved.Clone();
            withTwoImages.Images.Add(new() { SourcePath=differentOriginal,SourceSha256=Hash(differentOriginal) });
            Reject(()=>store.SaveMaterialDraft(withTwoImages,metadata),"A card cannot save a second image");
            Check(ActiveContents(store.DatabasePath)==1 && store.LoadMaterialDraft(saved.Id).Images.Count==1,
                "Rejected multi-image edits preserve the first image and introduce no image bytes");
            var noOpHash=Hash(store.DatabasePath); var revision=store.GetMetadata().ContentRevision;
            var same=store.SaveMaterialDraft(saved.Clone(),_=>throw new Exception("Existing image must not be decoded again"));
            Check(same.Revision==saved.Revision && store.GetMetadata().ContentRevision==revision && Hash(store.DatabasePath)==noOpHash,
                "Unchanged saved draft is a byte-for-byte no-op including revisions");
            var edited=saved.Clone(); edited.Elements[0].Texts["en"].Description="Changed details";
            edited.Elements[0].Regions[2].Geometry=RegionGeometry.Ellipse(120.5,150.75,40.125,25.875);
            var updated=store.SaveMaterialDraft(edited,metadata);
            Check(updated.Revision==saved.Revision+1 && updated.Elements[0].Texts["en"].Description=="Changed details" && ImageHash(store,updated.Images[0].ImageId)==expectedImageHash,
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
            var sourceChanged=Path.Combine(temp,"ChangedAfterPreview.jpg"); File.Copy(original,sourceChanged);
            var changedDraft=new MaterialDraft(); changedDraft.Images.Add(new() { SourcePath=sourceChanged,SourceSha256=Hash(sourceChanged),IsPrimary=true });
            File.WriteAllBytes(sourceChanged,File.ReadAllBytes(differentOriginal));
            var countBefore=store.GetStatistics().Materials;
            Reject(()=>store.SaveMaterialDraft(changedDraft,metadata),"A source file changed after preview cannot receive contours for different bytes");
            Check(store.GetStatistics().Materials==countBefore,"Changed-source rejection creates no partial material");
            var newWithRegion=new MaterialDraft();
            var desiredImage=new MaterialImageDraft { SourcePath=differentOriginal,SourceSha256=Hash(differentOriginal),IsPrimary=true };
            newWithRegion.Images.Add(desiredImage);
            var newElement=new MaterialElementDraft();
            newElement.Regions.Add(new() { MaterialImageId=desiredImage.Id,Geometry=RegionGeometry.Rectangle(1,2,3,4) });
            newWithRegion.Elements.Add(newElement);
            Sql(store.DatabasePath,"CREATE TRIGGER fail_new_region BEFORE INSERT ON regions BEGIN SELECT RAISE(ABORT,'fail after image blob insertion'); END;");
            var originalContentCount=ActiveContents(store.DatabasePath);
            Reject(()=>store.SaveMaterialDraft(newWithRegion,metadata),"A failure after inserting the original rolls back the new card");
            Check(ActiveContents(store.DatabasePath)==originalContentCount && !store.LoadMaterialIndex().Any(m=>m.Id==newWithRegion.Id),
                "The image payload and the material graph share the same transaction");
            Sql(store.DatabasePath,"DROP TRIGGER fail_new_region;");
            store.CreateBackup(copy);

            var imageLink=updated.Images[0].Id;
            var removed=updated.Clone(); removed.Images.Clear(); foreach(var e in removed.Elements) e.Regions.Clear();
            removed=store.SaveMaterialDraft(removed,metadata);
            Check(removed.Images.Count==0 && removed.Elements[0].Regions.Count==0,"Explicit image and region removal persists as tombstones");
            var readded=removed.Clone(); var newImage=new MaterialImageDraft { SourcePath=original,SourceSha256=Hash(original),IsPrimary=true };
            readded.Images.Add(newImage); readded.Elements[0].Regions.Add(new() { MaterialImageId=newImage.Id,Geometry=RegionGeometry.Rectangle(1,2,3,4) });
            readded=store.SaveMaterialDraft(readded,metadata);
            Check(readded.Images[0].Id==imageLink && readded.Elements[0].Regions[0].MaterialImageId==imageLink,"Re-adding a removed original revives its stable association UUID and remaps new contours");

            var shared=new MaterialDraft(); shared.Images.Add(new() { ImageId=readded.Images[0].ImageId,IsPrimary=true });
            shared=store.SaveMaterialDraft(shared,metadata);
            var sharedImageId=shared.Images[0].ImageId;
            store.DeleteMaterial(readded.Id,readded.Revision);
            Check(!store.LoadMaterialIndex().Any(m=>m.Id==readded.Id) && ImageHash(store,sharedImageId)==expectedImageHash && store.LoadMaterialDraft(shared.Id).Images.Count==1,
                "Deleting a material soft-deletes its graph and preserves a shared original and its other material");
            Check(Convert.ToInt64(Sql(store.DatabasePath,"SELECT COUNT(*) FROM regions WHERE material_id=$id AND deleted_utc IS NULL;",("$id",readded.Id.ToString())))==0 &&
                Convert.ToInt64(Sql(store.DatabasePath,"SELECT COUNT(*) FROM elements WHERE material_id=$id AND deleted_utc IS NULL;",("$id",readded.Id.ToString())))==0,
                "Material deletion cascades active elements and regions to deletion markers");
        }
        var restored=Path.Combine(temp,"EditorRestored.histosets"); CatalogStore.CopyCatalog(copy,restored);
        using(var store=new CatalogStore(restored))
        {
            var draft=store.LoadMaterialDraft(materialId);
            Check(draft.Elements[0].Regions.Count==3 && draft.Elements[0].Regions[2].Geometry.Kind=="ellipse" && draft.Texts["und"].Name=="Legacy"
                && ImageHash(store,draft.Images[0].ImageId)==expectedImageHash,
                "A single-file catalog copy preserves original bytes, editable typed geometry and all language records");
        }

        return count;
    }

    private static long ActiveContents(string path) => (long)Sql(path,"SELECT COUNT(*) FROM image_contents;")!;
    private static string ImageHash(CatalogStore store,Guid? imageId)
    {
        using var image=store.OpenImage(imageId!.Value);
        return Convert.ToHexStringLower(SHA256.HashData(image));
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

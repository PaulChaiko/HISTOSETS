using System.Security.Cryptography;
using HistOSets.Core;
using HistOSets.Storage;
using Microsoft.Data.Sqlite;

internal static class MeasurementStorageChecks
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
        var original=Directory.EnumerateFiles(Path.Combine(source,"SPECIMENS"),"*.jpg",SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).First();
        var catalog=Path.Combine(temp,"CalibratedEditor.histosets");
        CatalogStore.CreateCatalog(catalog,"Calibrated editor");
        var backup=Path.Combine(temp,"calibrated-compound.histosets");
        Guid materialId;
        string compoundJson;
        ImageCalibration persisted;
        using(var store=new CatalogStore(catalog))
        {
            var draft=new MaterialDraft();
            draft.Texts["ru"].Name="Калиброванный объект";
            var first=new MaterialImageDraft { SourcePath=original,SourceSha256=Hash(original),IsPrimary=true };
            draft.Images.Add(first);
            var dimensions=metadata(original);
            first.Calibration=ImageCalibration.FromDimensions(dimensions.Width*2,dimensions.Height*5,"µm",dimensions.Width,dimensions.Height);
            var element=new MaterialElementDraft();
            element.Texts["ru"].Name="Составная область";
            var compound=RegionGeometryCodec.Deserialize("""
                {"version":2,"kind":"compound","coordinateSpace":"pixels","fillRule":"evenOdd","rings":[[[0,0],[20,0],[20,20],[0,20]],[[5,5],[15,5],[15,15],[5,15]],[[30,0],[40,0],[40,10],[30,10]]]}
                """);
            element.Regions.Add(new() { MaterialImageId=first.Id,Geometry=compound });
            draft.Elements.Add(element);
            var untouched=Hash(store.DatabasePath);
            var abandoned=draft.Clone(); abandoned.Images[0].Calibration=abandoned.Images[0].Calibration! with { SourceNote="Abandoned edit" };
            Check(first.Calibration.SourceNote=="" && Hash(store.DatabasePath)==untouched,"Calibration working-copy history and discard leave original DTO and catalog unchanged");
            draft=store.SaveMaterialDraft(draft,metadata); materialId=draft.Id;
            persisted=draft.Images[0].Calibration!;
            compoundJson=RegionGeometryCodec.Serialize(draft.Elements[0].Regions[0].Geometry);
            Check(Math.Abs(persisted.PixelSizeXMetres-2e-6)<1e-20 && Math.Abs(persisted.PixelSizeYMetres-5e-6)<1e-20 && persisted.InputUnit=="um",
                "Manual whole-image physical dimensions persist independent SI X/Y scales and canonical micrometre units");
            Check(compoundJson==RegionGeometryCodec.Serialize(compound) && draft.Elements[0].Regions[0].Id==element.Regions[0].Id,
                "Compound holes and disconnected components persist canonical JSON with a stable region UUID");
            var views=store.Load().Specimens.Where(s=>s.MaterialId==draft.Id).ToArray();
            Check(views.Length==2 && views.All(s=>s.MaterialImageId==first.Id && s.Calibration==persisted),
                "Viewer projections expose the same image calibration in both languages");
            Check(views.First(s=>s.MaterialImageId==first.Id).Elements[0].Regions[0].Geometry.Kind=="compound" && views.First(s=>s.MaterialImageId==first.Id).Elements[0].Polygons.Count==0,
                "Compound geometry stays authoritative in Regions instead of a lossy single-polygon fallback");
            var id=(string)Sql(store.DatabasePath,"SELECT id FROM image_calibrations WHERE deleted_utc IS NULL;")!;
            var hash=Hash(store.DatabasePath); var catalogRevision=store.GetMetadata().ContentRevision;
            draft=store.SaveMaterialDraft(draft.Clone(),metadata);
            Check(Hash(store.DatabasePath)==hash && store.GetMetadata().ContentRevision==catalogRevision,"Saving unchanged calibrated compound content is a byte-for-byte no-op");
            var old=draft.Clone();
            var note=draft.Clone(); note.Images[0].Calibration=note.Images[0].Calibration! with { SourceNote="Measured original, not display DPI" };
            draft=store.SaveMaterialDraft(note,metadata);
            Check(draft.Revision==old.Revision+1 && (string)Sql(store.DatabasePath,"SELECT id FROM image_calibrations WHERE deleted_utc IS NULL;")! ==id && draft.Images[0].Calibration!.SourceNote==note.Images[0].Calibration!.SourceNote,
                "Calibration provenance edit preserves its UUID and advances the material revision once");
            hash=Hash(store.DatabasePath);
            Reject(()=>store.SaveMaterialDraft(old,metadata),"A stale editor cannot overwrite a newer calibration");
            Check(Hash(store.DatabasePath)==hash,"Stale calibration rejection leaves database bytes unchanged");
            Sql(store.DatabasePath,"CREATE TRIGGER fail_calibrated_region BEFORE UPDATE ON regions BEGIN SELECT RAISE(ABORT,'region write failed'); END;");
            var failing=draft.Clone(); failing.Images[0].Calibration=failing.Images[0].Calibration! with { SourceNote="Must roll back" };
            failing.Texts["ru"].Name="Must roll back"; failing.Elements[0].Regions[0].Geometry=RegionGeometry.Rectangle(1,2,3,4);
            hash=Hash(store.DatabasePath);
            Reject(()=>store.SaveMaterialDraft(failing,metadata),"A later region failure aborts the combined calibration, text and geometry save");
            Check(Hash(store.DatabasePath)==hash && store.LoadMaterialDraft(draft.Id).Images[0].Calibration==draft.Images[0].Calibration,
                "Calibration, geometry, text, UUIDs and revisions roll back in one transaction");
            Sql(store.DatabasePath,"DROP TRIGGER fail_calibrated_region;");
            foreach(var invalid in new[]
            {
                new ImageCalibration(0,1),new ImageCalibration(double.NaN,1),new ImageCalibration(1,double.PositiveInfinity),
                new ImageCalibration(1,1,InputWidth:1,InputHeight:1,InputUnit:"px"),
                new ImageCalibration(1,1,InputWidth:1),
                new ImageCalibration(1,1,InputWidth:double.PositiveInfinity,InputHeight:1,InputUnit:"m")
            })
            {
                var bad=draft.Clone(); bad.Images[0].Calibration=invalid;
                Reject(()=>store.SaveMaterialDraft(bad,metadata),"Invalid, incomplete or nonfinite physical calibration is rejected before persistence");
            }
            var mismatched=draft.Clone(); mismatched.Images[0].Calibration=mismatched.Images[0].Calibration! with { InputWidth=1 };
            Reject(()=>store.SaveMaterialDraft(mismatched,metadata),"Calibration source dimensions must match the immutable original's actual pixel dimensions");
            foreach(var unit in new[] { "m","cm","mm","um","nm" })
            {
                var variant=draft.Clone(); variant.Images[0].Calibration=ImageCalibration.FromDimensions(200,500,unit,dimensions.Width,dimensions.Height);
                draft=store.SaveMaterialDraft(variant,metadata);
                Check(draft.Images[0].Calibration==variant.Images[0].Calibration,"Physical input unit "+unit+" round-trips with its source dimensions");
            }
            var removed=draft.Clone(); removed.Images[0].Calibration=null;
            draft=store.SaveMaterialDraft(removed,metadata);
            Check(draft.Images[0].Calibration is null && (long)Sql(store.DatabasePath,"SELECT COUNT(*) FROM image_calibrations WHERE deleted_utc IS NULL;")! ==0 &&
                (string)Sql(store.DatabasePath,"SELECT id FROM image_calibrations;")! ==id,"Explicit calibration removal soft-deletes the calibration without deleting its identity");
            draft.Images[0].Calibration=persisted;
            draft=store.SaveMaterialDraft(draft,metadata);
            Check((string)Sql(store.DatabasePath,"SELECT id FROM image_calibrations WHERE deleted_utc IS NULL;")! ==id &&
                (long)Sql(store.DatabasePath,"SELECT COUNT(*) FROM image_calibrations;")! ==1,"Restoring calibration on the same image revives its stable UUID");
            var shared=new MaterialDraft(); shared.Images.Add(new() { ImageId=draft.Images[0].ImageId,IsPrimary=true });
            shared=store.SaveMaterialDraft(shared,metadata);
            Check(shared.Images[0].Calibration is null,"Sharing original image bytes does not silently share object-specific calibration");
            store.CreateBackup(backup);
            var removedImage=draft.Clone(); removedImage.Images.RemoveAt(0); removedImage.Elements[0].Regions.Clear();
            removedImage=store.SaveMaterialDraft(removedImage,metadata);
            Check((long)Sql(store.DatabasePath,"SELECT COUNT(*) FROM image_calibrations WHERE deleted_utc IS NULL;")! ==0,"Removing the image tombstones its calibration atomically with its regions");
            removedImage.Images.Add(new() { ImageId=shared.Images[0].ImageId });
            removedImage=store.SaveMaterialDraft(removedImage,metadata);
            Check(removedImage.Images.All(i=>i.Calibration is null),"Re-adding a removed image without explicit calibration does not revive an obsolete scale");
        }
        var restoredRoot=Path.Combine(temp,"CalibratedRestored.histosets"); CatalogStore.CopyCatalog(backup,restoredRoot);
        using(var restored=new CatalogStore(restoredRoot))
        {
            var draft=restored.LoadMaterialDraft(materialId);
            Check(draft.Images[0].Calibration==persisted &&
                RegionGeometryCodec.Serialize(draft.Elements[0].Regions[0].Geometry)==compoundJson,"Native single-file copy preserves compound holes, calibration, source values and geometry exactly");
            restored.DeleteMaterial(draft.Id,draft.Revision);
            Check((long)Sql(restored.DatabasePath,"SELECT COUNT(*) FROM image_calibrations WHERE deleted_utc IS NULL;")! ==0,"Deleting a material soft-deletes its active calibration");
        }

        return count;
    }

    private static SqliteConnection Open(string db)
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=db,ForeignKeys=true,Pooling=false }.ToString()); connection.Open(); return connection;
    }
    private static object? Sql(string db,string sql,params (string Name,object? Value)[] parameters)
    {
        using var connection=Open(db); using var command=connection.CreateCommand(); command.CommandText=sql;
        foreach(var (name,value) in parameters) command.Parameters.AddWithValue(name,value??DBNull.Value);
        return command.ExecuteScalar();
    }
    private static string Hash(string path) { using var input=File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input)); }
}

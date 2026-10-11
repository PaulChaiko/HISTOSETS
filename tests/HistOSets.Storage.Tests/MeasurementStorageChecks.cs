using System.Security.Cryptography;
using System.Text.Json;
using HistOSets.Core;
using HistOSets.Storage;
using Microsoft.Data.Sqlite;

internal static class MeasurementStorageChecks
{
    internal static int Run(string source,string baselineBackup,string temp,Func<string,ImageMetadata> metadata)
    {
        var count=0;
        void Check(bool value,string name) { if(!value) throw new Exception(name); count++; Console.WriteLine("PASS "+name); }
        void Reject(Action action,string name)
        {
            try { action(); } catch(CatalogStorageException) { Check(true,name); return; }
            throw new Exception("Expected CatalogStorageException: "+name);
        }
        var originals=AtlasLoader.Load(source).Specimens.Select(s=>s.ImagePath).Distinct().Take(2).ToArray();
        var root=Path.Combine(temp,"CalibratedEditor");
        var backup=Path.Combine(temp,"calibrated-compound.histosets");
        Guid materialId;
        string compoundJson;
        ImageCalibration persisted;
        using(var store=new CatalogStore(root))
        {
            var draft=new MaterialDraft();
            draft.Texts["ru"].Name="Калиброванный объект";
            var first=new MaterialImageDraft { SourcePath=originals[0],SourceSha256=Hash(originals[0]),IsPrimary=true };
            var second=new MaterialImageDraft { SourcePath=originals[1],SourceSha256=Hash(originals[1]) };
            draft.Images.AddRange([first,second]);
            var dimensions=metadata(originals[0]);
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
            Check(draft.Images[1].Calibration is null,"A second image receives no invented calibration from DPI or another image");
            Check(compoundJson==RegionGeometryCodec.Serialize(compound) && draft.Elements[0].Regions[0].Id==element.Regions[0].Id,
                "Compound holes and disconnected components persist canonical JSON with a stable region UUID");
            var views=store.Load().Specimens.Where(s=>s.MaterialId==draft.Id).ToArray();
            Check(views.Where(s=>s.MaterialImageId==first.Id).All(s=>s.Calibration==persisted) && views.Where(s=>s.MaterialImageId==second.Id).All(s=>s.Calibration is null),
                "Viewer projections expose only their own image calibration in every language");
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
        var restoredRoot=Path.Combine(temp,"CalibratedRestored"); CatalogStore.RestoreBackup(backup,restoredRoot);
        using(var restored=new CatalogStore(restoredRoot))
        {
            var draft=restored.LoadMaterialDraft(materialId);
            Check(restored.GetMetadata().FormatVersion==6 && restored.LastMigrationBackupPath is null && draft.Images[0].Calibration==persisted &&
                RegionGeometryCodec.Serialize(draft.Elements[0].Regions[0].Geometry)==compoundJson,"Schema6 backup preserves compound holes, calibration, source values and geometry exactly");
            restored.DeleteMaterial(draft.Id,draft.Revision);
            Check((long)Sql(restored.DatabasePath,"SELECT COUNT(*) FROM image_calibrations WHERE deleted_utc IS NULL;")! ==0,"Deleting a material soft-deletes its active calibration");
        }

        var schema3=MakeSchema3(source,baselineBackup,Path.Combine(temp,"ActualSchema3"));
        var database=Path.Combine(schema3,"catalog.sqlite");
        // Include unusual, valid formatting and deleted scientific records in the migration fixture.
        Sql(database,"UPDATE regions SET geometry_json='  '||geometry_json||char(10); UPDATE regions SET deleted_utc=modified_utc WHERE id=(SELECT id FROM regions ORDER BY id LIMIT 1);");
        Sql(database,"""
            UPDATE regions SET geometry_type='rectangle',geometry_json='{ "version":1,"kind":"rectangle","coordinateSpace":"pixels","x":-0.0,"y":20.75,"width":80.5,"height":60.125 }'
            WHERE id=(SELECT id FROM regions ORDER BY id LIMIT 1 OFFSET 1);
            UPDATE regions SET geometry_type='ellipse',geometry_json='{ "version":1,"kind":"ellipse","coordinateSpace":"pixels","x":120.25,"y":150.75,"width":40.125,"height":25.875 }'
            WHERE id=(SELECT id FROM regions ORDER BY id LIMIT 1 OFFSET 2);
            """);
        var image=(string)Sql(database,"SELECT id FROM material_images ORDER BY id LIMIT 1;")!;
        Sql(database,"""
            INSERT INTO image_calibrations(id,material_image_id,pixel_size_x_m,pixel_size_y_m,source_kind,source_note,created_utc,modified_utc,origin_catalog_id)
            SELECT $id,$image,0.000002,0.000005,'scale_bar','Legacy independent calibration',created_utc,created_utc,catalog_id FROM catalog_metadata;
            INSERT INTO image_calibrations(id,material_image_id,pixel_size_x_m,pixel_size_y_m,source_kind,source_note,created_utc,modified_utc,deleted_utc,origin_catalog_id)
            SELECT $deleted,$image,0.000003,0.000006,'image_metadata','Retired calibration',created_utc,created_utc,created_utc,catalog_id FROM catalog_metadata;
            """,("$id",Guid.NewGuid().ToString()),("$deleted",Guid.NewGuid().ToString()),("$image",image));
        var regions=Snapshot(database,"regions"); var calibrations=Snapshot(database,"image_calibrations");
        string oldBackup;
        using(var upgraded=new CatalogStore(schema3))
        {
            Check(upgraded.GetMetadata().FormatVersion==6 && (long)Sql(database,"SELECT COUNT(*) FROM schema_migrations;")! ==6,"Real schema3 migrates to schema6 exactly once");
            Check(Snapshot(database,"regions")==regions,"Schema3 migration preserves every JSON byte, region UUID, revision, provenance and deletion marker");
            Check(Snapshot(database,"image_calibrations")==calibrations,"Schema3 migration preserves active and historical calibration rows exactly");
            oldBackup=upgraded.LastMigrationBackupPath!;
            Check(Path.GetFileName(oldBackup).StartsWith("before-schema6-",StringComparison.Ordinal),"Schema6 migration exposes an explicitly named pre-upgrade recovery archive");
        }
        var oldRestored=Path.Combine(temp,"RestoreOriginalSchema3"); CatalogStore.RestoreBackup(oldBackup,oldRestored);
        var oldDb=Path.Combine(oldRestored,"catalog.sqlite");
        Check((long)Sql(oldDb,"PRAGMA user_version;")! ==3 && Snapshot(oldDb,"regions")==regions && Snapshot(oldDb,"image_calibrations")==calibrations,
            "Pre-schema6 backup restores actual schema3 and all its scientific data before opening");
        using(var upgraded=new CatalogStore(oldRestored)) Check(Snapshot(oldDb,"regions")==regions && Snapshot(oldDb,"image_calibrations")==calibrations,
            "Restored schema3 backup migrates again without changing any scientific row");
        using(var reopened=new CatalogStore(schema3)) Check(reopened.LastMigrationBackupPath is null,"Opening current schema6 never creates a redundant migration backup");

        var failed=MakeSchema3(source,baselineBackup,Path.Combine(temp,"FailSchema4")); var failedDb=Path.Combine(failed,"catalog.sqlite");
        Sql(failedDb,"CREATE TRIGGER fail_schema4 BEFORE INSERT ON schema_migrations WHEN NEW.version=4 BEGIN SELECT RAISE(ABORT,'final migration write failed'); END;");
        var failedHash=Hash(failedDb);
        Reject(()=> { using var rejected=new CatalogStore(failed); },"Schema4 migration reports failure after table copies and renames");
        Check(Hash(failedDb)==failedHash && (long)Sql(failedDb,"PRAGMA user_version;")! ==3,"Failed schema4 migration restores byte-identical original tables, indexes, JSON and metadata");
        Check(Directory.GetFiles(Path.Combine(failed,"BACKUPS"),"before-schema6-*.histosets").Length==1,"A complete old schema3 archive remains available after migration rollback");
        return count;
    }

    private static string MakeSchema3(string source,string backup,string root)
    {
        CatalogStore.RestoreBackup(backup,root);
        var db=Path.Combine(root,"catalog.sqlite");
        // Rebuild with the shipped v3 DDL rather than merely lying about user_version.
        Sql(db,File.ReadAllText(Path.Combine(source,"src","HistOSets.Storage","Migrations","003_editor_geometry.sql"))+"""
            INSERT INTO regions_v3 SELECT * FROM regions;
            DROP TABLE regions;
            ALTER TABLE regions_v3 RENAME TO regions;
            CREATE INDEX ix_regions_element ON regions(element_id,sort_order);
            CREATE INDEX ix_regions_image ON regions(material_image_id);
            DROP TABLE training_prompt_translations;
            DROP TABLE training_prompts;
            DROP TABLE exchange_baselines;
            DELETE FROM schema_migrations WHERE version>3;
            PRAGMA user_version=3;
            """);
        return root;
    }

    private static string Snapshot(string db,string table)
    {
        using var connection=Open(db); using var command=connection.CreateCommand(); command.CommandText=$"SELECT * FROM {table} ORDER BY id;";
        using var reader=command.ExecuteReader(); var rows=new List<object?[]>();
        while(reader.Read()) rows.Add(Enumerable.Range(0,reader.FieldCount).Select(i=>reader.IsDBNull(i)?null:reader.GetValue(i)).ToArray());
        return JsonSerializer.Serialize(rows);
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

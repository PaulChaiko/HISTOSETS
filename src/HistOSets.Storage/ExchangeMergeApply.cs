using HistOSets.Core;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

public sealed partial class CatalogStore
{
    /// <summary>Applies explicitly resolved whole graphs atomically after a complete recovery snapshot.</summary>
    public ExchangeImportResult ApplyExchange(ExchangePreview preview,IReadOnlyDictionary<string,ExchangeResolution> choices,
        Func<string,ImageMetadata>? readMetadata=null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(choices);
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(preview.TargetPath!=DatabasePath) throw new CatalogStorageException("План обмена относится к другому каталогу.");
            var package=preview.Package;
            package.VerifyFiles();
            if(readMetadata is not null) ValidateDecodedExchangeImages(package,readMetadata);
            var resolutions=ExchangeResolutions(preview,choices);
            var incoming=ExchangeEntities(package.Tables).ToDictionary(e=>e.Key,StringComparer.Ordinal);
            var installed=new List<string>();
            var committed=false;
            try
            {
                using var connection=Open(DatabasePath,SqliteOpenMode.ReadWrite);
                Validate(connection);
                _=ReferencedFiles(connection,RootDirectory);
                CheckExchangePreview(connection,null,preview);
                var local=ReadExchangeTables(connection,null);
                var locals=ExchangeEntities(local).ToDictionary(e=>e.Key,StringComparer.Ordinal);
                var changed=preview.Items.Where(i=>resolutions[i.Key]==ExchangeResolution.UseIncoming &&
                    (!locals.TryGetValue(i.Key,out var old) || ExchangeEntityFingerprint(old,local)!=ExchangeEntityFingerprint(incoming[i.Key],package.Tables)))
                    .Select(i=>i.Key).ToHashSet(StringComparer.Ordinal);
                var baselines=ReadExchangeBaselines(connection,null,package.SourceCatalogId.ToString());
                // Keeping a divergent local choice does not create a false common ancestor.
                // Otherwise a later source-only edit could silently replace the changes just retained.
                var acknowledge=preview.Items.Where(i=>resolutions[i.Key]==ExchangeResolution.UseIncoming ||
                    resolutions[i.Key]==ExchangeResolution.KeepLocal && locals.TryGetValue(i.Key,out var old) &&
                    ExchangeEntityFingerprint(old,local)==ExchangeEntityFingerprint(incoming[i.Key],package.Tables)).ToArray();
                var added=changed.Count(k=>!locals.ContainsKey(k) && incoming[k].Root.String("deleted_utc") is null);
                var deleted=changed.Count(k=>incoming[k].Root.String("deleted_utc") is not null);
                var updated=changed.Count-added-deleted;
                var kept=resolutions.Count(p=>p.Value==ExchangeResolution.KeepLocal || p.Value==ExchangeResolution.UseIncoming && !changed.Contains(p.Key));
                var skipped=resolutions.Count(p=>p.Value==ExchangeResolution.Skip);
                // An equal-only/keep-only import is a true no-op, including the ledger and recovery directory.
                if(changed.Count==0) return new(0,0,0,kept,skipped,0,null,false);

                var backups=Path.Combine(RootDirectory,"BACKUPS");
                Directory.CreateDirectory(backups);
                var backup=Path.Combine(backups,"before-exchange-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".histosets");
                CreateBackup(backup);
                using var transaction=connection.BeginTransaction();
                CheckExchangePreview(connection,transaction,preview);
                package.VerifyFiles();
                var now=StorageFiles.UtcNow();
                var mappings=new Dictionary<(string Table,string Id),string>();
                var selected=ExchangeEntities(package.Tables).Where(e=>changed.Contains(e.Key)).ToArray();
                var imageIds=selected.Where(e=>e.Kind==ExchangeEntityKind.Material).SelectMany(e=>e.Rows["material_images"])
                    .Select(r=>r.String("image_id")!).ToHashSet(StringComparer.Ordinal);
                foreach(var image in package.Tables["images"].Where(r=>imageIds.Contains(r.Id)))
                    PrepareExchangeImage(connection,transaction,package,image,mappings,installed,now);

                foreach(var entity in selected)
                    ApplyExchangeEntity(connection,transaction,entity,mappings,now);
                RequireExchangeActiveReferences(connection,transaction);
                var readyBefore=Convert.ToInt32(Scalar(connection,"SELECT COUNT(*) FROM materials WHERE status='ready' AND deleted_utc IS NULL;",transaction));
                DemoteIncompleteMaterials(connection,transaction,now);
                var demoted=readyBefore-Convert.ToInt32(Scalar(connection,"SELECT COUNT(*) FROM materials WHERE status='ready' AND deleted_utc IS NULL;",transaction));
                var after=ReadExchangeTables(connection,transaction);
                var afterEntities=ExchangeEntities(after).ToDictionary(e=>e.Key,StringComparer.Ordinal);
                foreach(var item in acknowledge)
                {
                    var sourceHash=ExchangeEntityFingerprint(incoming[item.Key],package.Tables);
                    var localHash=ExchangeEntityFingerprint(afterEntities[item.Key],after);
                    if(baselines.TryGetValue(item.Key,out var baseline) && baseline.SourceFingerprint==sourceHash && baseline.LocalFingerprint==localHash) continue;
                    Execute(connection,"""
                        INSERT INTO exchange_baselines(source_catalog_id,entity_kind,entity_id,source_fingerprint,local_fingerprint,source_revision,acknowledged_utc)
                        VALUES($source,$kind,$id,$sourceHash,$localHash,$revision,$now)
                        ON CONFLICT(source_catalog_id,entity_kind,entity_id) DO UPDATE SET
                          source_fingerprint=excluded.source_fingerprint,local_fingerprint=excluded.local_fingerprint,
                          source_revision=excluded.source_revision,acknowledged_utc=excluded.acknowledged_utc;
                        """,transaction,("$source",package.SourceCatalogId.ToString()),("$kind",ExchangeKindKey(item.Kind)),("$id",item.Id.ToString()),
                        ("$sourceHash",sourceHash),("$localHash",localHash),("$revision",incoming[item.Key].Root.Integer("revision")),("$now",now));
                }
                TouchCatalog(connection,transaction,now);
                Validate(connection,transaction:transaction);
                // Every referenced original is verified before commit, including assets shared with unchanged objects.
                VerifyExchangeManagedImages(connection,transaction);
                transaction.Commit();
                committed=true;
                return new(added,updated,deleted,kept,skipped,demoted,backup,true);
            }
            catch(Exception ex) when(!committed && ex is SqliteException or IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException)
            { throw new CatalogStorageException("Не удалось применить обмен. Изменения базы отменены.\n"+ex.Message,ex); }
            finally
            {
                if(!committed) foreach(var path in installed)
                    try { if(File.Exists(path)) File.Delete(path); }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static Dictionary<string,ExchangeResolution> ExchangeResolutions(ExchangePreview preview,IReadOnlyDictionary<string,ExchangeResolution> choices)
    {
        var keys=preview.Items.Select(i=>i.Key).ToHashSet(StringComparer.Ordinal);
        if(choices.Any(p=>!keys.Contains(p.Key) || !Enum.IsDefined(p.Value))) throw new CatalogStorageException("Решения не соответствуют плану обмена.");
        var result=new Dictionary<string,ExchangeResolution>(StringComparer.Ordinal);
        foreach(var item in preview.Items)
        {
            var resolution=choices.GetValueOrDefault(item.Key,item.SuggestedResolution);
            if(resolution==ExchangeResolution.Unresolved) throw new CatalogStorageException("Выберите решение для каждого конфликта, удаления и изменения общего справочника.");
            result.Add(item.Key,resolution);
        }
        return result;
    }

    private void CheckExchangePreview(SqliteConnection connection,SqliteTransaction? transaction,ExchangePreview preview)
    {
        var fresh=BuildExchangePreview(connection,transaction,preview.Package);
        if(fresh.TargetCatalogId!=preview.TargetCatalogId || fresh.TargetContentRevision!=preview.TargetContentRevision || fresh.StateFingerprint!=preview.StateFingerprint)
            throw new CatalogStorageException("Каталог изменился после предварительного просмотра. Обновите план обмена и повторно проверьте решения.");
    }

    private static void ValidateDecodedExchangeImages(ExchangePackage package,Func<string,ImageMetadata> readMetadata)
    {
        foreach(var row in package.Tables["images"])
        {
            var actual=readMetadata(package.ImagePaths[row.String("storage_key")!]);
            static bool Near(double left,double right)=>Math.Abs(left-right)<=Math.Max(Math.Abs(left),Math.Abs(right))*1e-9;
            if(actual.Width!=row.Integer("pixel_width") || actual.Height!=row.Integer("pixel_height") || actual.Orientation!=row.Integer("orientation") || actual.MimeType!=row.String("mime_type")
                || !Near(ImageCoordinates.EffectiveDpi(actual.DpiX),Convert.ToDouble(row.Values["dpi_x"])) || !Near(ImageCoordinates.EffectiveDpi(actual.DpiY),Convert.ToDouble(row.Values["dpi_y"])))
                throw new CatalogStorageException("Параметры оригинала не соответствуют пакету. Обмен остановлен: "+row.Id);
        }
    }

    private void PrepareExchangeImage(SqliteConnection connection,SqliteTransaction transaction,ExchangePackage package,ExchangeRow incoming,
        Dictionary<(string Table,string Id),string> mappings,List<string> installed,string now)
    {
        var hash=incoming.String("sha256")!;
        var existing=Scalar(connection,"SELECT id FROM images WHERE sha256=$hash;",transaction,("$hash",hash)) as string;
        if(existing is not null)
        {
            mappings[("images",incoming.Id)]=existing;
            // An original is immutable. A historical asset can become active again without affecting other links.
            if(incoming.String("deleted_utc") is null)
                Execute(connection,"UPDATE images SET deleted_utc=NULL,modified_utc=$now,revision=revision+1 WHERE id=$id AND deleted_utc IS NOT NULL;",transaction,("$now",now),("$id",existing));
            return;
        }
        var extension=incoming.String("mime_type") switch
        {
            "image/jpeg"=>".jpg","image/png"=>".png","image/tiff"=>".tif","image/bmp"=>".bmp","image/gif"=>".gif",
            _=>throw new CatalogStorageException("Формат изображения пока не поддерживается: "+incoming.String("mime_type"))
        };
        var key="SPECIMENS/"+hash+extension;
        var path=StorageFiles.Resolve(RootDirectory,key);
        if(File.Exists(path))
        {
            if(new FileInfo(path).Length!=incoming.Integer("byte_size") || StorageFiles.Hash(path)!=hash)
                throw new CatalogStorageException("Повреждён файл существующего каталога: "+key);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input=new FileStream(package.ImagePaths[incoming.String("storage_key")!],FileMode.Open,FileAccess.Read,FileShare.Read);
            using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            installed.Add(path);
            CopyVerified(input,output,new(key,hash,incoming.Integer("byte_size")));
        }
        var values=incoming.Values.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
        values["storage_key"]=key;
        WriteExchangeRow(connection,transaction,"images",new(values),mappings,now);
    }

    private static void ApplyExchangeEntity(SqliteConnection connection,SqliteTransaction transaction,ExchangeEntity entity,
        Dictionary<(string Table,string Id),string> mappings,string now)
    {
        var table=ExchangeRootTable(entity.Kind);
        var oldRevision=Scalar(connection,$"SELECT revision FROM {table} WHERE id=$id;",transaction,("$id",entity.Root.Id));
        // Free partial unique slots (primary/calibration) before applying the incoming choice.
        if(entity.Kind==ExchangeEntityKind.Material)
        {
            Execute(connection,"UPDATE material_images SET is_primary=0,revision=revision+1,modified_utc=$now WHERE material_id=$id AND is_primary=1;",transaction,("$id",entity.Root.Id),("$now",now));
            Execute(connection,"""
                UPDATE image_calibrations SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE deleted_utc IS NULL AND material_image_id IN
                (SELECT id FROM material_images WHERE material_id=$id);
                """,transaction,("$now",now),("$id",entity.Root.Id));
        }
        foreach(var rowTable in ExchangeSchema.TableNames.Where(entity.Rows.ContainsKey))
            foreach(var row in entity.Rows[rowTable].OrderBy(r=>r.String("deleted_utc") is null?1:0))
                WriteExchangeRow(connection,transaction,rowTable,row,mappings,now);
        // A whole graph replaces the selected root. Omitted local children become tombstones, never hard deletions.
        var after=ReadExchangeTables(connection,transaction);
        var current=ExchangeEntities(after).Single(e=>e.Key==entity.Key);
        foreach(var (rowTable,rows) in current.Rows.Where(p=>p.Key!=table))
        {
            var retained=entity.Rows[rowTable].Select(r=>mappings.GetValueOrDefault((rowTable,r.Id),r.Id)).ToHashSet(StringComparer.Ordinal);
            foreach(var row in rows)
                if(row.String("deleted_utc") is null && (entity.Root.String("deleted_utc") is not null || !retained.Contains(row.Id)))
                    Execute(connection,$"UPDATE {rowTable} SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE id=$id;",transaction,("$now",now),("$id",row.Id));
        }
        if(oldRevision is not null)
            Execute(connection,$"UPDATE {table} SET revision=$revision,modified_utc=$now WHERE id=$id;",transaction,
                ("$revision",Convert.ToInt64(oldRevision)+1),("$now",now),("$id",entity.Root.Id));
    }

    private static void WriteExchangeRow(SqliteConnection connection,SqliteTransaction transaction,string table,ExchangeRow incoming,
        Dictionary<(string Table,string Id),string> mappings,string now)
    {
        var values=incoming.Values.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
        string Remap(string target,string id)=>mappings.GetValueOrDefault((target,id),id);
        if(table=="material_images") values["image_id"]=Remap("images",(string)values["image_id"]!);
        if(table is "regions" or "image_calibrations") values["material_image_id"]=Remap("material_images",(string)values["material_image_id"]!);
        // The same locale/link can have different row UUIDs after independent editing. Its unique parent key is authoritative.
        string? natural=null;
        (string Parent,string Value)? naturalKey=table switch
        {
            "material_translations"=>("material_id","locale"), "element_translations"=>("element_id","locale"),
            "tag_group_translations"=>("group_id","locale"), "tag_translations"=>("tag_id","locale"),
            "analog_group_translations"=>("group_id","locale"), "material_images"=>("material_id","image_id"),
            "material_tags"=>("material_id","tag_id"), _=>null
        };
        if(naturalKey is { } pair)
            natural=Scalar(connection,$"SELECT id FROM {table} WHERE {pair.Parent}=$parent AND {pair.Value}=$value;",transaction,
                ("$parent",values[pair.Parent]),("$value",values[pair.Value])) as string;
        // Calibration history can legitimately contain several UUIDs for one image link.
        // The active partial-unique slot was released above; preserve each incoming UUID.
        if(natural is not null && natural!=incoming.Id)
        {
            var other=Scalar(connection,$"SELECT COUNT(*) FROM {table} WHERE id=$id;",transaction,("$id",incoming.Id));
            if(Convert.ToInt64(other)>0) throw new CatalogStorageException("UUID записи и её родительская связь расходятся. Обмен остановлен: "+incoming.Id);
            values["id"]=natural;
        }
        var id=(string)values["id"]!;
        mappings[(table,incoming.Id)]=id;
        ExchangeRow? previous=null;
        using(var command=Command(connection,$"SELECT * FROM {table} WHERE id=$id;",transaction,("$id",id)))
        using(var reader=command.ExecuteReader())
            if(reader.Read())
            {
                var old=new Dictionary<string,object?>(StringComparer.Ordinal);
                for(var i=0;i<reader.FieldCount;i++) old[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
                previous=new(old);
            }
        if(previous is not null)
        {
            string? owner=table switch
            {
                "material_translations" or "material_images" or "elements" or "regions" or "material_tags"=>"material_id",
                "element_translations"=>"element_id", "tag_group_translations" or "analog_group_translations"=>"group_id",
                "tag_translations"=>"tag_id", "image_calibrations"=>"material_image_id", _=>null
            };
            if(owner is not null && !Equals(previous.Values[owner],values[owner]))
                throw new CatalogStorageException("UUID дочерней записи принадлежит другому объекту. Обмен остановлен: "+incoming.Id);
            if(table=="material_images" && !Equals(previous.Values["image_id"],values["image_id"]))
                throw new CatalogStorageException("Существующее изображение объекта нельзя подменять другим оригиналом.");
            var fields=values.Keys.Where(c=>c!="id" && !ExchangeAuditColumn(c)).ToArray();
            if(fields.All(c=>Equals(previous.Values[c],values[c]))) return;
            foreach(var column in new[]{"created_utc","origin_catalog_id","source_batch_id"}) values[column]=previous.Values[column];
            values["revision"]=previous.Integer("revision")+1;
            values["modified_utc"]=now;
        }
        else values["source_batch_id"]=null;
        var columns=values.Keys.ToArray();
        var parameters=columns.Select((c,i)=>("$v"+i,values[c])).ToArray();
        if(previous is null)
            Execute(connection,$"INSERT INTO {table}({string.Join(',',columns)}) VALUES({string.Join(',',columns.Select((_,i)=>"$v"+i))});",transaction,parameters);
        else
            Execute(connection,$"UPDATE {table} SET {string.Join(',',columns.Select((c,i)=>c+"=$v"+i))} WHERE id=$key;",transaction,
                parameters.Append(("$key",(object?)id)).ToArray());
    }

    private static void RequireExchangeActiveReferences(SqliteConnection connection,SqliteTransaction transaction)
    {
        var invalid=Convert.ToInt64(Scalar(connection,"""
            SELECT
              (SELECT COUNT(*) FROM materials m JOIN analog_groups a ON a.id=m.analog_group_id WHERE m.deleted_utc IS NULL AND a.deleted_utc IS NOT NULL) +
              (SELECT COUNT(*) FROM material_tags mt JOIN materials m ON m.id=mt.material_id JOIN tags t ON t.id=mt.tag_id WHERE mt.deleted_utc IS NULL AND m.deleted_utc IS NULL AND t.deleted_utc IS NOT NULL) +
              (SELECT COUNT(*) FROM tags t JOIN tag_groups g ON g.id=t.group_id WHERE t.deleted_utc IS NULL AND g.deleted_utc IS NOT NULL) +
              (SELECT COUNT(*) FROM material_images mi JOIN materials m ON m.id=mi.material_id JOIN images i ON i.id=mi.image_id WHERE mi.deleted_utc IS NULL AND m.deleted_utc IS NULL AND i.deleted_utc IS NOT NULL) +
              (SELECT COUNT(*) FROM regions r JOIN elements e ON e.id=r.element_id JOIN material_images mi ON mi.id=r.material_image_id
               WHERE r.deleted_utc IS NULL AND (e.deleted_utc IS NOT NULL OR mi.deleted_utc IS NOT NULL));
            """,transaction));
        if(invalid!=0) throw new CatalogStorageException("Решения оставляют ссылки на удалённые записи. Сохраните используемые справочники или измените решения по связанным объектам.");
    }

    private void VerifyExchangeManagedImages(SqliteConnection connection,SqliteTransaction transaction)
    {
        using var command=Command(connection,"SELECT storage_key,sha256,byte_size FROM images UNION ALL SELECT source_key,xml_sha256,NULL FROM import_batches;",transaction);
        using var reader=command.ExecuteReader();
        while(reader.Read())
        {
            var path=StorageFiles.Resolve(RootDirectory,reader.GetString(0));
            if(!File.Exists(path) || !reader.IsDBNull(2) && new FileInfo(path).Length!=reader.GetInt64(2) || StorageFiles.Hash(path)!=reader.GetString(1))
                throw new CatalogStorageException("Отсутствует или повреждён файл: "+reader.GetString(0));
        }
    }
}

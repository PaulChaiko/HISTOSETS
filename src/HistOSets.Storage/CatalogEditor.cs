using HistOSets.Core;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

public sealed partial class CatalogStore
{
    public IReadOnlyList<MaterialIndexEntry> LoadMaterialIndex(string locale = "ru")
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var connection = Open(DatabasePath);
            var names = ReadNames(connection, "material_translations", "material_id");
            var analogs = ReadNames(connection, "analog_group_translations", "group_id");
            var taxonomy = LoadTaxonomy();
            var rows = new List<MaterialIndexEntry>();
            using var command = Command(connection, """
                SELECT m.id,m.revision,m.status,m.analog_group_id,
                    (SELECT i.storage_key FROM material_images mi JOIN images i ON i.id=mi.image_id AND i.deleted_utc IS NULL
                     WHERE mi.material_id=m.id AND mi.deleted_utc IS NULL ORDER BY mi.is_primary DESC,mi.sort_order,mi.id LIMIT 1)
                FROM materials m WHERE m.deleted_utc IS NULL ORDER BY m.created_utc,m.id;
                """);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var localized = names.GetValueOrDefault(id) ?? new();
                var name = CatalogBrowser.Label(localized, locale);
                if (string.IsNullOrWhiteSpace(name)) name = locale == "en" ? "Untitled" : "Без названия";
                var texts = ReadEditorTexts(connection, "material_translations", "material_id", id);
                var selected = texts.GetValueOrDefault(locale) ?? texts.GetValueOrDefault("ru") ?? texts.GetValueOrDefault("en") ?? texts.Values.FirstOrDefault();
                var analog = reader.IsDBNull(3) ? null : reader.GetString(3);
                var guid = Guid.Parse(id);
                rows.Add(new(guid, reader.GetInt32(1), reader.GetString(2), name, selected?.Summary ?? "",
                    reader.IsDBNull(4) ? null : StorageFiles.Resolve(RootDirectory, reader.GetString(4)),
                    analog is null ? null : Guid.Parse(analog), localized,
                    analog is null ? new Dictionary<string,string>() : analogs.GetValueOrDefault(analog) ?? new(),
                    taxonomy.MaterialTags.GetValueOrDefault(guid) ?? new HashSet<Guid>()));
            }
            return rows;
        }
    }

    public MaterialDraft LoadMaterialDraft(Guid materialId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var connection = Open(DatabasePath);
            return ReadDraft(connection, materialId);
        }
    }

    private MaterialDraft ReadDraft(SqliteConnection connection, Guid materialId, SqliteTransaction? transaction = null)
    {
        var key = materialId.ToString();
        MaterialDraft result;
        using (var command = Command(connection, "SELECT revision,kind,status,author_credit,rights_credit,analog_group_id FROM materials WHERE id=$id AND deleted_utc IS NULL;", transaction, ("$id", key)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new CatalogStorageException("Объект не найден или удалён.");
            result = new() { Id = materialId, Revision = reader.GetInt32(0), Kind = reader.GetString(1), Status = reader.GetString(2),
                AuthorCredit = reader.GetString(3), RightsCredit = reader.GetString(4), AnalogGroupId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)) };
        }
        result.Texts = ReadEditorTexts(connection, "material_translations", "material_id", key, transaction);
        result.TrainingPrompts = ReadEditorPrompts(connection, materialId, null, transaction);
        using (var command = Command(connection, """
            SELECT mi.id,i.id,i.storage_key,i.pixel_width,i.pixel_height,i.dpi_x,i.dpi_y,i.mime_type,i.orientation,mi.is_primary
            FROM material_images mi JOIN images i ON i.id=mi.image_id AND i.deleted_utc IS NULL
            WHERE mi.material_id=$id AND mi.deleted_utc IS NULL ORDER BY mi.sort_order,mi.id;
            """, transaction, ("$id", key)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) result.Images.Add(new()
            {
                Id = Guid.Parse(reader.GetString(0)), ImageId = Guid.Parse(reader.GetString(1)), ManagedPath = StorageFiles.Resolve(RootDirectory,reader.GetString(2)),
                Metadata = new(reader.GetInt32(3),reader.GetInt32(4),reader.GetDouble(5),reader.GetDouble(6),reader.GetString(7),reader.GetInt32(8)), IsPrimary = reader.GetBoolean(9),
                Calibration = ReadCalibration(connection, Guid.Parse(reader.GetString(0)), transaction)
            });
        using (var command = Command(connection, "SELECT id FROM elements WHERE material_id=$id AND deleted_utc IS NULL ORDER BY sort_order,id;", transaction, ("$id", key)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var elementId = Guid.Parse(reader.GetString(0));
                var element = new MaterialElementDraft { Id = elementId, Texts = ReadEditorTexts(connection,"element_translations","element_id",reader.GetString(0),transaction),
                    TrainingPrompts = ReadEditorPrompts(connection, materialId, elementId, transaction) };
                using var regions = Command(connection, "SELECT id,material_image_id,geometry_json FROM regions WHERE element_id=$id AND deleted_utc IS NULL ORDER BY sort_order,id;", transaction, ("$id",element.Id.ToString()));
                using var regionReader = regions.ExecuteReader();
                while (regionReader.Read()) element.Regions.Add(new() { Id = Guid.Parse(regionReader.GetString(0)), MaterialImageId = Guid.Parse(regionReader.GetString(1)), Geometry = RegionGeometryCodec.Deserialize(regionReader.GetString(2)) });
                result.Elements.Add(element);
            }
        using (var command = Command(connection, "SELECT tag_id FROM material_tags WHERE material_id=$id AND deleted_utc IS NULL;", transaction, ("$id", key)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) result.TagIds.Add(Guid.Parse(reader.GetString(0)));
        return result;
    }

    private static Dictionary<string,MaterialTextDraft> ReadEditorTexts(SqliteConnection connection, string table, string parent, string id, SqliteTransaction? transaction = null)
    {
        var result = new Dictionary<string,MaterialTextDraft>();
        using var command = Command(connection,$"SELECT locale,name,summary,description FROM {table} WHERE {parent}=$id AND deleted_utc IS NULL;",transaction,("$id",id));
        using var reader = command.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = new() { Name = reader.GetString(1),Summary = reader.GetString(2),Description = reader.GetString(3) };
        return result;
    }

    private sealed record EditorAsset(Guid ImageId, string Key, string Hash, long Size, ImageMetadata Metadata, string? StagedPath);

    public MaterialDraft SaveMaterialDraft(MaterialDraft input, Func<string,ImageMetadata> readMetadata)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(readMetadata);
        var draft = input.Clone();
        ValidateDraft(draft);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var stage = Path.Combine(RootDirectory,".editor-" + Guid.NewGuid().ToString("N"));
            var installed = new List<string>();
            var committed = false;
            try
            {
                using var connection = Open(DatabasePath,SqliteOpenMode.ReadWrite);
                Validate(connection);
                CheckExpectedRevision(connection,null,draft.Id,draft.Revision);
                var assets = new Dictionary<Guid,EditorAsset>();
                foreach (var image in draft.Images)
                {
                    EditorAsset asset;
                    if (!string.IsNullOrWhiteSpace(image.SourcePath))
                    {
                        if (image.SourceSha256 is null || image.SourceSha256.Length != 64 || !image.SourceSha256.All(Uri.IsHexDigit))
                            throw new CatalogStorageException("Повторно добавьте изображение: отсутствует контрольная сумма исходного файла.");
                        Directory.CreateDirectory(stage);
                        var staged = Path.Combine(stage,image.Id.ToString("N"));
                        using (var source = new FileStream(image.SourcePath,FileMode.Open,FileAccess.Read,FileShare.Read))
                        using (var destination = new FileStream(staged,FileMode.CreateNew,FileAccess.Write,FileShare.None)) source.CopyTo(destination);
                        var hash = StorageFiles.Hash(staged);
                        if (!string.Equals(hash,image.SourceSha256,StringComparison.OrdinalIgnoreCase))
                            throw new CatalogStorageException("Исходное изображение изменилось после добавления. Добавьте его заново и проверьте области.");
                        var metadata = readMetadata(staged);
                        if (metadata.Width <= 0 || metadata.Height <= 0 || metadata.Orientation != 1)
                            throw new CatalogStorageException("Нужен оригинал изображения с нормальной ориентацией и положительными размерами.");
                        metadata = metadata with { DpiX = ImageCoordinates.EffectiveDpi(metadata.DpiX), DpiY = ImageCoordinates.EffectiveDpi(metadata.DpiY) };
                        var extension = metadata.MimeType switch
                        {
                            "image/jpeg" => ".jpg", "image/png" => ".png", "image/tiff" => ".tif", "image/bmp" => ".bmp", "image/gif" => ".gif",
                            _ => throw new CatalogStorageException("Формат изображения пока не поддерживается: " + metadata.MimeType)
                        };
                        var existing = Scalar(connection,"SELECT id FROM images WHERE sha256=$hash;",null,("$hash",hash)) as string;
                        asset = existing is null
                            ? new(Guid.Parse(StorageFiles.Id("image/"+hash)),"SPECIMENS/"+hash+extension,hash,new FileInfo(staged).Length,metadata,staged)
                            : ReadAsset(connection,Guid.Parse(existing));
                    }
                    else if (image.ImageId is { } imageId) asset = ReadAsset(connection,imageId);
                    else throw new CatalogStorageException("Для изображения не выбран исходный файл.");
                    assets.Add(image.Id,asset);
                }
                if (assets.Values.Select(a=>a.ImageId).Distinct().Count()!=assets.Count)
                    throw new CatalogStorageException("Одно изображение нельзя дважды добавить в один объект.");
                // A removed association retains its UUID and unique (material,image) pair.
                // Re-adding the same original revives that association and remaps new working-copy contours.
                foreach (var image in draft.Images)
                {
                    var asset = assets[image.Id];
                    var historical = Scalar(connection,"SELECT id FROM material_images WHERE material_id=$material AND image_id=$image;",null,
                        ("$material",draft.Id.ToString()),("$image",asset.ImageId.ToString())) as string;
                    if(historical is null || historical==image.Id.ToString()) continue;
                    var previousId=image.Id;
                    image.Id=Guid.Parse(historical);
                    assets.Remove(previousId);
                    assets.Add(image.Id,asset);
                    foreach(var region in draft.Elements.SelectMany(e=>e.Regions).Where(r=>r.MaterialImageId==previousId)) region.MaterialImageId=image.Id;
                }
                foreach (var asset in assets.Values)
                {
                    var destination = StorageFiles.Resolve(RootDirectory,asset.Key);
                    if (asset.StagedPath is not null && !File.Exists(destination))
                    {
                        Install(asset.StagedPath,asset.Key,asset.Hash);
                        installed.Add(destination);
                    }
                    if (!File.Exists(destination) || new FileInfo(destination).Length!=asset.Size || StorageFiles.Hash(destination)!=asset.Hash)
                        throw new CatalogStorageException("Изображение отсутствует или изменено: " + asset.Key);
                }
                using (var transaction = connection.BeginTransaction())
                {
                    CheckExpectedRevision(connection,transaction,draft.Id,draft.Revision);
                    var now = StorageFiles.UtcNow();
                    var origin = (string)Scalar(connection,"SELECT catalog_id FROM catalog_metadata;",transaction)!;
                    var changesBefore = Convert.ToInt64(Scalar(connection,"SELECT total_changes();",transaction));
                    if (draft.AnalogGroupId is { } analog) RequireActive(connection,transaction,"analog_groups",analog);
                    foreach(var tag in draft.TagIds) RequireActive(connection,transaction,"tags",tag);
                    SaveEditorRow(connection,transaction,"materials",draft.Id,now,origin,
                        [("kind",draft.Kind),("status",draft.Status),("author_credit",draft.AuthorCredit),("rights_credit",draft.RightsCredit),("analog_group_id",draft.AnalogGroupId?.ToString())],touch:false);
                    SaveEditorTexts(connection,transaction,"material_translations","material_id",draft.Id,draft.Texts,now,origin);
                    SaveEditorPrompts(connection,transaction,draft.Id,null,draft.TrainingPrompts,now,origin);
                    var primary = draft.Images.SingleOrDefault(i=>i.IsPrimary)?.Id.ToString();
                    var previousPrimary = Scalar(connection,"SELECT id FROM material_images WHERE material_id=$id AND is_primary=1 AND deleted_utc IS NULL;",transaction,("$id",draft.Id.ToString())) as string;
                    if (previousPrimary is not null && previousPrimary!=primary)
                        Execute(connection,"UPDATE material_images SET is_primary=0,revision=revision+1,modified_utc=$now WHERE id=$id;",transaction,("$id",previousPrimary),("$now",now));
                    for(var i=0;i<draft.Images.Count;i++)
                    {
                        var image = draft.Images[i]; var asset = assets[image.Id];
                        var oldImage = Scalar(connection,"SELECT image_id FROM material_images WHERE id=$id;",transaction,("$id",image.Id.ToString())) as string;
                        if (oldImage is not null && oldImage!=asset.ImageId.ToString())
                            throw new CatalogStorageException("Для замены снимка добавьте новое изображение. Существующие области нельзя переносить на другой оригинал автоматически.");
                        SaveEditorRow(connection,transaction,"images",asset.ImageId,now,origin,
                            [("storage_key",asset.Key),("sha256",asset.Hash),("byte_size",asset.Size),("pixel_width",asset.Metadata.Width),("pixel_height",asset.Metadata.Height),
                            ("dpi_x",asset.Metadata.DpiX),("dpi_y",asset.Metadata.DpiY),("mime_type",asset.Metadata.MimeType),("orientation",asset.Metadata.Orientation)]);
                        RequireOwner(connection,transaction,"material_images",image.Id,"material_id",draft.Id);
                        SaveEditorRow(connection,transaction,"material_images",image.Id,now,origin,
                            [("material_id",draft.Id.ToString()),("image_id",asset.ImageId.ToString()),("sort_order",i),("is_primary",image.IsPrimary?1:0)]);
                        SaveCalibration(connection,transaction,image,asset.Metadata,now,origin);
                    }
                    for(var e=0;e<draft.Elements.Count;e++)
                    {
                        var element = draft.Elements[e];
                        RequireOwner(connection,transaction,"elements",element.Id,"material_id",draft.Id);
                        SaveEditorRow(connection,transaction,"elements",element.Id,now,origin,[("material_id",draft.Id.ToString()),("sort_order",e)]);
                        SaveEditorTexts(connection,transaction,"element_translations","element_id",element.Id,element.Texts,now,origin);
                        SaveEditorPrompts(connection,transaction,draft.Id,element.Id,element.TrainingPrompts,now,origin);
                        for(var r=0;r<element.Regions.Count;r++)
                        {
                            var region=element.Regions[r];
                            RequireOwner(connection,transaction,"regions",region.Id,"material_id",draft.Id);
                            SaveEditorRow(connection,transaction,"regions",region.Id,now,origin,
                                [("material_id",draft.Id.ToString()),("material_image_id",region.MaterialImageId.ToString()),("element_id",element.Id.ToString()),
                                 ("geometry_type",region.Geometry.Kind),("coordinate_space","pixels"),("geometry_json",RegionGeometryCodec.Serialize(region.Geometry)),("sort_order",r)]);
                        }
                    }
                    RemoveMissingRows(connection,transaction,"regions","material_id",draft.Id,draft.Elements.SelectMany(e=>e.Regions).Select(r=>r.Id).ToHashSet(),now);
                    RemoveMissingRows(connection,transaction,"elements","material_id",draft.Id,draft.Elements.Select(e=>e.Id).ToHashSet(),now);
                    RemoveMissingRows(connection,transaction,"training_prompts","material_id",draft.Id,
                        draft.TrainingPrompts.Concat(draft.Elements.SelectMany(e=>e.TrainingPrompts)).Select(p=>p.Id).ToHashSet(),now);
                    Execute(connection,"""
                        UPDATE element_translations SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE deleted_utc IS NULL AND element_id IN
                        (SELECT id FROM elements WHERE material_id=$id AND deleted_utc IS NOT NULL);
                        UPDATE training_prompt_translations SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE deleted_utc IS NULL AND prompt_id IN
                        (SELECT id FROM training_prompts WHERE material_id=$id AND deleted_utc IS NOT NULL);
                        """,transaction,("$now",now),("$id",draft.Id.ToString()));
                    RemoveMissingRows(connection,transaction,"material_images","material_id",draft.Id,draft.Images.Select(i=>i.Id).ToHashSet(),now);
                    Execute(connection,"""
                        UPDATE image_calibrations SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE deleted_utc IS NULL AND material_image_id IN
                        (SELECT id FROM material_images WHERE material_id=$id AND deleted_utc IS NOT NULL);
                        """,transaction,("$now",now),("$id",draft.Id.ToString()));
                    SaveEditorTags(connection,transaction,draft,now,origin);
                    if (draft.Status=="ready")
                    {
                        var issues=ReadinessIssues(connection,draft.Id.ToString(),transaction);
                        AddFileIssues(connection,transaction,draft.Id,issues);
                        if(issues.Count>0) throw new CatalogStorageException("Объект пока не готов:\n"+string.Join("\n",issues));
                    }
                    if(Convert.ToInt64(Scalar(connection,"SELECT total_changes();",transaction))!=changesBefore)
                    {
                        if(draft.Revision>0) Touch(connection,transaction,"materials",draft.Id,now);
                        TouchCatalog(connection,transaction,now);
                    }
                    Validate(connection,transaction:transaction);
                    var saved = ReadDraft(connection,draft.Id,transaction);
                    transaction.Commit();
                    committed=true;
                    return saved;
                }
            }
            catch(Exception ex) when(!committed && ex is SqliteException or IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException)
            { throw new CatalogStorageException("Не удалось сохранить объект. Изменения базы отменены.\n"+ex.Message,ex); }
            finally
            {
                // Cleanup cannot turn a committed save into a reported rollback or hide the original error.
                try { if(Directory.Exists(stage)) Directory.Delete(stage,true); }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
                if(!committed) foreach(var path in installed)
                    try { if(File.Exists(path)) File.Delete(path); }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void ValidateDraft(MaterialDraft draft)
    {
        if(draft.Id==Guid.Empty || draft.Revision<0 || draft.Status is not ("draft" or "ready")) throw new CatalogStorageException("Некорректные сведения объекта.");
        static void Unique(IEnumerable<Guid> ids)
        {
            var list=ids.ToList();
            if(list.Any(id=>id==Guid.Empty) || list.Distinct().Count()!=list.Count) throw new CatalogStorageException("Идентификаторы записей редактора должны быть непустыми и уникальными.");
        }
        Unique(draft.Images.Select(i=>i.Id)); Unique(draft.Elements.Select(e=>e.Id)); Unique(draft.Elements.SelectMany(e=>e.Regions).Select(r=>r.Id));
        var prompts = draft.TrainingPrompts.Concat(draft.Elements.SelectMany(e=>e.TrainingPrompts)).ToList();
        Unique(prompts.Select(p=>p.Id));
        foreach(var prompt in prompts)
            foreach(var (locale,text) in prompt.Texts)
                if(string.IsNullOrWhiteSpace(locale) || text is null)
                    throw new CatalogStorageException("Некорректная языковая запись учебного описания.");
        if(draft.Images.Count(i=>i.IsPrimary)>1) throw new CatalogStorageException("У объекта может быть только одно основное изображение.");
        foreach(var image in draft.Images)
            if(image.Calibration is { } calibration)
            {
                try { calibration.Validate(); }
                catch(ArgumentException ex) { throw new CatalogStorageException("Некорректная физическая калибровка изображения.",ex); }
            }
        var images=draft.Images.Select(i=>i.Id).ToHashSet();
        foreach(var region in draft.Elements.SelectMany(e=>e.Regions))
        {
            if(!images.Contains(region.MaterialImageId)) throw new CatalogStorageException("Область ссылается на отсутствующее изображение. Удалите её явно или сохраните изображение.");
            try { RegionGeometryCodec.Validate(region.Geometry); }
            catch(ArgumentException ex) { throw new CatalogStorageException("Некорректная геометрия области.",ex); }
        }
        foreach(var texts in draft.Elements.Select(e=>e.Texts).Prepend(draft.Texts))
            foreach(var (locale,text) in texts)
                if(string.IsNullOrWhiteSpace(locale) || text is null || text.Name is null || text.Summary is null || text.Description is null)
                    throw new CatalogStorageException("Некорректная языковая запись.");
    }

    private EditorAsset ReadAsset(SqliteConnection connection,Guid id)
    {
        using var command=Command(connection,"SELECT storage_key,sha256,byte_size,pixel_width,pixel_height,dpi_x,dpi_y,mime_type,orientation FROM images WHERE id=$id AND deleted_utc IS NULL;",null,("$id",id.ToString()));
        using var reader=command.ExecuteReader();
        if(!reader.Read()) throw new CatalogStorageException("Изображение не найдено или удалено.");
        return new(id,reader.GetString(0),reader.GetString(1),reader.GetInt64(2),new(reader.GetInt32(3),reader.GetInt32(4),reader.GetDouble(5),reader.GetDouble(6),reader.GetString(7),reader.GetInt32(8)),null);
    }

    private static void CheckExpectedRevision(SqliteConnection connection,SqliteTransaction? transaction,Guid id,int expected)
    {
        using var command=Command(connection,"SELECT revision,deleted_utc FROM materials WHERE id=$id;",transaction,("$id",id.ToString()));
        using var reader=command.ExecuteReader();
        if(!reader.Read()) { if(expected==0) return; }
        else if(expected>0 && reader.IsDBNull(1) && reader.GetInt32(0)==expected) return;
        throw new CatalogStorageException("Объект изменён или удалён после открытия редактора. Откройте его заново; ваши правки не перезаписали новые данные.");
    }

    private static void RequireOwner(SqliteConnection connection,SqliteTransaction transaction,string table,Guid id,string parent,Guid owner)
    {
        var existing=Scalar(connection,$"SELECT {parent} FROM {table} WHERE id=$id;",transaction,("$id",id.ToString())) as string;
        if(existing is not null && existing!=owner.ToString()) throw new CatalogStorageException("Запись редактора принадлежит другому объекту.");
    }

    private static void SaveEditorRow(SqliteConnection connection,SqliteTransaction transaction,string table,Guid id,string now,string origin,
        (string Column,object? Value)[] fields,bool touch=true)
    {
        bool exists,changed;
        using(var command=Command(connection,$"SELECT {string.Join(',',fields.Select(f=>f.Column))},deleted_utc FROM {table} WHERE id=$id;",transaction,("$id",id.ToString())))
        using(var reader=command.ExecuteReader())
        {
            exists=reader.Read(); changed=!exists;
            if(exists)
            {
                changed=!reader.IsDBNull(fields.Length);
                for(var i=0;i<fields.Length;i++)
                {
                    var actual=reader.IsDBNull(i)?null:reader.GetValue(i);
                    var expected=fields[i].Value;
                    if(expected is int n) expected=(long)n;
                    if(!Equals(actual,expected)) changed=true;
                }
            }
        }
        if(!changed) return;
        var parameters=fields.Select((f,i)=>("$v"+i,f.Value)).Concat(new (string,object?)[]{("$id",id.ToString()),("$now",now),("$origin",origin)}).ToArray();
        if(!exists)
            Execute(connection,$"INSERT INTO {table}(id,{string.Join(',',fields.Select(f=>f.Column))},created_utc,modified_utc,origin_catalog_id) VALUES($id,{string.Join(',',fields.Select((_,i)=>"$v"+i))},$now,$now,$origin);",transaction,parameters);
        else
            Execute(connection,$"UPDATE {table} SET {string.Join(',',fields.Select((f,i)=>f.Column+"=$v"+i))},deleted_utc=NULL{(touch?",modified_utc=$now,revision=revision+1":"")} WHERE id=$id;",transaction,parameters);
    }

    private static void SaveEditorTexts(SqliteConnection connection,SqliteTransaction transaction,string table,string parent,Guid id,Dictionary<string,MaterialTextDraft> texts,string now,string origin)
    {
        var retained=new HashSet<Guid>();
        foreach(var (locale,text) in texts)
        {
            var old=Scalar(connection,$"SELECT id FROM {table} WHERE {parent}=$parent AND locale=$locale;",transaction,("$parent",id.ToString()),("$locale",locale)) as string;
            var key=old is null?Guid.NewGuid():Guid.Parse(old); retained.Add(key);
            var fields=new List<(string,object?)>{(parent,id.ToString()),("locale",locale),("name",text.Name),("summary",text.Summary),("description",text.Description)};
            if(table=="material_translations" && old is null) fields.Add(("sort_order",Convert.ToInt64(Scalar(connection,"SELECT COALESCE(MAX(sort_order),-1)+1 FROM material_translations;",transaction))));
            SaveEditorRow(connection,transaction,table,key,now,origin,fields.ToArray());
        }
        RemoveMissingRows(connection,transaction,table,parent,id,retained,now);
    }

    private static void RemoveMissingRows(SqliteConnection connection,SqliteTransaction transaction,string table,string parent,Guid id,HashSet<Guid> retained,string now)
    {
        var existing=new List<Guid>();
        using(var command=Command(connection,$"SELECT id FROM {table} WHERE {parent}=$id AND deleted_utc IS NULL;",transaction,("$id",id.ToString())))
        using(var reader=command.ExecuteReader()) while(reader.Read()) existing.Add(Guid.Parse(reader.GetString(0)));
        foreach(var missing in existing.Where(i=>!retained.Contains(i)))
            Execute(connection,$"UPDATE {table} SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE id=$id;",transaction,("$id",missing.ToString()),("$now",now));
    }

    private static List<TrainingPromptDraft> ReadEditorPrompts(SqliteConnection connection, Guid materialId,
        Guid? elementId, SqliteTransaction? transaction = null)
    {
        var result = new List<TrainingPromptDraft>();
        using var command = Command(connection,"""
            SELECT id,is_enabled FROM training_prompts
            WHERE material_id=$material AND element_id IS $element AND deleted_utc IS NULL ORDER BY sort_order,id;
            """,transaction,("$material",materialId.ToString()),("$element",elementId?.ToString()));
        using var reader = command.ExecuteReader();
        while(reader.Read())
        {
            var prompt = new TrainingPromptDraft { Id = Guid.Parse(reader.GetString(0)), IsEnabled = reader.GetBoolean(1), Texts = new() };
            using var textCommand = Command(connection,"SELECT locale,text FROM training_prompt_translations WHERE prompt_id=$id AND deleted_utc IS NULL;",
                transaction,("$id",prompt.Id.ToString()));
            using var textReader = textCommand.ExecuteReader();
            while(textReader.Read()) prompt.Texts.Add(textReader.GetString(0),textReader.GetString(1));
            result.Add(prompt);
        }
        return result;
    }

    private static void SaveEditorPrompts(SqliteConnection connection,SqliteTransaction transaction,Guid materialId,
        Guid? elementId,IReadOnlyList<TrainingPromptDraft> prompts,string now,string origin)
    {
        for(var i=0;i<prompts.Count;i++)
        {
            var prompt = prompts[i];
            using(var command = Command(connection,"SELECT material_id,element_id FROM training_prompts WHERE id=$id;",transaction,("$id",prompt.Id.ToString())))
            using(var reader = command.ExecuteReader())
                if(reader.Read() && (reader.GetString(0)!=materialId.ToString() || (reader.IsDBNull(1)?null:reader.GetString(1))!=elementId?.ToString()))
                    throw new CatalogStorageException("Учебное описание принадлежит другому объекту или элементу.");
            SaveEditorRow(connection,transaction,"training_prompts",prompt.Id,now,origin,
                [("material_id",materialId.ToString()),("element_id",elementId?.ToString()),("sort_order",i),("is_enabled",prompt.IsEnabled?1:0)]);
            var retained = new HashSet<Guid>();
            foreach(var (locale,text) in prompt.Texts)
            {
                var old = Scalar(connection,"SELECT id FROM training_prompt_translations WHERE prompt_id=$id AND locale=$locale;",
                    transaction,("$id",prompt.Id.ToString()),("$locale",locale)) as string;
                var id = old is null?Guid.NewGuid():Guid.Parse(old);
                retained.Add(id);
                SaveEditorRow(connection,transaction,"training_prompt_translations",id,now,origin,
                    [("prompt_id",prompt.Id.ToString()),("locale",locale),("text",text)]);
            }
            RemoveMissingRows(connection,transaction,"training_prompt_translations","prompt_id",prompt.Id,retained,now);
        }
    }

    private static void SaveEditorTags(SqliteConnection connection,SqliteTransaction transaction,MaterialDraft draft,string now,string origin)
    {
        var retained=new HashSet<Guid>();
        foreach(var tag in draft.TagIds)
        {
            var existing=Scalar(connection,"SELECT id FROM material_tags WHERE material_id=$id AND tag_id=$tag;",transaction,("$id",draft.Id.ToString()),("$tag",tag.ToString())) as string;
            var id=existing is null?Guid.NewGuid():Guid.Parse(existing); retained.Add(id);
            SaveEditorRow(connection,transaction,"material_tags",id,now,origin,[("material_id",draft.Id.ToString()),("tag_id",tag.ToString())]);
        }
        RemoveMissingRows(connection,transaction,"material_tags","material_id",draft.Id,retained,now);
    }

    public void DeleteMaterial(Guid materialId,int expectedRevision)
    {
        WriteTags((connection,transaction,now,_)=>
        {
            CheckExpectedRevision(connection,transaction,materialId,expectedRevision);
            if(expectedRevision<=0) throw new CatalogStorageException("Нельзя удалить несохранённый объект.");
            Execute(connection,"""
                UPDATE element_translations SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE deleted_utc IS NULL AND element_id IN(SELECT id FROM elements WHERE material_id=$id);
                UPDATE image_calibrations SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE deleted_utc IS NULL AND material_image_id IN(SELECT id FROM material_images WHERE material_id=$id);
                UPDATE training_prompt_translations SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE deleted_utc IS NULL AND prompt_id IN(SELECT id FROM training_prompts WHERE material_id=$id);
                """,transaction,("$id",materialId.ToString()),("$now",now));
            foreach(var table in new[]{"material_translations","material_tags","regions","elements","material_images","training_prompts"})
                Execute(connection,$"UPDATE {table} SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE material_id=$id AND deleted_utc IS NULL;",transaction,("$id",materialId.ToString()),("$now",now));
            Execute(connection,"UPDATE materials SET deleted_utc=$now,modified_utc=$now,revision=revision+1 WHERE id=$id;",transaction,("$id",materialId.ToString()),("$now",now));
            return true;
        });
    }
}

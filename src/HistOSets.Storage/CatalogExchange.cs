using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HistOSets.Core;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

public sealed partial class CatalogStore
{
    private sealed record ExchangeEntity(ExchangeEntityKind Kind, ExchangeRow Root,
        IReadOnlyDictionary<string,IReadOnlyList<ExchangeRow>> Rows)
    {
        internal string Key => ExchangeKindKey(Kind) + ":" + Root.Id;
    }

    private sealed record ExchangeBaseline(string SourceFingerprint, string LocalFingerprint, long SourceRevision);
    private static string ExchangeKindKey(ExchangeEntityKind kind) => kind switch
    {
        ExchangeEntityKind.Material => "material", ExchangeEntityKind.Tag => "tag",
        ExchangeEntityKind.TagGroup => "tag_group", ExchangeEntityKind.AnalogGroup => "analog_group",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static string ExchangeRootTable(ExchangeEntityKind kind) => kind switch
    {
        ExchangeEntityKind.Material => "materials", ExchangeEntityKind.Tag => "tags",
        ExchangeEntityKind.TagGroup => "tag_groups", ExchangeEntityKind.AnalogGroup => "analog_groups",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Reads and verifies both sides without modifying the receiving catalog.</summary>
    public ExchangePreview PreviewExchange(ExchangePackage package,string locale="ru")
    {
        ArgumentNullException.ThrowIfNull(package);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            package.VerifyFiles();
            using var connection = Open(DatabasePath);
            Validate(connection);
            _ = ReferencedFiles(connection,RootDirectory);
            return BuildExchangePreview(connection,null,package,locale);
        }
    }

    private ExchangePreview BuildExchangePreview(SqliteConnection connection, SqliteTransaction? transaction, ExchangePackage package,string locale="ru")
    {
        var local = ReadExchangeTables(connection,transaction);
        ValidateExchangeImageIdentities(local,package.Tables);
        var locals = ExchangeEntities(local).ToDictionary(e=>e.Key,StringComparer.Ordinal);
        var incoming = ExchangeEntities(package.Tables);
        var source = package.SourceCatalogId.ToString();
        var baselines = ReadExchangeBaselines(connection,transaction,source);
        var items = new List<ExchangePreviewItem>();
        var warnings = new List<string>();
        string T(string ru,string en)=>locale=="en"?en:ru;
        foreach (var entity in incoming)
        {
            locals.TryGetValue(entity.Key,out var previous);
            var incomingHash = ExchangeEntityFingerprint(entity,package.Tables);
            var localHash = previous is null ? null : ExchangeEntityFingerprint(previous,local);
            var divergentAncestor = false;
            ExchangeChangeKind status;
            if (localHash == incomingHash || package.FormatVersion == 1 && previous is not null && ExchangeContentsEqual(previous, local, entity, package)) status = ExchangeChangeKind.Unchanged;
            else if (entity.Root.String("deleted_utc") is not null) status = ExchangeChangeKind.Deletion;
            else if (previous is null) status = ExchangeChangeKind.Add;
            else if (!baselines.TryGetValue(entity.Key,out var baseline)) status = ExchangeChangeKind.Conflict;
            else
            {
                var sourceChanged = incomingHash != baseline.SourceFingerprint;
                var localChanged = localHash != baseline.LocalFingerprint;
                // A content change at an old/equal source revision is never a safe fast-forward.
                // Fingerprints still decide equality and divergence; counters only veto a stale update.
                var staleSource = entity.Root.Integer("revision") <= baseline.SourceRevision;
                // A legacy import can preserve local descriptions absent from its source graph.
                // A later format2 package must not treat that divergent state as consent to erase them.
                divergentAncestor = entity.Kind == ExchangeEntityKind.Material && package.FormatVersion >= 2 &&
                    baseline.SourceFingerprint != baseline.LocalFingerprint;
                status = sourceChanged ? (localChanged || staleSource || divergentAncestor ? ExchangeChangeKind.Conflict : ExchangeChangeKind.Update) : ExchangeChangeKind.LocalChanged;
            }
            var affected = entity.Kind == ExchangeEntityKind.Material ? 0 : ExchangeAffectedMaterials(local,entity).Count;
            var warning = "";
            if (divergentAncestor && status == ExchangeChangeKind.Conflict)
                warning = T("После предыдущего обмена содержимое карточки различалось между каталогами. Новый пакет требует явного решения; проверьте сохранённые учебные описания перед заменой.",
                    "The catalogs retained different card contents after the previous exchange. The new package requires an explicit choice; review retained training descriptions before replacing the card.");
            if (entity.Kind != ExchangeEntityKind.Material && status is not (ExchangeChangeKind.Unchanged or ExchangeChangeKind.LocalChanged) && affected > 0)
                warning = T($"Общая запись используется в {affected} объектах. Замена изменит их метаданные; неполные готовые объекты станут черновиками.",
                    $"This shared entry is used by {affected} objects. Replacing it changes their metadata; incomplete ready objects become drafts.");
            if (status == ExchangeChangeKind.Deletion)
                warning += (warning.Length == 0 ? "" : " ") + T("Пакет содержит удаление. Оно применяется только после явного выбора; используемый справочник удалить нельзя.",
                    "The package contains a deletion. It requires an explicit choice; a shared entry still in use cannot be deleted.");
            if (entity.Kind == ExchangeEntityKind.Material && package.FormatVersion == 1)
                warning += (warning.Length == 0 ? "" : " ") + T(
                    "Пакет формата 1 не содержит учебных описаний. Описания сохранятся у остающихся объектов и элементов; удаление владельца удалит и его описания.",
                    "Format 1 packages do not contain training descriptions. Descriptions of retained objects and elements are preserved; deleting an owner also deletes its descriptions.");
            var resolution = status switch
            {
                ExchangeChangeKind.Conflict or ExchangeChangeKind.Deletion => ExchangeResolution.Unresolved,
                ExchangeChangeKind.Add or ExchangeChangeKind.Update => ExchangeResolution.UseIncoming,
                _ => ExchangeResolution.KeepLocal
            };
            // Shared names/types affect cards beyond the selected graph and always need an explicit decision.
            if (entity.Kind != ExchangeEntityKind.Material && warning.Length > 0 && resolution == ExchangeResolution.UseIncoming) resolution = ExchangeResolution.Unresolved;
            var name = ExchangeEntityName(entity,locale);
            items.Add(new(entity.Key,entity.Kind,Guid.Parse(entity.Root.Id),name,status,
                previous is null ? T("Запись отсутствует.","The entry does not exist.") : ExchangeEntitySummary(previous,local,locale),
                ExchangeEntitySummary(entity,package.Tables,locale),warning,resolution,affected));
            if(warning.Length > 0) warnings.Add(name + ": " + warning);
        }
        var id = Guid.Parse((string)Scalar(connection,"SELECT catalog_id FROM catalog_metadata;",transaction)!);
        var revision = Convert.ToInt64(Scalar(connection,"SELECT content_revision FROM catalog_metadata;",transaction));
        return new(package,id,revision,ExchangeStateFingerprint(connection,transaction,local),DatabasePath,
            Array.AsReadOnly(items.ToArray()),Array.AsReadOnly(warnings.ToArray()));
    }

    private static Dictionary<string,ExchangeBaseline> ReadExchangeBaselines(SqliteConnection connection, SqliteTransaction? transaction,string source)
    {
        var result = new Dictionary<string,ExchangeBaseline>(StringComparer.Ordinal);
        using var command = Command(connection,"SELECT entity_kind,entity_id,source_fingerprint,local_fingerprint,source_revision FROM exchange_baselines WHERE source_catalog_id=$source;",transaction,("$source",source));
        using var reader = command.ExecuteReader();
        while(reader.Read()) result.Add(reader.GetString(0)+":"+reader.GetString(1),new(reader.GetString(2),reader.GetString(3),reader.GetInt64(4)));
        return result;
    }

    private static Dictionary<string,IReadOnlyList<ExchangeRow>> ReadExchangeTables(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var result = new Dictionary<string,IReadOnlyList<ExchangeRow>>(StringComparer.Ordinal);
        foreach(var table in ExchangeSchema.TableNames)
        {
            var rows = new List<ExchangeRow>();
            using var command = Command(connection,$"SELECT * FROM {table} ORDER BY id;",transaction);
            using var reader = command.ExecuteReader();
            while(reader.Read())
            {
                var values = new Dictionary<string,object?>(StringComparer.Ordinal);
                for(var i=0;i<reader.FieldCount;i++) values.Add(reader.GetName(i),reader.IsDBNull(i)?null:reader.GetValue(i));
                rows.Add(new(values));
            }
            result.Add(table,rows.AsReadOnly());
        }
        return result;
    }

    private static string ExchangeStateFingerprint(SqliteConnection connection,SqliteTransaction? transaction,IReadOnlyDictionary<string,IReadOnlyList<ExchangeRow>> tables)
    {
        var parts = new List<object>();
        foreach(var table in ExchangeSchema.TableNames)
            foreach(var row in tables[table].OrderBy(r=>r.Id,StringComparer.Ordinal))
                parts.Add(new object[] {table,row.Values.OrderBy(p=>p.Key,StringComparer.Ordinal).ToArray()});
        using(var command=Command(connection,"SELECT * FROM exchange_baselines ORDER BY source_catalog_id,entity_kind,entity_id;",transaction))
        using(var reader=command.ExecuteReader())
            while(reader.Read()) parts.Add(Enumerable.Range(0,reader.FieldCount).Select(reader.GetValue).ToArray());
        return ExchangeHash(parts);
    }

    private static string ExchangeHash(object value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    private static List<ExchangeEntity> ExchangeEntities(IReadOnlyDictionary<string,IReadOnlyList<ExchangeRow>> tables)
    {
        var result = new List<ExchangeEntity>();
        foreach(var kind in new[] {ExchangeEntityKind.TagGroup,ExchangeEntityKind.Tag,ExchangeEntityKind.AnalogGroup,ExchangeEntityKind.Material})
            foreach(var root in tables[ExchangeRootTable(kind)].OrderBy(r=>r.Id,StringComparer.Ordinal))
            {
                var rows = new Dictionary<string,IReadOnlyList<ExchangeRow>>(StringComparer.Ordinal) { [ExchangeRootTable(kind)] = new[]{root} };
                void Add(string table,string parent,IReadOnlySet<string> ids) => rows.Add(table,tables[table].Where(r=>ids.Contains(r.String(parent)!)).ToArray());
                var roots = new HashSet<string>(StringComparer.Ordinal) {root.Id};
                switch(kind)
                {
                    case ExchangeEntityKind.Material:
                        foreach(var table in new[]{"material_translations","material_images","elements","regions","material_tags","training_prompts"}) Add(table,"material_id",roots);
                        Add("element_translations","element_id",rows["elements"].Select(r=>r.Id).ToHashSet(StringComparer.Ordinal));
                        Add("image_calibrations","material_image_id",rows["material_images"].Select(r=>r.Id).ToHashSet(StringComparer.Ordinal));
                        Add("training_prompt_translations","prompt_id",rows["training_prompts"].Select(r=>r.Id).ToHashSet(StringComparer.Ordinal));
                        break;
                    case ExchangeEntityKind.TagGroup: Add("tag_group_translations","group_id",roots); break;
                    case ExchangeEntityKind.Tag: Add("tag_translations","tag_id",roots); break;
                    case ExchangeEntityKind.AnalogGroup: Add("analog_group_translations","group_id",roots); break;
                }
                result.Add(new(kind,root,new ReadOnlyDictionary<string,IReadOnlyList<ExchangeRow>>(rows)));
            }
        return result;
    }

    private static bool ExchangeContentsEqual(ExchangeEntity local, IReadOnlyDictionary<string,IReadOnlyList<ExchangeRow>> localTables,
        ExchangeEntity incoming, ExchangePackage package) =>
        ExchangeEntityFingerprint(local, localTables, package.FormatVersion >= 2) == ExchangeEntityFingerprint(incoming, package.Tables, package.FormatVersion >= 2);

    private static string ExchangeEntityFingerprint(ExchangeEntity entity,IReadOnlyDictionary<string,IReadOnlyList<ExchangeRow>> tables, bool includeTraining = true)
    {
        if(entity.Root.String("deleted_utc") is not null) return ExchangeHash(new[]{entity.Key,"deleted"});
        var images = tables["images"].ToDictionary(r=>r.Id,StringComparer.Ordinal);
        var links = tables["material_images"].ToDictionary(r=>r.Id,r=>images[r.String("image_id")!].String("sha256")!,StringComparer.Ordinal);
        var parts = new List<object>();
        foreach(var (table,rows) in entity.Rows.OrderBy(p=>p.Key,StringComparer.Ordinal))
        {
            // Empty training tables retain update6 baseline hashes. Legacy packages cannot express changes to these fields.
            if (ExchangeSchema.IsTrainingTable(table) && (!includeTraining || rows.All(r => r.String("deleted_utc") is not null))) continue;
            var normalized = new List<string>();
            foreach(var row in rows.Where(r=>r.String("deleted_utc") is null))
            {
                var fields = new SortedDictionary<string,object?>(StringComparer.Ordinal);
                foreach(var (column,original) in row.Values)
                {
                    if(ExchangeAuditColumn(column) || column=="deleted_utc") continue;
                    if(column=="id" && (table.EndsWith("_translations",StringComparison.Ordinal) || table is "material_images" or "material_tags" or "image_calibrations")) continue;
                    if(table=="material_translations" && column=="sort_order") continue; // Global browser order is local presentation.
                    object? value = original;
                    if(column=="image_id") value=images[(string)original!].String("sha256");
                    if(column=="material_image_id") value=links[(string)original!];
                    if(column=="geometry_json") value=RegionGeometryCodec.Serialize(RegionGeometryCodec.Deserialize((string)original!));
                    fields[column]=value;
                }
                normalized.Add(JsonSerializer.Serialize(fields));
            }
            parts.Add(new object[] {table,normalized.Order(StringComparer.Ordinal).ToArray()});
        }
        if(entity.Kind==ExchangeEntityKind.Material)
        {
            var referenced=entity.Rows["material_images"].Where(r=>r.String("deleted_utc") is null).Select(r=>images[r.String("image_id")!]);
            foreach(var image in referenced.OrderBy(r=>r.String("sha256"),StringComparer.Ordinal))
                parts.Add(image.Values.Where(p=>!ExchangeAuditColumn(p.Key) && p.Key is not ("id" or "storage_key" or "deleted_utc")).OrderBy(p=>p.Key,StringComparer.Ordinal).ToArray());
        }
        return ExchangeHash(parts);
    }

    private static bool ExchangeAuditColumn(string column) => column is "revision" or "created_utc" or "modified_utc" or "origin_catalog_id" or "source_batch_id";

    private static string ExchangeEntityName(ExchangeEntity entity,string locale="ru")
    {
        var translations=entity.Rows.Where(p=>p.Key.EndsWith("_translations",StringComparison.Ordinal) && p.Key is not ("element_translations" or "training_prompt_translations")).SelectMany(p=>p.Value)
            .OrderBy(r=>r.String("deleted_utc") is null?0:1).ToArray();
        var name=translations.FirstOrDefault(r=>r.String("locale")==locale)?.String("name") ?? translations.FirstOrDefault(r=>r.String("locale")=="ru")?.String("name")
            ?? translations.FirstOrDefault(r=>r.String("locale")=="en")?.String("name") ?? translations.FirstOrDefault()?.String("name");
        return string.IsNullOrWhiteSpace(name) ? entity.Root.Id : name;
    }

    private static string ExchangeEntitySummary(ExchangeEntity entity,IReadOnlyDictionary<string,IReadOnlyList<ExchangeRow>> tables,string locale)
    {
        string T(string ru,string en)=>locale=="en"?en:ru;
        static string Value(object? value)=>Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture)??"—";
        var root=entity.Root;
        var parts=new List<string>{"UUID: "+root.Id,T("Редакция: ","Revision: ")+root.Integer("revision"),
            root.String("deleted_utc") is null ? T("Действует","Active") : T("Удалено","Deleted")};
        foreach(var (table,rows) in entity.Rows.Where(p=>p.Key.EndsWith("_translations",StringComparison.Ordinal) && p.Key is not ("element_translations" or "training_prompt_translations")))
            foreach(var row in rows.Where(r=>root.String("deleted_utc") is not null || r.String("deleted_utc") is null).OrderBy(r=>r.String("locale"),StringComparer.Ordinal))
            {
                parts.Add((row.String("locale")??"").ToUpperInvariant()+": "+row.String("name"));
                if(table=="material_translations") { parts.Add(row.String("summary")??""); parts.Add(row.String("description")??""); }
            }
        if(entity.Kind==ExchangeEntityKind.Material)
        {
            int Count(string table)=>entity.Rows[table].Count(r=>r.String("deleted_utc") is null);
            parts.Add(T($"Состояние: {root.String("status")}; изображений: {Count("material_images")}; элементов: {Count("elements")}; областей: {Count("regions")}; калибровок: {Count("image_calibrations")}; тегов: {Count("material_tags")}",
                $"Status: {root.String("status")}; images: {Count("material_images")}; elements: {Count("elements")}; regions: {Count("regions")}; calibrations: {Count("image_calibrations")}; tags: {Count("material_tags")}"));
            parts.Add(T("Учебных описаний: ", "Training descriptions: ") + Count("training_prompts"));
            foreach (var prompt in entity.Rows["training_prompts"].Where(r => r.String("deleted_utc") is null).OrderBy(r => r.Integer("sort_order")).ThenBy(r => r.Id, StringComparer.Ordinal))
            {
                parts.Add(T("Учебное описание: ", "Training description: ") + prompt.Id +
                    (prompt.String("element_id") is { } owner ? T(" · элемент ", " · element ") + owner : T(" · объект", " · object")) +
                    (prompt.Integer("is_enabled") == 1 ? T(" · включено", " · enabled") : T(" · выключено", " · disabled")));
                foreach (var translation in entity.Rows["training_prompt_translations"].Where(r => r.String("prompt_id") == prompt.Id && r.String("deleted_utc") is null).OrderBy(r => r.String("locale"), StringComparer.Ordinal))
                    parts.Add((translation.String("locale") ?? "").ToUpperInvariant() + ": " + translation.String("text"));
            }
            parts.Add(T("Автор: ","Author: ")+root.String("author_credit"));
            parts.Add(T("Права: ","Rights: ")+root.String("rights_credit"));
            string Label(string translationTable,string parent,string id)
            {
                var names=tables[translationTable].Where(r=>r.String(parent)==id && r.String("deleted_utc") is null).ToArray();
                return (names.FirstOrDefault(r=>r.String("locale")==locale)??names.FirstOrDefault())?.String("name")??"—";
            }
            var analog=root.String("analog_group_id");
            parts.Add(T("Аналоги: ","Analogs: ")+(analog is null?"—":Label("analog_group_translations","group_id",analog)+" · "+analog));
            foreach(var assignment in entity.Rows["material_tags"].Where(r=>r.String("deleted_utc") is null))
            {
                var tag=assignment.String("tag_id")!;
                parts.Add(T("Тег: ","Tag: ")+Label("tag_translations","tag_id",tag)+" · "+tag);
            }
            foreach(var link in entity.Rows["material_images"].Where(r=>r.String("deleted_utc") is null).OrderBy(r=>r.Integer("sort_order")))
            {
                var image=tables["images"].Single(r=>r.Id==link.String("image_id"));
                parts.Add(T("Изображение: ","Image: ")+link.Id+(link.Integer("is_primary")==1?T(" · основное"," · primary"):""));
                parts.Add($"{image.Integer("pixel_width")} × {image.Integer("pixel_height")} px · {image.String("mime_type")} · SHA-256 {image.String("sha256")}");
                foreach(var calibration in entity.Rows["image_calibrations"].Where(r=>r.String("material_image_id")==link.Id && r.String("deleted_utc") is null))
                    parts.Add(T("Калибровка: ","Calibration: ")+Value(calibration.Values["pixel_size_x_m"])+" × "+Value(calibration.Values["pixel_size_y_m"])+T(" м/пиксель", " m/pixel")+
                        "; "+Value(calibration.Values["input_width"])+" × "+Value(calibration.Values["input_height"])+" "+Value(calibration.Values["input_unit"])+
                        "; "+calibration.String("source_kind")+"; "+calibration.String("source_note"));
            }
            foreach(var element in entity.Rows["elements"].Where(r=>r.String("deleted_utc") is null).OrderBy(r=>r.Integer("sort_order")))
            {
                parts.Add(T("Элемент: ","Element: ")+element.Id);
                foreach(var translation in entity.Rows["element_translations"].Where(r=>r.String("element_id")==element.Id && r.String("deleted_utc") is null).OrderBy(r=>r.String("locale"),StringComparer.Ordinal))
                    parts.Add((translation.String("locale")??"").ToUpperInvariant()+": "+translation.String("name")+"\n"+translation.String("summary")+"\n"+translation.String("description"));
            }
            foreach(var region in entity.Rows["regions"].Where(r=>r.String("deleted_utc") is null))
            {
                var geometry=RegionGeometryCodec.Deserialize(region.String("geometry_json")!);
                var bounds=RegionGeometryOperations.GetBounds(geometry);
                parts.Add(T("Область ","Region ")+region.Id+": "+region.String("geometry_type")+" · "+ExchangeHash(region.String("geometry_json")!)[..12]+
                    T(" · элемент "," · element ")+region.String("element_id")+T(" · изображение "," · image ")+region.String("material_image_id"));
                parts.Add(T("Границы в пикселях: ","Pixel bounds: ")+$"x={Value(bounds.X)}, y={Value(bounds.Y)}, w={Value(bounds.Width)}, h={Value(bounds.Height)}");
            }
        }
        else if(entity.Kind==ExchangeEntityKind.Tag) parts.Add(T("Тип: ","Type: ")+(root.String("group_id")??T("не назначен","unassigned")));
        return string.Join("\n",parts.Where(p=>p.Length>0));
    }

    private static HashSet<string> ExchangeAffectedMaterials(IReadOnlyDictionary<string,IReadOnlyList<ExchangeRow>> tables,ExchangeEntity entity)
    {
        var active=tables["materials"].Where(r=>r.String("deleted_utc") is null).Select(r=>r.Id).ToHashSet(StringComparer.Ordinal);
        if(entity.Kind==ExchangeEntityKind.AnalogGroup)
            return tables["materials"].Where(r=>active.Contains(r.Id) && r.String("analog_group_id")==entity.Root.Id).Select(r=>r.Id).ToHashSet(StringComparer.Ordinal);
        var tags=entity.Kind==ExchangeEntityKind.Tag ? new HashSet<string>{entity.Root.Id}
            : tables["tags"].Where(r=>r.String("group_id")==entity.Root.Id && r.String("deleted_utc") is null).Select(r=>r.Id).ToHashSet(StringComparer.Ordinal);
        return tables["material_tags"].Where(r=>r.String("deleted_utc") is null && active.Contains(r.String("material_id")!) && tags.Contains(r.String("tag_id")!))
            .Select(r=>r.String("material_id")!).ToHashSet(StringComparer.Ordinal);
    }

    private static void ValidateExchangeImageIdentities(IReadOnlyDictionary<string,IReadOnlyList<ExchangeRow>> local,IReadOnlyDictionary<string,IReadOnlyList<ExchangeRow>> incoming)
    {
        var byId=local["images"].ToDictionary(r=>r.Id,StringComparer.Ordinal);
        var byHash=local["images"].ToDictionary(r=>r.String("sha256")!,StringComparer.Ordinal);
        foreach(var image in incoming["images"])
        {
            if(byId.TryGetValue(image.Id,out var sameId) && sameId.String("sha256")!=image.String("sha256"))
                throw new CatalogStorageException("Один UUID изображения обозначает разные оригиналы. Обмен остановлен: "+image.Id);
            if(byHash.TryGetValue(image.String("sha256")!,out var sameHash))
                foreach(var field in new[]{"byte_size","pixel_width","pixel_height","dpi_x","dpi_y","mime_type","orientation"})
                    if(!Equals(sameHash.Values[field],image.Values[field]))
                        throw new CatalogStorageException("Параметры одного оригинала различаются между каталогами. Обмен остановлен: "+image.Id);
        }
    }
}

using System.Text.Json;
using HistOSets.Core;
using HistOSets.Storage;

// Build-only demonstrator. The distributed application opens Demo.histosets directly.
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var destination = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(root, "Demo.histosets"));
if (File.Exists(destination)) throw new IOException("Choose a new demo output file.");
var seed = JsonSerializer.Deserialize<DemoCard[]>(File.ReadAllText(Path.Combine(root, "tools", "HistOSets.Demo", "catalog-seed.json")))
    ?? throw new InvalidDataException("The demo seed is empty.");
var staging = Path.Combine(Path.GetDirectoryName(destination)!, ".demo-" + Guid.NewGuid().ToString("N") + ".histosets");
try
{
    CatalogStore.CreateCatalog(staging, "Демонстрационный атлас HISTOSETS");
    using (var store = new CatalogStore(staging))
        foreach (var item in seed)
        {
            var image = new MaterialImageDraft
            {
                SourcePath = Path.Combine(root, "SPECIMENS", item.File), SourceSha256 = item.Sha256, IsPrimary = true
            };
            var draft = new MaterialDraft { Texts = item.Texts, Images = [image] };
            foreach (var entry in item.Elements)
                draft.Elements.Add(new()
                {
                    Texts = entry.Texts,
                    Regions = entry.Polygons.Select(vertices => new MaterialRegionDraft
                    {
                        MaterialImageId = image.Id,
                        Geometry = RegionGeometry.Polygon(vertices.Select(v => new AtlasPoint(v[0], v[1])))
                    }).ToList()
                });
            _ = store.SaveMaterialDraft(draft, _ => item.Metadata);
        }
    File.Move(staging, destination);
}
finally { File.Delete(staging); }

internal sealed record DemoCard(string File, string Sha256, ImageMetadata Metadata, Dictionary<string, MaterialTextDraft> Texts, DemoElement[] Elements);
internal sealed record DemoElement(Dictionary<string, MaterialTextDraft> Texts, double[][][] Polygons);

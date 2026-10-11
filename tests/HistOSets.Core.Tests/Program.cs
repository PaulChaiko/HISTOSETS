using System.Globalization;
using HistOSets.Core;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    count++;
    Console.WriteLine($"PASS {name}");
}
void Reject<T>(Action action, string name) where T : Exception
{
    try { action(); } catch (T) { Check(true, name); return; }
    throw new Exception($"Expected {typeof(T).Name}: {name}");
}

foreach (var culture in new[] { "ru-RU", "en-US" })
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
    var poly = AtlasLoader.ParsePolygon("  0,0  10.5,0\n10.5,20.25\t0,0 ");
    Check(poly.Points[2] == new AtlasPoint(10.5, 20.25), $"Whitespace and decimal parsing: {culture}");
}
Reject<FormatException>(() => AtlasLoader.ParsePolygon("0,0 1,1"), "Reject incomplete polygon");
Reject<FormatException>(() => AtlasLoader.ParsePolygon("0,0 NaN,1 2,2"), "Reject NaN");
Reject<FormatException>(() => AtlasLoader.ParsePolygon("0,0 1 2,2"), "Reject incomplete coordinate");
Check(ImageCoordinates.ToPixels(new(103, 76), CoordinateSpace.LegacyDip, 2400, 2400) == new AtlasPoint(2575, 1900), "Legacy cornea coordinates include 2400 DPI");
Check(ImageCoordinates.ToPixels(new(5, 7), CoordinateSpace.LegacyDip, 0, double.NaN) == new AtlasPoint(5, 7), "Invalid DPI uses 96");
Check(ImageCoordinates.ToPixels(new(103, 76), CoordinateSpace.Pixels, 2400, 2400) == new AtlasPoint(103, 76), "Pixel coordinates are never scaled by DPI");

// Cards are identified by UUID, including same-name analogs. Display language and
// a primary image are projections; they must never change membership or identity.
var materialA = Guid.NewGuid();
var materialB = Guid.NewGuid();
var groupId = Guid.NewGuid();
var imageA = Guid.NewGuid();
var imageB = Guid.NewGuid();
var analogNames = new Dictionary<string, string> { ["ru"] = "Роговица", ["en"] = "Cornea" };
var first = new AtlasSpecimen("Поле 1", "image-a.png", "", "", CoordinateSpace.Pixels, [])
{
    MaterialId = materialA, ImageId = imageA, Locale = "ru", AnalogGroupId = groupId, AnalogGroupNames = analogNames
};
var primary = first with { ImageId = imageB, ImagePath = "image-b.png", IsPrimaryImage = true };
var english = primary with { Name = "Field 1", Locale = "en" };
var duplicate = primary with { MaterialId = materialB };
var unclassified = first with { MaterialId = Guid.NewGuid(), Name = "Без перевода", Locale = "und", AnalogGroupId = null };
var browserCatalog = new AtlasCatalog([first, primary, english, duplicate, unclassified], []);
var unnamed = first with { Name = "", AnalogGroupId = null };
Check(unnamed.DisplayName == "Объект без названия" && (unnamed with { DisplayLocale = "en" }).DisplayName == "Untitled object"
    && unnamed.Name == "", "Unnamed draft receives a localized presentation label without changing its stored name");
var russianCards = CatalogBrowser.Filter(browserCatalog, "ru", "CORNEA", [], CatalogTaxonomy.Empty);
Check(russianCards.Count == 2 && russianCards.Select(s => s.MaterialId).Distinct().Count() == 2,
    "Analog-name search finds cards in any language without merging duplicate object names");
Check(russianCards[0].ImageId == imageB && russianCards[0].DisplayName == "Роговица — Поле 1",
    "Browser uses primary image and localized analog label");
var englishCards = CatalogBrowser.Filter(browserCatalog, "en", "роговица field", [], CatalogTaxonomy.Empty);
Check(englishCards.Count == 1 && englishCards[0].MaterialId == materialA && englishCards[0].DisplayName == "Cornea — Field 1",
    "Mixed-language group/object tokens find the same UUID and English display");
var fallbackCards = CatalogBrowser.Filter(browserCatalog, "en", "без перевода", [], CatalogTaxonomy.Empty);
Check(fallbackCards.Count == 1 && fallbackCards[0].Locale == "und" && fallbackCards[0].LocaleLabel == "Language unspecified",
    "Legacy draft with unspecified language remains visible with localized fallback label");
var authorType = Guid.NewGuid();
var topicType = Guid.NewGuid();
var authorA = Guid.NewGuid();
var authorB = Guid.NewGuid();
var topic = Guid.NewGuid();
var tags = new CatalogTaxonomy([], [new(authorA, authorType, new Dictionary<string, string>()),
    new(authorB, authorType, new Dictionary<string, string>()), new(topic, topicType, new Dictionary<string, string>())],
    new Dictionary<Guid, IReadOnlySet<Guid>> { [materialA] = new HashSet<Guid> { authorA, topic }, [materialB] = new HashSet<Guid> { authorB } });
var tagged = CatalogBrowser.Filter(browserCatalog, "en", "cornea", [authorA, authorB, topic], tags);
Check(tagged.Count == 1 && tagged[0].MaterialId == materialA, "Tag filtering applies OR within a type and AND between types together with analog search");
Check(CatalogBrowser.Filter(browserCatalog, "en", "", [Guid.NewGuid()], tags).Count == 0, "Unknown selected tag does not silently broaden results");

var originalPoints = new List<AtlasPoint> { new(-5.125, 2.25), new(Math.BitIncrement(1d), -0d), new(100000.123456789, 1e-12), new(-5.125, 2.25) };
var geometryPolygon = RegionGeometry.Polygon(originalPoints);
originalPoints[0] = new(99, 99);
Check(geometryPolygon.Points[0] == new AtlasPoint(-5.125, 2.25), "Geometry defensively copies its ordered vertices");
Reject<NotSupportedException>(() => ((IList<AtlasPoint>)geometryPolygon.Points)[0] = new(9, 9), "Geometry vertices cannot be mutated through IList");
var serializedPolygon = RegionGeometryCodec.Serialize(geometryPolygon);
var restoredPolygon = RegionGeometryCodec.Deserialize(serializedPolygon);
Check(restoredPolygon == geometryPolygon && restoredPolygon.GetHashCode() == geometryPolygon.GetHashCode(), "Polygon JSON round trip has structural value equality");
Check(restoredPolygon.Points.Count == 4 && restoredPolygon.Points[0] == restoredPolygon.Points[3], "Round trip preserves legacy duplicate closing vertex and overshoot");
Check(restoredPolygon.Points.Zip(geometryPolygon.Points).All(pair =>
    BitConverter.DoubleToInt64Bits(pair.First.X) == BitConverter.DoubleToInt64Bits(pair.Second.X)
    && BitConverter.DoubleToInt64Bits(pair.First.Y) == BitConverter.DoubleToInt64Bits(pair.Second.Y)), "JSON preserves exact double bits including adjacent doubles and negative zero");
Check(RegionGeometryCodec.Serialize(restoredPolygon) == serializedPolygon, "JSON canonicalization is stable on repeated save");
var geometryRectangle = RegionGeometry.Rectangle(5.125, 10.5, 100.25, .0625);
var geometryEllipse = RegionGeometry.Ellipse(5.125, 10.5, 100.25, .0625);
Check(RegionGeometryCodec.Deserialize(RegionGeometryCodec.Serialize(geometryRectangle)) == geometryRectangle, "Rectangle exact bounds survive JSON round trip");
var serializedEllipse = RegionGeometryCodec.Serialize(geometryEllipse);
Check(RegionGeometryCodec.Deserialize(serializedEllipse) == geometryEllipse && !serializedEllipse.Contains("points", StringComparison.Ordinal), "Ellipse persists exact parametric bounds without polygon approximation");
Check(geometryRectangle != geometryEllipse, "Same bounds do not make rectangle and ellipse geometrically equal");
var rectangleProjection = RegionGeometryCodec.ToPolygon(geometryRectangle);
Check(rectangleProjection.Points.SequenceEqual(new AtlasPoint[] { new(5.125, 10.5), new(105.375, 10.5), new(105.375, 10.5625), new(5.125, 10.5625) }), "Rectangle compatibility projection has the four exact corners");
var ellipseProjection = RegionGeometryCodec.ToPolygon(geometryEllipse, 32);
Check(ellipseProjection.Points.Count == 32 && ellipseProjection.Points[0] == new AtlasPoint(105.375, 10.53125)
    && RegionGeometryCodec.Serialize(geometryEllipse) == serializedEllipse, "Ellipse preview projection leaves stored geometry unchanged");
Check(RegionGeometryCodec.ToPolygon(geometryPolygon).Points.SequenceEqual(geometryPolygon.Points), "Polygon compatibility projection preserves every original vertex");
Reject<ArgumentException>(() => RegionGeometry.Polygon([new(0, 0), new(0, 0), new(1, 1)]), "Reject polygon with fewer than three distinct vertices");
Reject<ArgumentException>(() => RegionGeometry.Polygon([new(0, 0), new(1, double.NaN), new(2, 2)]), "Reject nonfinite polygon vertex");
Reject<ArgumentException>(() => RegionGeometry.Rectangle(0, 0, 0, 10), "Reject zero-width rectangle");
Reject<ArgumentException>(() => RegionGeometry.Ellipse(0, 0, 10, -1), "Reject negative-height ellipse");
Reject<ArgumentException>(() => RegionGeometry.Rectangle(double.MaxValue, 0, double.MaxValue, 1), "Reject overflowing bounds");
Reject<ArgumentException>(() => RegionGeometry.Rectangle(1e300, 0, 1, 1), "Reject width that cannot be represented at the chosen origin");
Reject<FormatException>(() => RegionGeometryCodec.Deserialize(serializedPolygon.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal)), "Reject unknown geometry version");
Reject<FormatException>(() => RegionGeometryCodec.Deserialize(serializedPolygon.Replace("\"polygon\"", "\"brush\"", StringComparison.Ordinal)), "Reject unsupported shape instead of reinterpreting it");
Reject<FormatException>(() => RegionGeometryCodec.Deserialize(serializedPolygon.Replace("\"pixels\"", "\"legacyDip\"", StringComparison.Ordinal)), "Reject geometry using an unsupported coordinate space");
Reject<FormatException>(() => RegionGeometryCodec.Deserialize(serializedPolygon.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)), "Reject duplicate JSON properties");
Reject<FormatException>(() => RegionGeometryCodec.Deserialize(serializedPolygon.Replace("\"version\":1", "\"version\":1,\"unexpected\":true", StringComparison.Ordinal)), "Reject unknown JSON properties");
Reject<FormatException>(() => RegionGeometryCodec.Deserialize("{\"version\":1,\"kind\":\"polygon\",\"coordinateSpace\":\"pixels\",\"points\":[[0,0,1],[1,0],[1,1]]}"), "Reject malformed point dimensions");
Reject<FormatException>(() => RegionGeometryCodec.Deserialize("{\"version\":1,\"kind\":\"ellipse\",\"coordinateSpace\":\"pixels\",\"x\":0,\"y\":0,\"width\":1e999,\"height\":1}"), "Reject JSON numbers outside finite double range");
Reject<FormatException>(() => RegionGeometryCodec.Deserialize("{\"version\":1,\"kind\":\"ellipse\",\"coordinateSpace\":\"pixels\",\"x\":0,\"y\":0,\"width\":1}"), "Reject incomplete bounds");

var temp = Path.Combine(Path.GetTempPath(), "histosets-tests-" + Guid.NewGuid());
Directory.CreateDirectory(Path.Combine(temp, "ATLAS"));
Directory.CreateDirectory(Path.Combine(temp, "SPECIMENS"));
try
{
    var xml = Path.Combine(temp, "ATLAS", "ATLAS.xml");
    Reject<ArgumentException>(() => AtlasLoader.ResolveImagePath(temp, "../outside.jpg"), "Reject escaping image path");
    File.WriteAllText(xml, "<ATLAS><Specimen NAME='A' IMAGE='a.jpg'><ELEMENT NAME='E'><POLYGON POINTS='invalid'/></ELEMENT></Specimen></ATLAS>");
    var catalog = AtlasLoader.Load(temp);
    Check(catalog.Specimens.Count == 1 && catalog.Warnings.Count == 2, "Keep usable record, report missing image and invalid polygon");
    Check(catalog.Specimens[0].Elements[0].Polygons.Count == 0, "Invalid polygon excluded");
    File.WriteAllText(xml, "<ATLAS><Specimen NAME='same' IMAGE='a.jpg'/><Specimen NAME='same' IMAGE='b.jpg'/></ATLAS>");
    catalog = AtlasLoader.Load(temp);
    Check(catalog.Specimens[0].ImagePath != catalog.Specimens[1].ImagePath, "Equal display names do not merge records");
    File.WriteAllText(xml, "<ATLAS><Specimen");
    Reject<AtlasLoadException>(() => AtlasLoader.Load(temp), "Malformed XML gives catalog error");
    File.WriteAllText(xml, "<!DOCTYPE ATLAS [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><ATLAS>&x;</ATLAS>");
    Reject<AtlasLoadException>(() => AtlasLoader.Load(temp), "External XML entities disabled");
    File.WriteAllText(xml, "<ATLAS><Specimen NAME='A' IMAGE='a.jpg' COORDINATES='unknown'/></ATLAS>");
    Reject<AtlasLoadException>(() => AtlasLoader.Load(temp), "Unknown coordinate system does not silently corrupt geometry");
}
finally { Directory.Delete(temp, true); }

if (args.Length > 0)
{
    var baseline = AtlasLoader.Load(Path.GetFullPath(args[0]));
    Check(baseline.Specimens.Count == 10, "Baseline: 10 localized records");
    Check(baseline.Specimens.Sum(s => s.Elements.Count) == 54, "Baseline: 54 elements");
    Check(baseline.Specimens.SelectMany(s => s.Elements).Sum(e => e.Polygons.Count) == 66, "Baseline: 66 polygons");
    Check(baseline.Warnings.Count == 0, "Baseline XML and file references valid");
}
Console.WriteLine($"{count} checks passed.");

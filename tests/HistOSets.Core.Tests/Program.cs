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

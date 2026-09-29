using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace HistOSets.Core;

public static class AtlasLoader
{
    public static AtlasCatalog Load(string dataDirectory)
        => LoadFile(Path.Combine(dataDirectory, "ATLAS", "ATLAS.xml"), dataDirectory);

    public static AtlasCatalog LoadFile(string xmlPath, string dataDirectory)
    {
        var rootDirectory = Path.GetFullPath(dataDirectory);
        xmlPath = Path.GetFullPath(xmlPath);
        try
        {
            using var reader = XmlReader.Create(xmlPath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16 * 1024 * 1024
            });
            var doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
            if (doc.Root?.Name != "ATLAS")
                throw new AtlasLoadException("В файле ATLAS.xml отсутствует корневой элемент ATLAS.");

            var warnings = new List<string>();
            var specimens = new List<AtlasSpecimen>();
            foreach (var node in doc.Root.Elements("Specimen"))
            {
                var name = Value(node, "NAME");
                var image = Value(node, "IMAGE");
                if (name.Length == 0 || image.Length == 0)
                {
                    warnings.Add($"Строка {Line(node)}: пропущен препарат без NAME или IMAGE.");
                    continue;
                }
                string imagePath;
                try
                {
                    imagePath = ResolveImagePath(rootDirectory, image);
                }
                catch (ArgumentException ex)
                {
                    warnings.Add($"{name}: {ex.Message}");
                    continue;
                }
                if (!File.Exists(imagePath))
                    warnings.Add($"{name}: не найдено изображение «{image}».");

                var coordinateText = Value(node, "COORDINATES");
                var coordinates = coordinateText switch
                {
                    "" or "legacy-dip" => CoordinateSpace.LegacyDip,
                    "pixels" => CoordinateSpace.Pixels,
                    _ => (CoordinateSpace?)null
                };
                if (coordinates is null)
                {
                    warnings.Add($"{name}: неизвестная система координат «{coordinateText}»; препарат пропущен.");
                    continue;
                }

                var elements = new List<AtlasElement>();
                foreach (var child in node.Elements("ELEMENT"))
                {
                    var elementName = Value(child, "NAME");
                    if (elementName.Length == 0)
                    {
                        warnings.Add($"{name}, строка {Line(child)}: пропущен элемент без имени.");
                        continue;
                    }
                    var polygons = new List<AtlasPolygon>();
                    foreach (var polygon in child.Elements("POLYGON"))
                    {
                        try { polygons.Add(ParsePolygon(Value(polygon, "POINTS"))); }
                        catch (FormatException ex)
                        {
                            warnings.Add($"{name} / {elementName}, строка {Line(polygon)}: контур пропущен. {ex.Message}");
                        }
                    }
                    elements.Add(new(elementName, Value(child, "INFO1"), Value(child, "INFO2"), polygons));
                }
                specimens.Add(new(name, imagePath, Value(node, "INFO1"), Value(node, "INFO2"),
                    coordinates.Value, elements));
            }
            if (specimens.Count == 0)
                throw new AtlasLoadException("В ATLAS.xml нет доступных записей препаратов. " + string.Join(" ", warnings.Take(3)));
            return new(specimens, warnings);
        }
        catch (AtlasLoadException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            throw new AtlasLoadException($"Не удалось открыть каталог:\n{xmlPath}\n\n{ex.Message}", ex);
        }
    }

    public static AtlasPolygon ParsePolygon(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var points = new List<AtlasPoint>();
        foreach (var token in tokens)
        {
            var pair = token.Split(',');
            if (pair.Length != 2 || !TryNumber(pair[0], out var x) || !TryNumber(pair[1], out var y))
                throw new FormatException("Координаты должны иметь вид x,y; дробная часть отделяется точкой.");
            points.Add(new(x, y));
        }
        if (points.Distinct().Count() < 3)
            throw new FormatException("Для полигона нужны не менее трёх различных точек.");
        return new(points);
    }

    public static string ResolveImagePath(string dataDirectory, string relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName) || Path.IsPathRooted(relativeName))
            throw new ArgumentException("IMAGE должен содержать относительный путь внутри SPECIMENS.");
        var root = Path.GetFullPath(Path.Combine(dataDirectory, "SPECIMENS")) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativeName.Replace('\\', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root, comparison))
            throw new ArgumentException("Путь изображения выходит за пределы папки SPECIMENS.");
        return fullPath;
    }

    private static bool TryNumber(string value, out double number)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);
    private static string Value(XElement element, string attribute) => ((string?)element.Attribute(attribute) ?? "").Trim();
    private static int Line(XElement element) => ((IXmlLineInfo)element).LineNumber;
}

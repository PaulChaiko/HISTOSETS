using System.Text;
using System.Text.Json;

namespace HistOSets.Core;

/// <summary>
/// Persisted geometry in original image pixels. Bounds are axis-aligned;
/// an ellipse is stored parametrically, never as an approximated polygon.
/// Polygon vertices are copied in their original order, including repeated
/// vertices and coordinates outside the image from legacy annotations.
/// </summary>
public sealed record RegionGeometry
{
    public int Version => 1;
    public string Kind { get; }
    public IReadOnlyList<AtlasPoint> Points { get; }
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    private RegionGeometry(string kind, IEnumerable<AtlasPoint>? points, double x, double y, double width, double height)
    {
        Kind = kind;
        Points = Array.AsReadOnly(points?.ToArray() ?? []);
        X = x;
        Y = y;
        Width = width;
        Height = height;
        RegionGeometryCodec.Validate(this);
    }

    public static RegionGeometry Polygon(IEnumerable<AtlasPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return new("polygon", points, 0, 0, 0, 0);
    }

    public static RegionGeometry Rectangle(double x, double y, double width, double height)
        => new("rectangle", null, x, y, width, height);

    public static RegionGeometry Ellipse(double x, double y, double width, double height)
        => new("ellipse", null, x, y, width, height);

    public bool Equals(RegionGeometry? other) => other is not null && Kind == other.Kind
        && X == other.X && Y == other.Y && Width == other.Width && Height == other.Height
        && Points.SequenceEqual(other.Points);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(X);
        hash.Add(Y);
        hash.Add(Width);
        hash.Add(Height);
        foreach (var point in Points) hash.Add(point);
        return hash.ToHashCode();
    }
}

/// <summary>Strict, versioned JSON format; all coordinates use IEEE 754 doubles.</summary>
public static class RegionGeometryCodec
{
    public static void Validate(RegionGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.Version != 1) throw new ArgumentException("Unsupported region geometry version.", nameof(geometry));
        if (geometry.Kind == "polygon")
        {
            if (geometry.Points.Count < 3 || geometry.Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))
                || geometry.Points.Distinct().Take(3).Count() < 3)
                throw new ArgumentException("A polygon requires at least three distinct finite vertices.", nameof(geometry));
            // Do not simplify, reorder, clamp, or reinterpret legacy polygons.
            // The drawing UI separately validates newly drawn regions.
            return;
        }
        if (geometry.Kind is not ("rectangle" or "ellipse"))
            throw new ArgumentException("Unsupported region geometry kind.", nameof(geometry));
        if (!double.IsFinite(geometry.X) || !double.IsFinite(geometry.Y)
            || !double.IsFinite(geometry.Width) || !double.IsFinite(geometry.Height)
            || geometry.Width <= 0 || geometry.Height <= 0
            || !double.IsFinite(geometry.X + geometry.Width) || !double.IsFinite(geometry.Y + geometry.Height)
            || geometry.X + geometry.Width <= geometry.X || geometry.Y + geometry.Height <= geometry.Y)
            throw new ArgumentException("A rectangle or ellipse requires finite bounds with a positive, representable width and height.", nameof(geometry));
    }

    public static string Serialize(RegionGeometry geometry)
    {
        Validate(geometry);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", geometry.Version);
            writer.WriteString("kind", geometry.Kind);
            writer.WriteString("coordinateSpace", "pixels");
            if (geometry.Kind == "polygon")
            {
                writer.WriteStartArray("points");
                foreach (var point in geometry.Points)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(point.X);
                    writer.WriteNumberValue(point.Y);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNumber("x", geometry.X);
                writer.WriteNumber("y", geometry.Y);
                writer.WriteNumber("width", geometry.Width);
                writer.WriteNumber("height", geometry.Height);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static RegionGeometry Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Region geometry JSON is empty.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Region geometry must be a JSON object.");
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!properties.Add(property.Name)) throw new FormatException("Duplicate region geometry property: " + property.Name);
            if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1)
                throw new FormatException("Unsupported or missing region geometry version.");
            if (!root.TryGetProperty("coordinateSpace", out var space) || space.ValueKind != JsonValueKind.String || space.GetString() != "pixels")
                throw new FormatException("Region geometry coordinates must be original image pixels.");
            if (!root.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String)
                throw new FormatException("Region geometry kind is missing.");
            var kind = kindValue.GetString();
            if (kind == "polygon")
            {
                RequireProperties(properties, ["version", "kind", "coordinateSpace", "points"]);
                var pointsValue = root.GetProperty("points");
                if (pointsValue.ValueKind != JsonValueKind.Array) throw new FormatException("Polygon points must be an array.");
                var points = new List<AtlasPoint>();
                foreach (var point in pointsValue.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2)
                        throw new FormatException("Each polygon vertex must contain exactly two numbers.");
                    points.Add(new(ReadNumber(point[0]), ReadNumber(point[1])));
                }
                return RegionGeometry.Polygon(points);
            }
            if (kind is not ("rectangle" or "ellipse")) throw new FormatException("Unsupported region geometry kind.");
            RequireProperties(properties, ["version", "kind", "coordinateSpace", "x", "y", "width", "height"]);
            var x = ReadNumber(root.GetProperty("x"));
            var y = ReadNumber(root.GetProperty("y"));
            var width = ReadNumber(root.GetProperty("width"));
            var height = ReadNumber(root.GetProperty("height"));
            return kind == "rectangle" ? RegionGeometry.Rectangle(x, y, width, height) : RegionGeometry.Ellipse(x, y, width, height);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or OverflowException)
        {
            throw new FormatException("Invalid region geometry JSON.", ex);
        }
    }

    private static double ReadNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new FormatException("Region coordinates must be finite numbers.");
        return number;
    }

    private static void RequireProperties(HashSet<string> actual, string[] expected)
    {
        if (!actual.SetEquals(expected)) throw new FormatException("Missing or unknown region geometry properties.");
    }

    /// <summary>
    /// Compatibility projection for polygon-only consumers. Never persist this
    /// approximation in place of the original ellipse parameters.
    /// </summary>
    public static AtlasPolygon ToPolygon(RegionGeometry geometry, int ellipseSegments = 96)
    {
        Validate(geometry);
        if (geometry.Kind == "polygon") return new(geometry.Points);
        if (geometry.Kind == "rectangle")
            return new(Array.AsReadOnly(new AtlasPoint[]
            {
                new(geometry.X, geometry.Y), new(geometry.X + geometry.Width, geometry.Y),
                new(geometry.X + geometry.Width, geometry.Y + geometry.Height), new(geometry.X, geometry.Y + geometry.Height)
            }));
        if (ellipseSegments < 8 || ellipseSegments > 65536)
            throw new ArgumentOutOfRangeException(nameof(ellipseSegments), "An ellipse projection needs 8 to 65536 segments.");
        var centerX = geometry.X + geometry.Width / 2;
        var centerY = geometry.Y + geometry.Height / 2;
        var points = Enumerable.Range(0, ellipseSegments).Select(i =>
        {
            var angle = i * (2 * Math.PI / ellipseSegments);
            return new AtlasPoint(centerX + geometry.Width / 2 * Math.Cos(angle), centerY + geometry.Height / 2 * Math.Sin(angle));
        }).ToArray();
        return new(Array.AsReadOnly(points));
    }
}

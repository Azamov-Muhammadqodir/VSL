using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace VSL.Views;

/// <summary>
/// File and folder icons of the Material Icon Theme (MIT, Material Extensions; Assets/MaterialIcons), chosen the
/// way the VS Code theme does: by file name, then by (compound) extension, folders by name, open or closed.
/// </summary>
public static partial class MaterialIcons
{
    const string ResourcePrefix = "MaterialIcons/";

    sealed record Manifest(
        Dictionary<string, string> IconFiles,
        Dictionary<string, string> FileNames,
        Dictionary<string, string> FileExtensions,
        Dictionary<string, string> FolderNames,
        Dictionary<string, string> FolderNamesExpanded,
        string File,
        string Folder,
        string FolderExpanded);

    static readonly Lazy<Manifest> Theme = new(LoadManifest);
    static readonly ConcurrentDictionary<string, IImage?> Images = new();

    /// <summary>The icon for a file, e.g. "csharp" for Program.cs, "git" for .gitignore, "visualstudio" for App.csproj.</summary>
    public static string ForFile(string fileName)
    {
        var theme = Theme.Value;
        var name = Path.GetFileName(fileName).ToLowerInvariant();
        if (theme.FileNames.TryGetValue(name, out var byName))
            return byName;
        // Longest extension first: "app.component.ts" tries "component.ts", then "ts".
        for (var dot = name.IndexOf('.'); dot >= 0; dot = name.IndexOf('.', dot + 1))
            if (dot + 1 < name.Length && theme.FileExtensions.TryGetValue(name[(dot + 1)..], out var byExtension))
                return byExtension;
        return theme.File;
    }

    /// <summary>The icon for a folder by name, e.g. "folder-class" (Models) or "folder-test-open" (tests, expanded).</summary>
    public static string ForFolder(string folderName, bool expanded)
    {
        var theme = Theme.Value;
        var name = folderName.ToLowerInvariant();
        return expanded
            ? theme.FolderNamesExpanded.GetValueOrDefault(name, theme.FolderExpanded)
            : theme.FolderNames.GetValueOrDefault(name, theme.Folder);
    }

    public static bool Exists(string iconName) => Theme.Value.IconFiles.ContainsKey(iconName);

    public static IEnumerable<string> All => Theme.Value.IconFiles.Keys;

    /// <summary>The icon as a vector image (null when the theme does not ship it).</summary>
    public static IImage? Image(string iconName) => Images.GetOrAdd(iconName, name =>
    {
        if (!Theme.Value.IconFiles.TryGetValue(name, out var file))
            return null;
        using var stream = typeof(MaterialIcons).Assembly.GetManifestResourceStream(ResourcePrefix + "icons/" + file);
        return stream is null ? null : SvgDrawing.Load(stream);
    });

    static Manifest LoadManifest()
    {
        using var stream = typeof(MaterialIcons).Assembly.GetManifestResourceStream(ResourcePrefix + "material-icons.json")
            ?? throw new InvalidOperationException("The Material icon manifest is not embedded.");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var shipped = typeof(MaterialIcons).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix + "icons/", StringComparison.Ordinal))
            .Select(n => n[(ResourcePrefix.Length + "icons/".Length)..])
            .ToHashSet(StringComparer.Ordinal);
        var iconFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in root.GetProperty("iconDefinitions").EnumerateObject())
            if (definition.Value.TryGetProperty("iconPath", out var path) && Path.GetFileName(path.GetString()) is { } file && shipped.Contains(file))
                iconFiles[definition.Name] = file;

        Dictionary<string, string> Map(string property)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty(property, out var entries))
                foreach (var entry in entries.EnumerateObject())
                    if (entry.Value.GetString() is { } icon && iconFiles.ContainsKey(icon))
                        map[entry.Name.ToLowerInvariant()] = icon;
            return map;
        }

        return new Manifest(iconFiles, Map("fileNames"), Map("fileExtensions"), Map("folderNames"), Map("folderNamesExpanded"),
            root.GetProperty("file").GetString() ?? "file", root.GetProperty("folder").GetString() ?? "folder",
            root.GetProperty("folderExpanded").GetString() ?? "folder-open");
    }
}

/// <summary>
/// Turns the small SVGs of an icon theme into an Avalonia drawing: paths, circles, ellipses, rectangles and
/// polygons with fills, strokes, opacity, groups and transforms. Gradients become their average color.
/// </summary>
public static partial class SvgDrawing
{
    sealed record Style(IBrush? Fill, IBrush? Stroke, double StrokeWidth, double Opacity, FillRule FillRule);

    public static DrawingImage Load(Stream svg)
    {
        var root = XDocument.Load(svg).Root ?? throw new InvalidDataException("Empty SVG.");
        var box = ViewBox(root);
        var gradients = root.Descendants().Where(e => e.Name.LocalName is "linearGradient" or "radialGradient")
            .ToDictionary(e => (string?)e.Attribute("id") ?? "", GradientColor);
        var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(box) };
        // A transparent frame keeps the drawing's bounds at the view box, so icons line up when scaled.
        drawing.Children.Add(new GeometryDrawing { Geometry = new RectangleGeometry(box), Brush = Brushes.Transparent });
        var style = new Style(Brushes.Black, null, 1, 1, FillRule.NonZero);
        Walk(root, Apply(root, style, gradients), drawing, gradients, box);
        return new DrawingImage(drawing);
    }

    static Rect ViewBox(XElement root)
    {
        var numbers = Numbers((string?)root.Attribute("viewBox") ?? "");
        if (numbers.Count == 4)
            return new Rect(numbers[0], numbers[1], numbers[2], numbers[3]);
        return new Rect(0, 0, Number((string?)root.Attribute("width"), 24), Number((string?)root.Attribute("height"), 24));
    }

    static void Walk(XElement parent, Style style, DrawingGroup target, Dictionary<string, Color> gradients, Rect viewport)
    {
        foreach (var element in parent.Elements())
        {
            var own = Apply(element, style, gradients);
            var transform = Transform((string?)element.Attribute("transform"));
            switch (element.Name.LocalName)
            {
                case "g":
                {
                    var group = new DrawingGroup { Opacity = own.Opacity };
                    if (transform is { } matrix)
                        group.Transform = new MatrixTransform(matrix);
                    Walk(element, own with { Opacity = 1 }, group, gradients, viewport);
                    target.Children.Add(group);
                    break;
                }
                case "svg":
                {
                    // A nested viewport: its view box is fitted into the parent's, centered (xMidYMid meet).
                    var inner = ViewBox(element);
                    var scale = Math.Min(viewport.Width / inner.Width, viewport.Height / inner.Height);
                    var fitted = Matrix.CreateTranslation(-inner.X, -inner.Y) * Matrix.CreateScale(scale, scale)
                        * Matrix.CreateTranslation(viewport.X + (viewport.Width - inner.Width * scale) / 2, viewport.Y + (viewport.Height - inner.Height * scale) / 2);
                    var group = new DrawingGroup { Transform = new MatrixTransform(fitted), Opacity = own.Opacity };
                    Walk(element, own with { Opacity = 1 }, group, gradients, inner);
                    target.Children.Add(group);
                    break;
                }
                case "path" or "circle" or "ellipse" or "rect" or "polygon" or "polyline" or "line":
                    if (Shape(element, own.FillRule) is not { } geometry)
                        break;
                    Drawing shape = new GeometryDrawing
                    {
                        Geometry = geometry,
                        Brush = element.Name.LocalName is "polyline" or "line" ? null : WithOpacity(own.Fill, own.Opacity),
                        Pen = own.Stroke is null ? null : new Pen(WithOpacity(own.Stroke, own.Opacity), own.StrokeWidth,
                            lineCap: Cap((string?)element.Attribute("stroke-linecap")), lineJoin: Join((string?)element.Attribute("stroke-linejoin"))),
                    };
                    if (transform is { } shapeMatrix)
                        shape = new DrawingGroup { Transform = new MatrixTransform(shapeMatrix), Children = { shape } };
                    target.Children.Add(shape);
                    break;
                // defs, gradients, clip paths, masks, style and metadata are not drawn.
            }
        }
    }

    static Geometry? Shape(XElement e, FillRule fillRule)
    {
        double A(string name, double fallback = 0) => Number((string?)e.Attribute(name), fallback);
        try
        {
            switch (e.Name.LocalName)
            {
                case "path" when (string?)e.Attribute("d") is { Length: > 0 } d:
                    return Geometry.Parse((fillRule == FillRule.EvenOdd ? "F0 " : "F1 ") + d);
                case "circle":
                    return new EllipseGeometry(new Rect(A("cx") - A("r"), A("cy") - A("r"), 2 * A("r"), 2 * A("r")));
                case "ellipse":
                    return new EllipseGeometry(new Rect(A("cx") - A("rx"), A("cy") - A("ry"), 2 * A("rx"), 2 * A("ry")));
                case "rect":
                    var rx = A("rx", A("ry"));
                    return new RectangleGeometry(new Rect(A("x"), A("y"), A("width"), A("height")), rx, A("ry", rx));
                case "line":
                    return Geometry.Parse(string.Create(CultureInfo.InvariantCulture, $"M{A("x1")},{A("y1")} L{A("x2")},{A("y2")}"));
                case "polygon" or "polyline" when Numbers((string?)e.Attribute("points") ?? "") is { Count: >= 4 } points:
                    var data = new System.Text.StringBuilder(string.Create(CultureInfo.InvariantCulture, $"M{points[0]},{points[1]}"));
                    for (var i = 2; i + 1 < points.Count; i += 2)
                        data.Append(CultureInfo.InvariantCulture, $" L{points[i]},{points[i + 1]}");
                    if (e.Name.LocalName == "polygon")
                        data.Append(" Z");
                    return Geometry.Parse(data.ToString());
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            // A shape the parser does not understand is left out; the rest of the icon still draws.
        }
        return null;
    }

    /// <summary>The element's style: presentation attributes and the style attribute over the inherited one.</summary>
    static Style Apply(XElement e, Style inherited, Dictionary<string, Color> gradients)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in e.Attributes())
            properties[attribute.Name.LocalName] = attribute.Value;
        if ((string?)e.Attribute("style") is { } css)
            foreach (var declaration in css.Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (declaration.Split(':', 2) is [var key, var value])
                    properties[key.Trim()] = value.Trim();
        var style = inherited;
        if (properties.TryGetValue("fill", out var fill))
            style = style with { Fill = Paint(fill, gradients) };
        if (properties.TryGetValue("stroke", out var stroke))
            style = style with { Stroke = Paint(stroke, gradients) };
        if (properties.TryGetValue("stroke-width", out var width))
            style = style with { StrokeWidth = Number(width, 1) };
        if (properties.TryGetValue("fill-rule", out var rule))
            style = style with { FillRule = rule == "evenodd" ? FillRule.EvenOdd : FillRule.NonZero };
        var opacity = Number(properties.GetValueOrDefault("opacity"), 1) * Number(properties.GetValueOrDefault("fill-opacity"), 1);
        return style with { Opacity = e.Name.LocalName == "g" ? opacity : inherited.Opacity * opacity };
    }

    static IBrush? Paint(string value, Dictionary<string, Color> gradients)
    {
        value = value.Trim();
        if (value is "none" or "transparent")
            return null;
        if (UrlReference().Match(value) is { Success: true } url)
            return gradients.TryGetValue(url.Groups[1].Value, out var color) ? new SolidColorBrush(color) : null;
        if (value == "currentColor")
            return new SolidColorBrush(Color.Parse("#C5C5C5"));
        return Color.TryParse(value, out var parsed) ? new SolidColorBrush(parsed) : Brushes.Black;
    }

    static IBrush? WithOpacity(IBrush? brush, double opacity) =>
        brush is ISolidColorBrush solid && opacity < 1 ? new SolidColorBrush(solid.Color, opacity) : brush;

    /// <summary>A gradient drawn as the average of its stops.</summary>
    static Color GradientColor(XElement gradient)
    {
        var stops = gradient.Elements().Where(s => s.Name.LocalName == "stop")
            .Select(s => (string?)s.Attribute("stop-color") ?? StyleValue(s, "stop-color"))
            .Select(c => c is not null && Color.TryParse(c, out var color) ? color : (Color?)null)
            .OfType<Color>().ToList();
        if (stops.Count == 0)
            return Colors.Gray;
        return Color.FromRgb((byte)stops.Average(c => c.R), (byte)stops.Average(c => c.G), (byte)stops.Average(c => c.B));
    }

    static string? StyleValue(XElement e, string name) =>
        ((string?)e.Attribute("style"))?.Split(';').Select(d => d.Split(':', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim() == name)?[1].Trim();

    static Matrix? Transform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var result = Matrix.Identity;
        foreach (Match part in TransformPart().Matches(value))
        {
            var n = Numbers(part.Groups[2].Value);
            var step = part.Groups[1].Value switch
            {
                "matrix" when n.Count == 6 => new Matrix(n[0], n[1], n[2], n[3], n[4], n[5]),
                "translate" when n.Count >= 1 => Matrix.CreateTranslation(n[0], n.Count > 1 ? n[1] : 0),
                "scale" when n.Count >= 1 => Matrix.CreateScale(n[0], n.Count > 1 ? n[1] : n[0]),
                "rotate" when n.Count == 1 => Matrix.CreateRotation(n[0] * Math.PI / 180),
                "rotate" when n.Count == 3 => Matrix.CreateTranslation(-n[1], -n[2]) * Matrix.CreateRotation(n[0] * Math.PI / 180) * Matrix.CreateTranslation(n[1], n[2]),
                _ => Matrix.Identity,
            };
            // SVG applies the rightmost transform first.
            result = step * result;
        }
        return result;
    }

    static PenLineCap Cap(string? value) => value switch { "round" => PenLineCap.Round, "square" => PenLineCap.Square, _ => PenLineCap.Flat };

    static PenLineJoin Join(string? value) => value switch { "round" => PenLineJoin.Round, "bevel" => PenLineJoin.Bevel, _ => PenLineJoin.Miter };

    static double Number(string? value, double fallback) =>
        value is not null && double.TryParse(value.Trim().TrimEnd('x', 'p'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    static List<double> Numbers(string value) =>
        NumberPattern().Matches(value).Select(m => double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();

    [GeneratedRegex(@"url\(\s*#([^)\s]+)\s*\)")]
    private static partial Regex UrlReference();

    [GeneratedRegex(@"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)")]
    private static partial Regex TransformPart();

    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex NumberPattern();
}

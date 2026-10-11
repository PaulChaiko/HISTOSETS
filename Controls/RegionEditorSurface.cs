using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HistOSets.Core;
using HistOSets.Services;

namespace HistOSets.Controls;

public enum RegionEditorTool { Pan, Select, Rectangle, Ellipse, Polygon, Brush, Eraser }
public enum RegionEditOperation { Create, Update, Delete }

public sealed class RegionsChangedEventArgs(IReadOnlyList<AtlasRegion> regions, Guid? selectedRegionId,
    RegionEditOperation operation) : EventArgs
{
    public IReadOnlyList<AtlasRegion> Regions { get; } = regions;
    public Guid? SelectedRegionId { get; } = selectedRegionId;
    public RegionEditOperation Operation { get; } = operation;
}

public sealed class RegionEditFailedEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

/// <summary>
/// A source-pixel drawing surface. A gesture changes only its preview until PointerUp or
/// FinishPolygon, then emits one complete operation for the owner's undo history.
/// </summary>
public sealed class RegionEditorSurface : FrameworkElement
{
    private enum Gesture { None, Pan, Draw, Move, Handle, Stroke }
    private BitmapSource? image;
    private double imageWidth, imageHeight;
    private IReadOnlyList<AtlasRegion> regions = Array.Empty<AtlasRegion>();
    private Guid? selectedRegionId;
    private RegionEditorTool tool = RegionEditorTool.Select;
    private bool canEdit = true;
    private double zoom = 1;
    private Vector offset;
    private bool fit = true;
    private Gesture gesture;
    private Point startViewport, startPixel;
    private Vector startOffset;
    private AtlasRegion? original;
    private RegionGeometry? preview;
    private int handleIndex = -1;
    private readonly List<AtlasPoint> polygon = [];
    private Point? polygonHover;
    private Point? pointerViewport;
    private AtlasPoint lastStrokePoint;
    private double brushRadius = 16;
    private double strokeRadius;
    private bool strokeErases;
    private bool subscribed;

    private static readonly Brush Backdrop = FrozenBrush(Color.FromRgb(23, 25, 33));
    private static readonly Brush RegionFill = FrozenBrush(Color.FromArgb(48, 192, 151, 246));
    private static readonly Brush RegionStroke = FrozenBrush(Color.FromRgb(192, 151, 246));
    private static readonly Brush SelectedFill = FrozenBrush(Color.FromArgb(62, 255, 124, 179));
    private static readonly Brush SelectedStroke = FrozenBrush(Color.FromRgb(255, 143, 190));
    private static readonly Brush PreviewFill = FrozenBrush(Color.FromArgb(56, 85, 210, 234));
    private static readonly Brush PreviewStroke = FrozenBrush(Color.FromRgb(99, 224, 244));

    public RegionEditorSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
        Loaded += (_, _) =>
        {
            if (!subscribed) { UiLanguage.Changed += LanguageChanged; subscribed = true; }
            if (fit) FitToView();
        };
        Unloaded += (_, _) =>
        {
            if (subscribed) { UiLanguage.Changed -= LanguageChanged; subscribed = false; }
            CancelPendingOperation();
        };
    }

    public event EventHandler<RegionsChangedEventArgs>? RegionsChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler? PendingOperationChanged;
    public event EventHandler? ZoomChanged;
    public event EventHandler<RegionEditFailedEventArgs>? OperationFailed;

    public BitmapSource? ImageSource
    {
        get => image;
        set
        {
            if (ReferenceEquals(image, value)) return;
            CancelPendingOperation();
            image = value;
            fit = true;
            FitToView();
            InvalidateVisual();
        }
    }

    /// <summary>Original image dimensions; preview bitmaps may have been downsampled.</summary>
    public double ImageWidth
    {
        get => imageWidth > 0 ? imageWidth : image?.PixelWidth ?? 0;
        set { imageWidth = ValidDimension(value); if (fit) FitToView(); InvalidateVisual(); }
    }
    public double ImageHeight
    {
        get => imageHeight > 0 ? imageHeight : image?.PixelHeight ?? 0;
        set { imageHeight = ValidDimension(value); if (fit) FitToView(); InvalidateVisual(); }
    }

    public IReadOnlyList<AtlasRegion> Regions
    {
        get => regions;
        set
        {
            CancelPendingOperation();
            regions = new ReadOnlyCollection<AtlasRegion>((value ?? Array.Empty<AtlasRegion>()).ToArray());
            if (selectedRegionId is not null && !regions.Any(region => region.Id == selectedRegionId))
                SelectRegion(null);
            InvalidateVisual();
        }
    }

    public Guid? SelectedRegionId { get => selectedRegionId; set => SelectRegion(value); }
    public RegionEditorTool Tool
    {
        get => tool;
        set
        {
            if (tool == value) return;
            CancelPendingOperation();
            tool = value;
            UpdateCursor();
            InvalidateVisual();
        }
    }
    public bool CanEdit
    {
        get => canEdit;
        set { if (canEdit == value) return; CancelPendingOperation(); canEdit = value; UpdateCursor(); InvalidateVisual(); }
    }
    /// <summary>The radius of brush and eraser in original image pixels, independent of zoom.</summary>
    public double BrushRadius
    {
        get => brushRadius;
        set
        {
            if (!double.IsFinite(value) || value < 0.01 || value > RegionGeometryOperations.MaxCoordinate)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (brushRadius == value) return;
            brushRadius = value;
            InvalidateVisual();
        }
    }
    public bool HasPendingPolygon => polygon.Count != 0;
    public bool HasPendingOperation => polygon.Count != 0 || gesture is Gesture.Draw or Gesture.Move or Gesture.Handle or Gesture.Stroke;
    public double Zoom => zoom;
    private bool HasImage => image is not null && ImageWidth > 0 && ImageHeight > 0;
    private bool IsDrawingTool => tool is RegionEditorTool.Rectangle or RegionEditorTool.Ellipse or RegionEditorTool.Polygon
        or RegionEditorTool.Brush or RegionEditorTool.Eraser;

    /// <summary>Explicitly start an independent brush region instead of adding to the selected one.</summary>
    public void BeginNewBrushRegion()
    {
        if (!canEdit) return;
        CancelPendingOperation();
        SelectRegion(null);
        Tool = RegionEditorTool.Brush;
    }

    public Point ImageToViewport(Point point) => new(point.X * zoom + offset.X, point.Y * zoom + offset.Y);
    public Point ImageToViewport(AtlasPoint point) => ImageToViewport(new Point(point.X, point.Y));
    public Point ViewportToImage(Point point) => new((point.X - offset.X) / zoom, (point.Y - offset.Y) / zoom);

    public void FitToView()
    {
        fit = true;
        if (!HasImage || ActualWidth <= 0 || ActualHeight <= 0) { InvalidateVisual(); return; }
        var availableWidth = Math.Max(1, ActualWidth - 24);
        var availableHeight = Math.Max(1, ActualHeight - 24);
        zoom = Math.Clamp(Math.Min(availableWidth / ImageWidth, availableHeight / ImageHeight), 0.00001, 64);
        offset = new Vector((ActualWidth - ImageWidth * zoom) / 2, (ActualHeight - ImageHeight * zoom) / 2);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void ActualSize() => SetZoom(1, new Point(ActualWidth / 2, ActualHeight / 2));
    public void ZoomBy(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        SetZoom(zoom * factor, new Point(ActualWidth / 2, ActualHeight / 2));
    }

    public void SelectRegion(Guid? id)
    {
        if (id is not null && !regions.Any(region => region.Id == id)) id = null;
        if (selectedRegionId == id) return;
        selectedRegionId = id;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Shared pointer entry points for mouse and other pointing devices.</summary>
    public void PointerDown(Point viewport, int clickCount = 1)
    {
        if (!HasImage || !IsFinite(viewport)) return;
        if (gesture != Gesture.None) CancelPendingOperation();
        pointerViewport = viewport;
        var pixel = ViewportToImage(viewport);
        startViewport = viewport;
        startPixel = ClampToImage(pixel);
        startOffset = offset;
        if (tool == RegionEditorTool.Pan || !canEdit)
        {
            gesture = Gesture.Pan;
            Cursor = Cursors.ScrollAll;
            return;
        }
        if (tool == RegionEditorTool.Polygon)
        {
            if (clickCount >= 2) { FinishPolygon(); return; }
            if (!InsideImage(pixel)) return;
            var point = new AtlasPoint(pixel.X, pixel.Y);
            if (polygon.Count == 0 || Distance(polygon[^1], point) * zoom >= 1)
            {
                polygon.Add(point);
                polygonHover = pixel;
                PendingOperationChanged?.Invoke(this, EventArgs.Empty);
            }
            InvalidateVisual();
            return;
        }
        if (tool is RegionEditorTool.Rectangle or RegionEditorTool.Ellipse)
        {
            if (!InsideImage(pixel)) return;
            gesture = Gesture.Draw;
            preview = null;
            PendingOperationChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }
        if (tool is RegionEditorTool.Brush or RegionEditorTool.Eraser)
        {
            if (!InsideImage(pixel)) return;
            original = regions.FirstOrDefault(region => region.Id == selectedRegionId);
            strokeErases = tool == RegionEditorTool.Eraser;
            if (strokeErases && original is null) return;
            strokeRadius = brushRadius;
            lastStrokePoint = new AtlasPoint(pixel.X, pixel.Y);
            gesture = Gesture.Stroke;
            if (!TryApplyStroke(original?.Geometry, [lastStrokePoint])) return;
            PendingOperationChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }
        var selected = regions.FirstOrDefault(region => region.Id == selectedRegionId);
        handleIndex = selected is null ? -1 : HitHandle(selected.Geometry, viewport);
        var hit = handleIndex >= 0 ? selected : regions.Reverse().FirstOrDefault(region => HitRegion(region.Geometry, pixel));
        SelectRegion(hit?.Id);
        if (hit is null) return;
        original = hit;
        preview = hit.Geometry;
        gesture = handleIndex >= 0 ? Gesture.Handle : Gesture.Move;
        startPixel = pixel;
        PendingOperationChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void PointerMove(Point viewport)
    {
        if (!HasImage || !IsFinite(viewport)) return;
        pointerViewport = viewport;
        var pixel = ClampToImage(ViewportToImage(viewport));
        switch (gesture)
        {
            case Gesture.Pan:
                offset = startOffset + (viewport - startViewport);
                fit = false;
                break;
            case Gesture.Draw:
                preview = RectangleOrEllipse(startPixel, pixel, tool == RegionEditorTool.Ellipse);
                break;
            case Gesture.Move when original is not null:
                preview = Translate(original.Geometry, ViewportToImage(viewport) - startPixel);
                break;
            case Gesture.Handle when original is not null:
                preview = Resize(original.Geometry, handleIndex, pixel);
                break;
            case Gesture.Stroke:
                var sourcePixel = ViewportToImage(viewport);
                var next = new AtlasPoint(sourcePixel.X, sourcePixel.Y);
                if (next != lastStrokePoint)
                {
                    // Boolean operations consume complete capsules between input samples, so a fast
                    // pointer never leaves gaps. Only the new stroke is clipped to image bounds.
                    if (!strokeErases || preview is not null)
                        if (!TryApplyStroke(preview, [lastStrokePoint, next])) return;
                    lastStrokePoint = next;
                }
                break;
            case Gesture.None when polygon.Count > 0:
                polygonHover = pixel;
                break;
        }
        InvalidateVisual();
    }

    public void PointerUp(Point viewport)
    {
        if (gesture == Gesture.None) return;
        PointerMove(viewport);
        var completedGesture = gesture;
        var completed = preview;
        var previous = original;
        ResetGesture();
        if (completedGesture == Gesture.Draw && completed is not null)
            Commit(new AtlasRegion(Guid.NewGuid(), completed), RegionEditOperation.Create);
        else if (completedGesture is Gesture.Move or Gesture.Handle && completed is not null && previous is not null
            && !SameGeometry(completed, previous.Geometry))
            Commit(previous with { Geometry = completed }, RegionEditOperation.Update);
        else if (completedGesture == Gesture.Stroke)
        {
            if (previous is null && completed is not null)
                Commit(new AtlasRegion(Guid.NewGuid(), completed), RegionEditOperation.Create);
            else if (previous is not null && completed is null)
                RemoveRegion(previous.Id);
            else if (previous is not null && completed is not null && !SameGeometry(completed, previous.Geometry))
                Commit(previous with { Geometry = completed }, RegionEditOperation.Update);
        }
        InvalidateVisual();
    }

    public bool FinishPolygon()
    {
        if (!canEdit || !HasImage || polygon.Count < 3 || !HasArea(polygon)) return false;
        RegionGeometry completed;
        try { completed = RegionGeometry.Polygon(polygon); }
        catch (ArgumentException) { return false; }
        CancelPendingOperation();
        Commit(new AtlasRegion(Guid.NewGuid(), completed), RegionEditOperation.Create);
        return true;
    }

    public void CancelPendingOperation()
    {
        var pending = HasPendingOperation;
        polygon.Clear();
        polygonHover = null;
        ResetGesture(false);
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (pending) PendingOperationChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public bool DeleteSelected()
    {
        if (!canEdit || selectedRegionId is null || !regions.Any(region => region.Id == selectedRegionId)) return false;
        CancelPendingOperation();
        RemoveRegion(selectedRegionId.Value);
        return true;
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRectangle(Backdrop, null, new Rect(RenderSize));
        if (!HasImage)
        {
            var text = new FormattedText(UiLanguage.T("Выберите изображение", "Select an image"),
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 15,
                Brushes.LightGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            context.DrawText(text, new Point(Math.Max(12, (ActualWidth - text.Width) / 2), Math.Max(12, (ActualHeight - text.Height) / 2)));
            return;
        }
        context.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        context.PushTransform(new MatrixTransform(zoom, 0, 0, zoom, offset.X, offset.Y));
        context.DrawImage(image, new Rect(0, 0, ImageWidth, ImageHeight));
        foreach (var region in regions)
        {
            if (original?.Id == region.Id && (preview is not null || gesture == Gesture.Stroke)) continue;
            var selected = region.Id == selectedRegionId;
            DrawRegion(context, region.Geometry, selected ? SelectedFill : RegionFill, selected ? SelectedStroke : RegionStroke);
        }
        if (preview is not null) DrawRegion(context, preview, PreviewFill, PreviewStroke);
        if (polygon.Count > 0)
        {
            var path = new StreamGeometry();
            using (var builder = path.Open())
            {
                builder.BeginFigure(ToPoint(polygon[0]), false, false);
                foreach (var point in polygon.Skip(1)) builder.LineTo(ToPoint(point), true, false);
                if (polygonHover is { } hover) builder.LineTo(hover, true, false);
            }
            context.DrawGeometry(null, new Pen(PreviewStroke, 2 / zoom), path);
            foreach (var point in polygon) context.DrawEllipse(PreviewStroke, null, ToPoint(point), 3 / zoom, 3 / zoom);
        }
        context.Pop();
        if (canEdit && tool == RegionEditorTool.Select)
        {
            var geometry = preview ?? regions.FirstOrDefault(region => region.Id == selectedRegionId)?.Geometry;
            if (geometry is not null)
                foreach (var point in Handles(geometry))
                {
                    var viewport = ImageToViewport(point);
                    context.DrawRectangle(Brushes.White, new Pen(SelectedStroke, 1.5),
                        new Rect(viewport.X - 4, viewport.Y - 4, 8, 8));
                }
        }
        if (canEdit && tool is (RegionEditorTool.Brush or RegionEditorTool.Eraser) && pointerViewport is { } cursorPoint)
        {
            var radius = (gesture == Gesture.Stroke ? strokeRadius : brushRadius) * zoom;
            context.DrawEllipse(null, new Pen(Brushes.Black, 3), cursorPoint, radius, radius);
            context.DrawEllipse(null, new Pen(tool == RegionEditorTool.Eraser ? Brushes.White : PreviewStroke, 1),
                cursorPoint, radius, radius);
        }
        context.Pop();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (fit) FitToView();
        else InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        PointerDown(e.GetPosition(this), e.ClickCount);
        if (gesture != Gesture.None) CaptureMouse();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        PointerMove(e.GetPosition(this));
        if (gesture != Gesture.None || polygon.Count > 0) e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        PointerUp(e.GetPosition(this));
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured) { pointerViewport = null; InvalidateVisual(); }
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (gesture != Gesture.None) CancelPendingOperation();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // A live drag uses a fixed transform so zoom cannot alter the gesture's coordinates.
        if (gesture == Gesture.None) SetZoom(zoom * Math.Pow(1.2, e.Delta / 120.0), e.GetPosition(this));
        e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { CancelPendingOperation(); e.Handled = true; }
        else if (e.Key == Key.Enter && polygon.Count > 0) { FinishPolygon(); e.Handled = true; }
        else if (e.Key == Key.Delete && !HasPendingOperation) { DeleteSelected(); e.Handled = true; }
    }

    private void Commit(AtlasRegion changed, RegionEditOperation operation)
    {
        regions = new ReadOnlyCollection<AtlasRegion>(operation == RegionEditOperation.Create
            ? regions.Append(changed).ToArray()
            : regions.Select(region => region.Id == changed.Id ? changed : region).ToArray());
        SelectRegion(changed.Id);
        RegionsChanged?.Invoke(this, new RegionsChangedEventArgs(regions, selectedRegionId, operation));
        InvalidateVisual();
    }

    private void RemoveRegion(Guid id)
    {
        regions = new ReadOnlyCollection<AtlasRegion>(regions.Where(region => region.Id != id).ToArray());
        if (selectedRegionId == id) SelectRegion(null);
        RegionsChanged?.Invoke(this, new RegionsChangedEventArgs(regions, selectedRegionId, RegionEditOperation.Delete));
        InvalidateVisual();
    }

    private bool TryApplyStroke(RegionGeometry? source, IReadOnlyList<AtlasPoint> points)
    {
        try
        {
            preview = RegionGeometryOperations.ApplyStroke(source, points, strokeRadius, ImageWidth, ImageHeight, strokeErases);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // A rejected operation must discard the whole live stroke, including earlier pointer
            // samples, rather than accidentally committing a partially changed region.
            CancelPendingOperation();
            OperationFailed?.Invoke(this, new RegionEditFailedEventArgs(UiLanguage.T(
                "Мазок отменён: превышена допустимая сложность области или диапазон координат. Попробуйте меньший радиус или более простое изменение.",
                "Stroke cancelled: the region complexity or coordinate range exceeds the supported limit. Try a smaller radius or a simpler edit.")));
            return false;
        }
    }

    private void ResetGesture(bool notify = true)
    {
        var pending = HasPendingOperation;
        gesture = Gesture.None;
        preview = null;
        original = null;
        handleIndex = -1;
        UpdateCursor();
        if (notify && pending) PendingOperationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetZoom(double value, Point anchor)
    {
        if (!HasImage || !double.IsFinite(value) || gesture != Gesture.None) return;
        var pixel = ViewportToImage(anchor);
        zoom = Math.Clamp(value, 0.00001, 64);
        offset = new Vector(anchor.X - pixel.X * zoom, anchor.Y - pixel.Y * zoom);
        fit = false;
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private RegionGeometry Translate(RegionGeometry geometry, Vector delta)
    {
        var bounds = Bounds(geometry);
        // Previously imported out-of-image coordinates remain unchanged unless explicitly edited.
        // For an in-bounds shape the whole moved shape remains inside the image.
        if (bounds.Left >= 0 && bounds.Right <= ImageWidth)
            delta.X = Math.Clamp(delta.X, -bounds.Left, ImageWidth - bounds.Right);
        if (bounds.Top >= 0 && bounds.Bottom <= ImageHeight)
            delta.Y = Math.Clamp(delta.Y, -bounds.Top, ImageHeight - bounds.Bottom);
        return RegionGeometryOperations.Translate(geometry, delta.X, delta.Y);
    }

    private static RegionGeometry? RectangleOrEllipse(Point first, Point second, bool ellipse)
    {
        var width = Math.Abs(second.X - first.X);
        var height = Math.Abs(second.Y - first.Y);
        if (width < 0.25 || height < 0.25) return null;
        var x = Math.Min(first.X, second.X);
        var y = Math.Min(first.Y, second.Y);
        return ellipse ? RegionGeometry.Ellipse(x, y, width, height) : RegionGeometry.Rectangle(x, y, width, height);
    }

    private static RegionGeometry Resize(RegionGeometry geometry, int handle, Point point)
    {
        if (geometry.Kind == "compound") return geometry;
        if (geometry.Kind == "polygon")
        {
            var points = geometry.Points.ToArray();
            if (handle < 0 || handle >= points.Length) return geometry;
            points[handle] = new AtlasPoint(point.X, point.Y);
            if (!HasArea(points)) return geometry;
            try { return RegionGeometry.Polygon(points); }
            catch (ArgumentException) { return geometry; }
        }
        var handles = Handles(geometry);
        return RectangleOrEllipse(handles[(handle + 2) % 4], point, geometry.Kind == "ellipse") ?? geometry;
    }

    private int HitHandle(RegionGeometry geometry, Point viewport)
    {
        var handles = Handles(geometry);
        for (var index = handles.Count - 1; index >= 0; index--)
            if ((ImageToViewport(handles[index]) - viewport).Length <= 7) return index;
        return -1;
    }
    private bool HitRegion(RegionGeometry geometry, Point point)
    {
        var shape = ToDrawingGeometry(geometry);
        return shape.FillContains(point) || shape.StrokeContains(new Pen(Brushes.White, 10 / zoom), point);
    }

    private static IReadOnlyList<Point> Handles(RegionGeometry geometry) => geometry.Kind switch
    {
        "compound" => Array.Empty<Point>(),
        "polygon" => geometry.Points.Select(ToPoint).ToArray(),
        _ => [new(geometry.X, geometry.Y), new(geometry.X + geometry.Width, geometry.Y),
            new(geometry.X + geometry.Width, geometry.Y + geometry.Height), new(geometry.X, geometry.Y + geometry.Height)]
    };
    private static Rect Bounds(RegionGeometry geometry)
    {
        var bounds = RegionGeometryOperations.GetBounds(geometry);
        return new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
    private static Geometry ToDrawingGeometry(RegionGeometry geometry)
    {
        if (geometry.Kind == "rectangle") return new RectangleGeometry(Bounds(geometry));
        if (geometry.Kind == "ellipse") return new EllipseGeometry(Bounds(geometry));
        var path = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var builder = path.Open())
        {
            foreach (var ring in RegionGeometryCodec.ToRings(geometry))
            {
                builder.BeginFigure(ToPoint(ring[0]), true, true);
                foreach (var point in ring.Skip(1)) builder.LineTo(ToPoint(point), true, false);
            }
        }
        path.Freeze();
        return path;
    }
    private void DrawRegion(DrawingContext context, RegionGeometry geometry, Brush fillBrush, Brush stroke)
        => context.DrawGeometry(fillBrush, new Pen(stroke, 2 / zoom), ToDrawingGeometry(geometry));
    private static bool SameGeometry(RegionGeometry first, RegionGeometry second) => first.Equals(second);
    private static bool HasArea(IReadOnlyList<AtlasPoint> points)
    {
        // Translate before the cross-products to avoid cancellation for small shapes far
        // from the origin. Existing degenerate legacy contours can still be displayed.
        var origin = points[0];
        var area = 0.0;
        for (var index = 1; index + 1 < points.Count; index++)
            area += (points[index].X - origin.X) * (points[index + 1].Y - origin.Y)
                - (points[index + 1].X - origin.X) * (points[index].Y - origin.Y);
        return double.IsFinite(area) && Math.Abs(area) > 0.000001;
    }
    private Point ClampToImage(Point point) => new(Math.Clamp(point.X, 0, ImageWidth), Math.Clamp(point.Y, 0, ImageHeight));
    private bool InsideImage(Point point) => point.X >= 0 && point.Y >= 0 && point.X <= ImageWidth && point.Y <= ImageHeight;
    private static bool IsFinite(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static double ValidDimension(double value) => double.IsFinite(value) && value > 0 ? value : 0;
    private static double Distance(AtlasPoint first, AtlasPoint second) => (ToPoint(first) - ToPoint(second)).Length;
    private static Point ToPoint(AtlasPoint point) => new(point.X, point.Y);
    private void UpdateCursor() => Cursor = tool == RegionEditorTool.Pan || !canEdit ? Cursors.Hand : IsDrawingTool ? Cursors.Cross : Cursors.Arrow;
    private void LanguageChanged(object? sender, EventArgs e) => InvalidateVisual();
    private static Brush FrozenBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
}

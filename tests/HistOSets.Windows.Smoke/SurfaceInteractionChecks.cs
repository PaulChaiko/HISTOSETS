using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HistOSets.Controls;
using HistOSets.Services;

internal static partial class Program
{
    private static void CheckSurfaceInteractions()
    {
        var drawing = new TrainingDrawingView();
        var bitmap = BitmapSource.Create(1000, 600, 96, 96, PixelFormats.Gray8, null, new byte[600_000], 1000);
        drawing.ShowImage(new LoadedImage(bitmap, 1000, 600, 96, 96));
        var window = new Window { Width = 780, Height = 650, Content = drawing, ShowInTaskbar = false };
        window.Show();
        try
        {
            Pump();
            var surface = drawing.Surface;
            CheckRadiusButtons(surface, (RepeatButton)drawing.FindName("DrawingDecreaseRadiusButton"),
                (RepeatButton)drawing.FindName("DrawingIncreaseRadiusButton"), (TextBox)drawing.FindName("DrawingBrushRadiusBox"));
            surface.FitToView();
            var fittedZoom = surface.Zoom;
            surface.ZoomBy(2);
            Check(surface.Zoom > fittedZoom, "Drawing can zoom in after fitting the original image");
            for (var i = 0; i < 70; i++) surface.ZoomBy(0.8);
            Check(Near(surface.Zoom, fittedZoom) && surface.ImageToViewport(new Point(0, 0)).X >= 0,
                "Repeated zoom-out stops at the fitted image size");

            surface.PointerDown(surface.ImageToViewport(new Point(250, 170)));
            surface.PointerDown(surface.ImageToViewport(new Point(320, 170)));
            var anchor = surface.ImageToViewport(new Point(300, 170));
            Check(surface.BeginPointerPan(anchor), "Right-drag navigation is available with an unfinished polygon");
            var end = anchor + new Vector(55, 30);
            surface.PointerMove(end);
            surface.EndPointerPan(end);
            var anchoredPixel = surface.ViewportToImage(end);
            Check(Near(anchoredPixel.X, 300) && Near(anchoredPixel.Y, 170)
                && surface.HasPendingPolygon && surface.Regions.Count == 0 && surface.Tool == RegionEditorTool.Polygon,
                "Panning translates the image without changing tools or adding a polygon vertex");
            surface.PointerDown(surface.ImageToViewport(new Point(290, 260)));
            Check(surface.FinishPolygon() && surface.Regions.Single().Geometry.Points.Count == 3,
                "The original unfinished polygon can be completed after panning");
        }
        finally { window.Close(); }
    }

    private static void CheckRadiusButtons(RegionEditorSurface surface, RepeatButton decrease, RepeatButton increase, TextBox display)
    {
        var initial = surface.BrushRadius;
        for (var i = 0; i < 3; i++) increase.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, increase));
        var larger = surface.BrushRadius;
        Check(increase.ClickMode == ClickMode.Press && increase.Interval < increase.Delay && larger > initial,
            "Holding the plus control repeatedly increases the radius in source pixels");
        decrease.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, decrease));
        Check(surface.BrushRadius < larger
            && double.TryParse(display.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var shown)
            && Near(surface.BrushRadius, shown),
            "Minus changes the actual radius and the displayed value stays synchronized");
    }
}

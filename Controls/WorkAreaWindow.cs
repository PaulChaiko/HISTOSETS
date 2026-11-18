using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace HistOSets.Controls;

/// <summary>Fits an application window to the usable part of its startup monitor.</summary>
public class WorkAreaWindow : Window
{
    private const uint NearestMonitor = 2;
    private const uint NoZOrderNoActivate = 0x0004 | 0x0010;
    private const double EdgeMargin = 8;
    private bool openingLayoutComplete;

    protected override void OnSourceInitialized(EventArgs e)
    {
        // A short form normally sizes itself to its content. On a small display,
        // make the same form scroll rather than clipping validation and buttons.
        if (SizeToContent != SizeToContent.Manual && Content is StackPanel form)
        {
            Content = null;
            Content = new ScrollViewer
            {
                Content = form,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
        }

        FitOpeningBounds();
        Loaded += FitAfterLayout;
        base.OnSourceInitialized(e);
    }

    private void FitAfterLayout(object sender, RoutedEventArgs e)
    {
        if (openingLayoutComplete) return;
        openingLayoutComplete = true;
        Loaded -= FitAfterLayout;
        // EnsureHandle and auto-sized content can change the startup placement
        // during the first layout. Recheck before starting any ordinary resize.
        FitOpeningBounds();
    }

    private void FitOpeningBounds()
    {
        if (WindowState != WindowState.Normal) return;
        var interop = new WindowInteropHelper(this);
        var handle = interop.Handle;
        if (handle == IntPtr.Zero) return;
        var owner = interop.Owner;
        var monitor = MonitorFromWindow(owner != IntPtr.Zero ? owner : handle, NearestMonitor);
        var info = new WindowMonitor { Size = Marshal.SizeOf<WindowMonitor>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

        // Native bounds and work area use the same desktop coordinates, including
        // monitors left or above the primary screen. Convert only the sizes to WPF
        // units so the window's layout respects its DPI.
        var transform = HwndSource.FromHwnd(handle)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var work = info.WorkArea;
        var marginX = Math.Min((int)Math.Ceiling(EdgeMargin / transform.M11), (work.Width - 1) / 2);
        var marginY = Math.Min((int)Math.Ceiling(EdgeMargin / transform.M22), (work.Height - 1) / 2);
        work.Left += marginX;
        work.Right -= marginX;
        work.Top += marginY;
        work.Bottom -= marginY;
        if (work.Width <= 0 || work.Height <= 0) return;

        var widthLimit = Math.Min(MaxWidth, work.Width * transform.M11);
        var heightLimit = Math.Min(MaxHeight, work.Height * transform.M22);
        SetCurrentValue(MinWidthProperty, Math.Min(MinWidth, widthLimit));
        SetCurrentValue(MinHeightProperty, Math.Min(MinHeight, heightLimit));
        // Keep content-sized dialogs bounded if their validation messages grow.
        // Ordinary windows can subsequently be resized or moved by the user.
        if (SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight)
            SetCurrentValue(MaxWidthProperty, widthLimit);
        else if (Width > widthLimit)
            SetCurrentValue(WidthProperty, widthLimit);
        if (SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight)
            SetCurrentValue(MaxHeightProperty, heightLimit);
        else if (Height > heightLimit)
            SetCurrentValue(HeightProperty, heightLimit);

        UpdateLayout();
        if (!GetWindowRect(handle, out var actual)) return;
        var width = Math.Min(actual.Width, work.Width);
        var height = Math.Min(actual.Height, work.Height);
        var left = actual.Left;
        var top = actual.Top;
        if (WindowStartupLocation == WindowStartupLocation.CenterOwner && owner != IntPtr.Zero && GetWindowRect(owner, out var parent))
        {
            left = parent.Left + (parent.Width - width) / 2;
            top = parent.Top + (parent.Height - height) / 2;
        }
        else if (WindowStartupLocation == WindowStartupLocation.CenterScreen)
        {
            left = work.Left + (work.Width - width) / 2;
            top = work.Top + (work.Height - height) / 2;
        }
        left = Math.Clamp(left, work.Left, work.Right - width);
        top = Math.Clamp(top, work.Top, work.Bottom - height);
        if (left != actual.Left || top != actual.Top || width != actual.Width || height != actual.Height)
            SetWindowPos(handle, IntPtr.Zero, left, top, width, height, NoZOrderNoActivate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenRectangle
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMonitor
    {
        public int Size;
        public ScreenRectangle MonitorArea, WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref WindowMonitor info);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out ScreenRectangle bounds);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

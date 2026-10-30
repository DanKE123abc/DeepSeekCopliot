using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace deepseek_copilot;

/// <summary>
/// A monitor described entirely in physical (device) pixels. Physical pixels are the only
/// coordinate space shared by monitors with different DPI scaling factors, so every
/// cross-monitor calculation should stay in this space.
/// </summary>
internal readonly record struct ScreenInfo(
    Rectangle Bounds,
    Rectangle WorkingArea,
    double Scale);

internal static class MonitorHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public static ScreenInfo FromScreen(Screen s)
    {
        var rect = new RECT
        {
            Left = s.Bounds.Left,
            Top = s.Bounds.Top,
            Right = s.Bounds.Right,
            Bottom = s.Bounds.Bottom
        };
        var hmon = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        var dpi = 96d;
        if (hmon != IntPtr.Zero && GetDpiForMonitor(hmon, 0, out var x, out _) == 0 && x > 0)
            dpi = x;
        return new ScreenInfo(s.Bounds, s.WorkingArea, dpi / 96d);
    }

    public static ScreenInfo MouseScreen()
    {
        GetCursorPos(out var p);
        return FromScreen(Screen.FromPoint(new Point(p.X, p.Y)));
    }

    public static IReadOnlyList<ScreenInfo> AllScreens()
    {
        var list = new List<ScreenInfo>();
        foreach (Screen s in Screen.AllScreens)
            list.Add(FromScreen(s));
        list.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
        return list;
    }
}

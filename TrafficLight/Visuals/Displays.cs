using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TrafficLight.Visuals;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
}

public sealed record MonitorInfo(nint Handle, string Name, string Label, RECT Bounds, bool Primary)
{
    public int WidthPx => Bounds.Right - Bounds.Left;
    public int HeightPx => Bounds.Bottom - Bounds.Top;
}

/// <summary>Перечисление мониторов (Win32) и полноэкранное размещение окна на выбранном.</summary>
public static class Displays
{
    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT lprc, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int CbSize;
        public RECT RcMonitor;
        public RECT RcWork;
        public uint DwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfn, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>Физические (пиксельные) границы окна на виртуальном рабочем столе.</summary>
    public static RECT GetBoundsPx(Window window)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        RECT r = default;
        if (hwnd != nint.Zero)
            GetWindowRect(hwnd, out r);
        return r;
    }

    /// <summary>Восстанавливает окно в физических пикселях — без DIP/DPI-путаницы.</summary>
    public static void ApplyBoundsPx(Window window, RECT b)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero)
            return;
        SetWindowPos(hwnd, nint.Zero, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, SWP_NOZORDER);
    }

    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (nint hMon, nint hdc, ref RECT rc, nint _) =>
        {
            var info = new MONITORINFOEX { CbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfoW(hMon, ref info))
                return true;

            uint dpiX = 96, dpiY = 96;
            try { GetDpiForMonitor(hMon, 0 /* EFFECTIVE */, out dpiX, out dpiY); } catch { /* нет shcore — сойдёт 96 */ }

            var name = info.DeviceName ?? "";
            if (name.StartsWith("\\\\.\\", StringComparison.Ordinal))
                name = name[4..];

            bool primary = (info.DwFlags & 1) != 0;
            string label = $"{name}  ·  {info.RcMonitor.Right - info.RcMonitor.Left}×{info.RcMonitor.Bottom - info.RcMonitor.Top}  ·  {dpiX * 100 / 96} %"
                         + (primary ? "  ·  основной" : "");

            list.Add(new MonitorInfo(hMon, name, label, info.RcMonitor, primary));
            return true;
        }, nint.Zero);

        return list;
    }

    /// <summary>Растягивает окно на весь монитор (пиксель-в-пиксель, через SetWindowPos).</summary>
    public static void ApplyFullscreen(Window window, MonitorInfo monitor)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero)
            return;

        SetWindowPos(hwnd, nint.Zero,
            monitor.Bounds.Left, monitor.Bounds.Top, monitor.WidthPx, monitor.HeightPx,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }
}

using System.Runtime.InteropServices;
using CrushGheChoi.Core.Layout;
using CrushGheChoi.Interop;

namespace CrushGheChoi.Services;

/// <summary>Một màn hình: vùng đầy đủ và vùng làm việc (trừ taskbar) theo pixel vật lý, kèm tỷ lệ hiển thị.</summary>
internal sealed record MonitorInfo(IntPtr Handle, string DeviceName, PixelRect Bounds, PixelRect WorkArea, double Scale, bool IsPrimary, int Number)
{
    public string DisplayName =>
        $"Màn hình {Number}{(IsPrimary ? " (chính)" : string.Empty)} — {Bounds.Width}×{Bounds.Height}, {Scale * 100:0}%";
}

/// <summary>Liệt kê màn hình và chọn màn hiển thị; màn đã chọn bị rút ra thì quay về màn chính.</summary>
internal static class ScreenService
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var handles = new List<IntPtr>();
        NativeMethods.MonitorEnumProc callback = (IntPtr h, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            handles.Add(h);
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        var monitors = handles
            .Select(Describe)
            .OfType<MonitorInfo>()
            .OrderBy(m => m.Bounds.X)
            .ThenBy(m => m.Bounds.Y)
            .ToList();
        return monitors.Select((m, i) => m with { Number = i + 1 }).ToList();
    }

    public static MonitorInfo Resolve(string? deviceName)
    {
        var all = GetMonitors();
        if (all.Count == 0)
        {
            // Hiếm khi xảy ra (đang đổi cấu hình màn hình); dùng tạm kích thước an toàn.
            var fallback = new PixelRect(0, 0, 1280, 720);
            return new MonitorInfo(IntPtr.Zero, string.Empty, fallback, fallback, 1.0, true, 1);
        }

        if (deviceName == Core.Settings.AppSettings.AnyMonitor)
        {
            return all[Random.Shared.Next(all.Count)];
        }

        return all.FirstOrDefault(m => deviceName is not null && m.DeviceName == deviceName)
            ?? all.FirstOrDefault(m => m.IsPrimary)
            ?? all[0];
    }

    public static MonitorInfo? FromWindow(IntPtr hwnd)
    {
        var handle = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = Describe(handle);
        return info is null ? null : GetMonitors().FirstOrDefault(m => m.DeviceName == info.DeviceName) ?? info;
    }

    private static MonitorInfo? Describe(IntPtr handle)
    {
        var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(), szDevice = string.Empty };
        if (handle == IntPtr.Zero || !NativeMethods.GetMonitorInfo(handle, ref mi))
        {
            return null;
        }

        var scale = NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 && dpi > 0
            ? dpi / 96.0
            : 1.0;
        return new MonitorInfo(
            handle,
            mi.szDevice,
            ToRect(mi.rcMonitor),
            ToRect(mi.rcWork),
            scale,
            (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
            0);
    }

    private static PixelRect ToRect(NativeMethods.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}

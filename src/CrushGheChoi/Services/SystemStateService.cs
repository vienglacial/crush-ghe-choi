using System.Diagnostics;
using System.Windows.Threading;
using CrushGheChoi.Interop;
using Microsoft.Win32;

namespace CrushGheChoi.Services;

/// <summary>Theo dõi khóa máy, ngủ/thức, đổi màn hình; và kiểm tra ứng dụng toàn màn hình hay desktop đang ở trước.</summary>
internal sealed class SystemStateService : IDisposable
{
    private static readonly HashSet<string> DesktopClasses = ["Progman", "WorkerW"];
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private readonly Dispatcher _dispatcher;

    public SystemStateService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public event EventHandler? Locked;

    public event EventHandler? Unlocked;

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public event EventHandler? DisplaysChanged;

    public bool IsSessionLocked { get; private set; }

    public bool IsSuspended { get; private set; }

    /// <summary>
    /// Có ứng dụng toàn màn hình, trình chiếu hoặc game Direct3D ở phía trước, trừ các app trong
    /// <paramref name="allowedApps"/> (tên tiến trình). Trả về tên tiến trình đang phủ màn hình để ghi nhật ký.
    /// </summary>
    public static bool IsFullScreenAppActive(IReadOnlyCollection<string> allowedApps, out string? processName)
    {
        processName = null;
        var fg = NativeMethods.GetForegroundWindow();
        if (!IsFullScreen(fg))
        {
            return false;
        }

        processName = ProcessNameOf(fg);
        return processName is null || !allowedApps.Contains(processName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsFullScreen(IntPtr fg)
    {
        if (NativeMethods.SHQueryUserNotificationState(out var state) == 0
            && state is NativeMethods.QUNS_BUSY or NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN or NativeMethods.QUNS_PRESENTATION_MODE)
        {
            return true;
        }

        // Dự phòng cho cửa sổ không viền phủ kín màn hình (ví dụ video toàn màn hình trong trình duyệt).
        if (fg == IntPtr.Zero || fg == NativeMethods.GetShellWindow() || IsDesktopWindow(fg) || IsOwnWindow(fg) || NativeMethods.IsZoomed(fg))
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(fg, out var r))
        {
            return false;
        }

        var monitor = ScreenService.FromWindow(fg);
        return monitor is not null
            && r.Left <= monitor.Bounds.X && r.Top <= monitor.Bounds.Y
            && r.Right >= monitor.Bounds.Right && r.Bottom >= monitor.Bounds.Bottom;
    }

    private static string? ProcessNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static bool IsDesktopInFront()
    {
        var fg = NativeMethods.GetForegroundWindow();
        return fg == IntPtr.Zero || IsDesktopWindow(fg);
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private static bool IsDesktopWindow(IntPtr hwnd) => DesktopClasses.Contains(NativeMethods.GetClassName(hwnd));

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == OwnProcessId;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                Post(() =>
                {
                    IsSessionLocked = true;
                    Locked?.Invoke(this, EventArgs.Empty);
                });
                break;
            case SessionSwitchReason.SessionUnlock:
                Post(() =>
                {
                    IsSessionLocked = false;
                    Unlocked?.Invoke(this, EventArgs.Empty);
                });
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Post(() =>
                {
                    IsSuspended = true;
                    Suspending?.Invoke(this, EventArgs.Empty);
                });
                break;
            case PowerModes.Resume:
                Post(() =>
                {
                    IsSuspended = false;
                    Resumed?.Invoke(this, EventArgs.Empty);
                });
                break;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Post(() => DisplaysChanged?.Invoke(this, EventArgs.Empty));

    private void Post(Action action)
    {
        // SystemEvents có thể báo trên luồng riêng; mọi xử lý đưa về luồng giao diện.
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("Lỗi khi xử lý sự kiện hệ thống", ex);
                Debug.Fail(ex.Message);
            }
        });
    }
}

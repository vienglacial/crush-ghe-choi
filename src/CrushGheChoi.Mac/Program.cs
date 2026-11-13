using Avalonia;
using CrushGheChoi.Services;

namespace CrushGheChoi;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Mở app lần nữa khi đang chạy thì phiên mới thoát luôn (macOS tự đưa app đang chạy lên trước).
        using var instance = SingleInstanceLock.TryAcquire();
        if (instance is null)
        {
            return 0;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Lỗi không xử lý được", ex);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new MacOSPlatformOptions { ShowInDock = false })
        .LogToTrace();
}

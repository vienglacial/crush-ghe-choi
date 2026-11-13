using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CrushGheChoi.Services;

namespace CrushGheChoi;

public partial class App : Application
{
    private AppController? _controller;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // App sống ở thanh menu; đóng cửa sổ Cài đặt không làm app thoát.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                // Giữ app chạy; lỗi được ghi lại để tra cứu.
                Log.Error("Lỗi trên luồng giao diện", e.Exception);
                e.Handled = true;
                _controller?.RecoverFromError();
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error("Lỗi trong tác vụ nền", e.Exception);
                e.SetObserved();
            };

            var args = desktop.Args ?? [];
            Log.Info($"Khởi động {typeof(App).Assembly.GetName().Version} ({Environment.OSVersion.Platform}) với tham số: {string.Join(' ', args)}");
            _controller = new AppController();
            desktop.Exit += (_, _) =>
            {
                _controller.Dispose();
                Log.Info("Đã thoát");
            };
            _controller.Start(args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

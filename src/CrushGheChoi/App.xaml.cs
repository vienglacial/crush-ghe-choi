using System.Windows;
using System.Windows.Threading;
using CrushGheChoi.Services;

namespace CrushGheChoi;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var wantsExit = e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase);
        _singleInstance = SingleInstance.TryAcquire();
        if (_singleInstance is null)
        {
            // Đã có một phiên đang chạy: nhờ phiên đó mở Cài đặt, gọi crush hoặc thoát, rồi phiên này thoát.
            SingleInstance.SignalExisting(
                wantsExit ? SecondLaunch.Exit
                : e.Args.Contains("--visit", StringComparer.OrdinalIgnoreCase) ? SecondLaunch.Visit
                : SecondLaunch.Show);
            Shutdown();
            return;
        }

        if (wantsExit)
        {
            // Không có phiên nào đang chạy để thoát.
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Lỗi không xử lý được", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Lỗi trong tác vụ nền", args.Exception);
            args.SetObserved();
        };

        System.Windows.Forms.Application.EnableVisualStyles();
        Log.Info($"Khởi động {typeof(App).Assembly.GetName().Version} với tham số: {string.Join(' ', e.Args)}");

        _controller = new AppController(Dispatcher);
        _singleInstance.Signaled += what => Dispatcher.BeginInvoke(() => _controller.HandleSecondLaunch(what));
        _controller.Start(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_controller is not null)
        {
            _controller.Dispose();
            Log.Info("Đã thoát");
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Giữ app chạy (khay, cài đặt, thoát vẫn dùng được); lỗi được ghi lại để tra cứu.
        Log.Error("Lỗi trên luồng giao diện", e.Exception);
        e.Handled = true;
        _controller?.RecoverFromError();
    }
}

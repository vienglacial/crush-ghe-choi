namespace CrushGheChoi.Services;

/// <summary>Việc phiên đang chạy cần làm khi app được mở thêm lần nữa.</summary>
internal enum SecondLaunch
{
    /// <summary>Mở cửa sổ Cài đặt.</summary>
    Show,

    /// <summary>Gọi crush ghé ngay (<c>--visit</c>).</summary>
    Visit,

    /// <summary>Thoát hẳn (<c>--exit</c>), ví dụ trước khi cập nhật hoặc gỡ app.</summary>
    Exit,
}

/// <summary>
/// Bảo đảm chỉ một phiên app chạy. Mở app lần nữa thì phiên mới chỉ gửi tín hiệu cho phiên đang chạy rồi thoát.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    // Phiên riêng (biến CRUSHGHECHOI_PROFILE) chạy song song được với phiên chính.
    private static readonly string Prefix = AppPaths.Profile is { } profile ? $@"Local\CrushGheChoi-{profile}-" : @"Local\CrushGheChoi-";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle[] _signals;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _listener;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _signals = Enum.GetValues<SecondLaunch>()
            .Select(s => new EventWaitHandle(false, EventResetMode.AutoReset, SignalName(s)))
            .ToArray();
        _listener = new Thread(Listen) { IsBackground = true, Name = "SingleInstanceListener" };
        _listener.Start();
    }

    public event Action<SecondLaunch>? Signaled;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, Prefix + "instance", out var createdNew);
        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public static void SignalExisting(SecondLaunch what)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(SignalName(what));
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Phiên kia đang khởi động hoặc vừa thoát.
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _listener.Join(TimeSpan.FromSeconds(1));
        foreach (var s in _signals)
        {
            s.Dispose();
        }

        _stop.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private static string SignalName(SecondLaunch what) => Prefix + what.ToString().ToLowerInvariant();

    private void Listen()
    {
        WaitHandle[] handles = [_stop, .. _signals];
        while (true)
        {
            var i = WaitHandle.WaitAny(handles);
            if (i == 0)
            {
                return;
            }

            Signaled?.Invoke((SecondLaunch)(i - 1));
        }
    }
}

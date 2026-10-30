using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CrushGheChoi.Services;

namespace CrushGheChoi.Overlay;

/// <summary>
/// Phát một chuỗi PNG mà không giải mã cả chuỗi vào RAM: giải mã trước vài khung phía trước
/// ở luồng nền, bỏ các khung đã qua. Khung chưa kịp giải mã thì giữ khung trước để không nháy.
/// </summary>
internal sealed class FrameSequence : IDisposable
{
    private const int Ahead = 8;
    private const int Keep = 12;

    private readonly string[] _paths;
    private readonly int _decodeHeight;
    private readonly bool _loop;
    private readonly ConcurrentDictionary<int, BitmapSource> _ready = new();
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();
    private volatile bool _disposed;
    private BitmapSource? _last;

    public FrameSequence(IReadOnlyList<string> paths, int decodeHeight, bool loop)
    {
        _paths = [.. paths];
        _decodeHeight = decodeHeight;
        _loop = loop;
    }

    public int Count => _paths.Length;

    /// <summary>Giải mã ngay vài khung đầu để lúc bắt đầu phát đã có hình.</summary>
    public void WarmUp()
    {
        Decode(0);
        Prefetch(1);
    }

    public ImageSource? Get(int index)
    {
        index = _loop ? index % Count : Math.Min(index, Count - 1);
        Prefetch(index);
        if (_ready.TryGetValue(index, out var frame))
        {
            _last = frame;
            Evict(index);
        }

        return _last;
    }

    public void Dispose()
    {
        _disposed = true;
        _ready.Clear();
        _last = null;
    }

    private void Prefetch(int from)
    {
        for (var k = 0; k < Ahead; k++)
        {
            var i = from + k;
            if (_loop)
            {
                i %= Count;
            }
            else if (i >= Count)
            {
                break;
            }

            if (_ready.ContainsKey(i) || !_inFlight.TryAdd(i, 0))
            {
                continue;
            }

            ThreadPool.QueueUserWorkItem(static s => s.self.Decode(s.i), (self: this, i), preferLocal: false);
        }
    }

    private void Evict(int current)
    {
        if (_ready.Count <= Keep)
        {
            return;
        }

        foreach (var key in _ready.Keys)
        {
            var ahead = _loop ? (key - current + Count) % Count : key - current;
            if (ahead < 0 || ahead > Ahead)
            {
                _ready.TryRemove(key, out _);
            }
        }
    }

    private void Decode(int i)
    {
        try
        {
            if (_disposed || _ready.ContainsKey(i))
            {
                return;
            }

            _ready[i] = LoadBitmap(_paths[i], _decodeHeight);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn($"Không đọc được khung hình {_paths[i]}: {ex.Message}");
        }
        finally
        {
            _inFlight.TryRemove(i, out _);
        }
    }

    public static BitmapSource LoadBitmap(string path, int decodeHeight)
    {
        using var stream = File.OpenRead(path);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodeHeight > 0)
        {
            bmp.DecodePixelHeight = decodeHeight;
        }

        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CrushGheChoi.Core.Cutout;
using SkiaSharp;

namespace CrushGheChoi.Cutout;

/// <summary>Đọc và ghi ảnh bằng Skia (đi kèm Avalonia). Các hàm đọc/ghi tệp chạy được trên luồng nền.</summary>
internal static class PhotoIO
{
    /// <summary>Cạnh dài tối đa khi tách nền: đủ nét cho hình cao 960 px mà vẫn vừa bộ nhớ.</summary>
    public const int MaxSide = 2048;

    public static readonly string[] Patterns = ["*.png", "*.jpg", "*.jpeg", "*.jfif", "*.webp", "*.bmp", "*.gif"];

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".bmp", ".gif", ".heic", ".heif",
    };

    public static bool LooksLikePhoto(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>Định dạng không có kênh trong suốt (JPG, HEIC, BMP): chắc chắn còn nền.</summary>
    public static bool IsAlwaysOpaque(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif" or ".heic" or ".heif" or ".bmp";

    /// <summary>
    /// Đọc ảnh, xoay đúng chiều theo EXIF (ảnh điện thoại), thu nhỏ để cạnh dài tối đa <see cref="MaxSide"/>.
    /// Ảnh có nền trong suốt (PNG đã tách nền) thì trả kèm kênh alpha; ảnh thường thì alpha là null.
    /// </summary>
    public static (RgbImage Rgb, Plane? Alpha) LoadPhoto(string path)
    {
        var (bgra, w, h, hasAlpha) = Decode(path, MaxSide, premultiplied: true);
        var r = new Plane(w, h);
        var g = new Plane(w, h);
        var b = new Plane(w, h);
        var alpha = new Plane(w, h);
        for (var i = 0; i < w * h; i++)
        {
            var a = bgra[i * 4 + 3];
            alpha.Data[i] = a / 255f;
            if (a == 0)
            {
                continue;
            }

            // Bỏ nhân trước alpha để lấy lại màu thật.
            var k = 1f / a;
            b.Data[i] = Math.Min(1f, bgra[i * 4] * k);
            g.Data[i] = Math.Min(1f, bgra[i * 4 + 1] * k);
            r.Data[i] = Math.Min(1f, bgra[i * 4 + 2] * k);
        }

        var rgb = new RgbImage(r, g, b);
        return (rgb, hasAlpha && CutoutPipeline.IsCutOut(alpha) ? alpha : null);
    }

    /// <summary>Ảnh nhỏ để hiện trong danh sách.</summary>
    public static Bitmap Thumbnail(string path, int height)
    {
        var (bgra, w, h, _) = Decode(path, height * 2, premultiplied: true);
        return ToBitmap(bgra, w, h);
    }

    /// <summary>Tạo ảnh hiển thị từ điểm ảnh BGRA đã nhân alpha.</summary>
    public static WriteableBitmap ToBitmap(byte[] premultipliedBgra, int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        CopyInto(bitmap, premultipliedBgra);
        return bitmap;
    }

    public static void CopyInto(WriteableBitmap bitmap, byte[] premultipliedBgra)
    {
        using var buffer = bitmap.Lock();
        var rowBytes = buffer.Size.Width * 4;
        for (var y = 0; y < buffer.Size.Height; y++)
        {
            Marshal.Copy(premultipliedBgra, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
        }
    }

    /// <summary>Ghi PNG từ điểm ảnh BGRA alpha thẳng.</summary>
    public static void SavePng(string path, int width, int height, byte[] bgra)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var pinned = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, pinned.AddrOfPinnedObject(), width * 4);
            using var data = pixmap.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new IOException("Không ghi được tệp PNG.");
            using var stream = File.Create(path);
            data.SaveTo(stream);
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>Lưu bản nháp (màu + alpha chưa cắt) thành PNG để chỉnh lại sau.</summary>
    public static void SaveDraft(string path, CutoutDraft draft)
    {
        int w = draft.Width, h = draft.Height;
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            bgra[i * 4] = ToByte(draft.Rgb.B.Data[i]);
            bgra[i * 4 + 1] = ToByte(draft.Rgb.G.Data[i]);
            bgra[i * 4 + 2] = ToByte(draft.Rgb.R.Data[i]);
            bgra[i * 4 + 3] = ToByte(draft.Alpha.Data[i]);
        }

        SavePng(path, w, h, bgra);
    }

    public static CutoutDraft LoadDraft(string path, DetectedFace? face)
    {
        var (bgra, w, h, _) = Decode(path, int.MaxValue, premultiplied: false);
        var rgb = RgbImage.FromBgra(bgra, w, h, w * 4);
        var alpha = new Plane(w, h);
        for (var i = 0; i < alpha.Data.Length; i++)
        {
            alpha.Data[i] = bgra[i * 4 + 3] / 255f;
        }

        return new CutoutDraft(rgb, alpha, face);
    }

    private static byte ToByte(float v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static (byte[] Bgra, int Width, int Height, bool HasAlpha) Decode(string path, int maxSide, bool premultiplied)
    {
        using var codec = SKCodec.Create(path)
            ?? throw new NotSupportedException("Không đọc được ảnh này. Hãy dùng tệp PNG, JPG hoặc WEBP.");
        var alphaType = premultiplied ? SKAlphaType.Premul : SKAlphaType.Unpremul;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, alphaType);
        var hasAlpha = codec.Info.AlphaType != SKAlphaType.Opaque;
        var bitmap = new SKBitmap(info);
        try
        {
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                throw new NotSupportedException($"Không giải mã được ảnh ({result}).");
            }

            var scale = Math.Min(1.0, maxSide / (double)Math.Max(info.Width, info.Height));
            if (scale < 1)
            {
                var target = new SKImageInfo(
                    Math.Max(1, (int)Math.Round(info.Width * scale)),
                    Math.Max(1, (int)Math.Round(info.Height * scale)),
                    SKColorType.Bgra8888,
                    alphaType);
                var resized = bitmap.Resize(target, SKFilterQuality.High)
                    ?? throw new NotSupportedException("Không thu nhỏ được ảnh.");
                bitmap.Dispose();
                bitmap = resized;
            }

            int w = bitmap.Width, h = bitmap.Height;
            var bytes = new byte[w * h * 4];
            var source = bitmap.GetPixels();
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(source + y * bitmap.RowBytes, bytes, y * w * 4, w * 4);
            }

            return Orient(bytes, w, h, (int)codec.EncodedOrigin) is var (oriented, ow, oh)
                ? (oriented, ow, oh, hasAlpha)
                : (bytes, w, h, hasAlpha);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>Xoay/lật theo giá trị EXIF Orientation (1..8). Trả về null khi không cần đổi.</summary>
    internal static (byte[] Bgra, int Width, int Height)? Orient(byte[] src, int w, int h, int orientation)
    {
        if (orientation is < 2 or > 8)
        {
            return null;
        }

        var swap = orientation >= 5;
        int dw = swap ? h : w, dh = swap ? w : h;
        var dst = new byte[src.Length];
        for (var y = 0; y < dh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                var (sx, sy) = orientation switch
                {
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),
                    6 => (y, h - 1 - x),
                    7 => (w - 1 - y, h - 1 - x),
                    _ => (w - 1 - y, x),
                };
                Buffer.BlockCopy(src, (sy * w + sx) * 4, dst, (y * dw + x) * 4, 4);
            }
        }

        return (dst, dw, dh);
    }
}

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CrushGheChoi.Core.Cutout;

namespace CrushGheChoi.Cutout;

/// <summary>Đọc và ghi ảnh bằng bộ giải mã của Windows (WIC). Các hàm ở đây chạy được trên luồng nền.</summary>
internal static class PhotoIO
{
    /// <summary>Cạnh dài tối đa khi tách nền: đủ nét cho hình cao 960 px mà vẫn vừa bộ nhớ.</summary>
    public const int MaxSide = 2048;

    public const string OpenFilter =
        "Ảnh (*.jpg, *.png, *.webp, *.heic…)|*.jpg;*.jpeg;*.jfif;*.png;*.webp;*.heic;*.heif;*.bmp;*.tif;*.tiff|Mọi tệp (*.*)|*.*";

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".heic", ".heif", ".bmp", ".tif", ".tiff",
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
        var bytes = File.ReadAllBytes(path);
        int rawW, rawH, orientation;
        using (var probe = new MemoryStream(bytes))
        {
            var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            (rawW, rawH) = (frame.PixelWidth, frame.PixelHeight);
            orientation = ReadOrientation(frame);
        }

        // Giải mã thẳng ra kích thước nhỏ (JPEG giải mã nhanh và nét hơn so với thu nhỏ sau).
        var scale = Math.Min(1.0, MaxSide / (double)Math.Max(rawW, rawH));
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = new MemoryStream(bytes);
        if (scale < 1)
        {
            image.DecodePixelWidth = Math.Max(1, (int)Math.Round(rawW * scale));
        }

        image.EndInit();
        BitmapSource source = Orient(image, orientation);
        var rgb = ToRgb(source, out var bgra);
        if (!HasAlpha(source.Format))
        {
            return (rgb, null);
        }

        var alpha = new Plane(rgb.Width, rgb.Height);
        for (var i = 0; i < alpha.Data.Length; i++)
        {
            alpha.Data[i] = bgra[i * 4 + 3] / 255f;
        }

        return (rgb, CutoutPipeline.IsCutOut(alpha) ? alpha : null);
    }

    private static bool HasAlpha(PixelFormat format) =>
        format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Prgba64 || format == PixelFormats.Rgba64
        || format == PixelFormats.Prgba128Float || format == PixelFormats.Rgba128Float || format == PixelFormats.Indexed8
        || format == PixelFormats.Indexed4 || format == PixelFormats.Indexed2 || format == PixelFormats.Indexed1;

    /// <summary>Ảnh nhỏ để hiện trong danh sách; không khóa tệp.</summary>
    public static BitmapSource Thumbnail(string path, int height)
    {
        var bytes = File.ReadAllBytes(path);
        int orientation;
        using (var probe = new MemoryStream(bytes))
        {
            orientation = ReadOrientation(BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0]);
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        if (orientation is 5 or 6 or 7 or 8)
        {
            image.DecodePixelWidth = height; // ảnh xoay 90°: chiều rộng gốc sẽ thành chiều cao
        }
        else
        {
            image.DecodePixelHeight = height;
        }

        image.EndInit();
        var result = Orient(image, orientation);
        result.Freeze();
        return result;
    }

    /// <summary>Ghi PNG từ điểm ảnh BGRA alpha thẳng.</summary>
    public static void SavePng(string path, int width, int height, byte[] bgra)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
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
        var frame = BitmapDecoder.Create(new MemoryStream(File.ReadAllBytes(path)), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        var rgb = ToRgb(frame, out var bgra);
        var alpha = new Plane(rgb.Width, rgb.Height);
        for (var i = 0; i < alpha.Data.Length; i++)
        {
            alpha.Data[i] = bgra[i * 4 + 3] / 255f;
        }

        return new CutoutDraft(rgb, alpha, face);
    }

    private static RgbImage ToRgb(BitmapSource source, out byte[] bgra)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        bgra = new byte[w * h * 4];
        converted.CopyPixels(bgra, w * 4, 0);
        return RgbImage.FromBgra(bgra, w, h, w * 4);
    }

    private static byte ToByte(float v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    /// <summary>Giá trị EXIF Orientation (1..8); 1 nếu không có.</summary>
    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort o and >= 1 and <= 8)
            {
                return o;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // Định dạng không có metadata (ví dụ BMP).
        }

        return 1;
    }

    private static BitmapSource Orient(BitmapSource source, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => null,
        };

        return transform is null ? source : new TransformedBitmap(source, transform);
    }
}

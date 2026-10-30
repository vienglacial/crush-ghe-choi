namespace CrushGheChoi.Core.Cutout;

/// <summary>Ảnh một kênh số thực (thường 0..1), lưu theo hàng.</summary>
public sealed class Plane
{
    public Plane(int width, int height, float[]? data = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (data is not null && data.Length != width * height)
        {
            throw new ArgumentException("Kích thước dữ liệu không khớp với ảnh.", nameof(data));
        }

        Width = width;
        Height = height;
        Data = data ?? new float[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    public float[] Data { get; }

    public float this[int x, int y]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }

    public Plane Clone() => new(Width, Height, (float[])Data.Clone());

    public Plane Crop(PixelBox box)
    {
        var dst = new Plane(box.Width, box.Height);
        for (var y = 0; y < box.Height; y++)
        {
            Array.Copy(Data, (box.Y + y) * Width + box.X, dst.Data, y * box.Width, box.Width);
        }

        return dst;
    }
}

/// <summary>Ảnh màu ba kênh 0..1.</summary>
public sealed class RgbImage(Plane r, Plane g, Plane b)
{
    public Plane R { get; } = r;

    public Plane G { get; } = g;

    public Plane B { get; } = b;

    public int Width => R.Width;

    public int Height => R.Height;

    /// <summary>Đọc từ điểm ảnh BGRA 8 bit (định dạng của WPF), bỏ kênh alpha.</summary>
    public static RgbImage FromBgra(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        var r = new Plane(width, height);
        var g = new Plane(width, height);
        var b = new Plane(width, height);
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            var o = y * width;
            for (var x = 0; x < width; x++)
            {
                b.Data[o + x] = row[x * 4] / 255f;
                g.Data[o + x] = row[x * 4 + 1] / 255f;
                r.Data[o + x] = row[x * 4 + 2] / 255f;
            }
        }

        return new RgbImage(r, g, b);
    }

    public RgbImage Crop(PixelBox box) => new(R.Crop(box), G.Crop(box), B.Crop(box));

    public Plane Luma()
    {
        var dst = new Plane(Width, Height);
        for (var i = 0; i < dst.Data.Length; i++)
        {
            dst.Data[i] = (R.Data[i] + G.Data[i] + B.Data[i]) / 3f;
        }

        return dst;
    }
}

/// <summary>Hình chữ nhật theo pixel nguyên.</summary>
public readonly record struct PixelBox(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public long Area => (long)Width * Height;
}

public readonly record struct PointF(float X, float Y);

public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;

    public float CenterX => X + Width / 2;

    public float CenterY => Y + Height / 2;
}

using CrushGheChoi.Core.Animation;

namespace CrushGheChoi.Core.Cutout;

/// <summary>Một nét cục tẩy, tọa độ trên bản nháp.</summary>
public sealed record EraseStroke(float X, float Y, float Radius);

/// <summary>
/// Những gì người dùng chỉnh trên một hình: đường cắt nửa người, điểm tim bay ra, hướng tim và các nét tẩy.
/// Tọa độ tính trên bản nháp (vùng quanh người cắt từ ảnh gốc).
/// </summary>
public sealed record CutoutEdits(int BottomCut, PointF KissAt, int Drift, IReadOnlyList<EraseStroke> Erase)
{
    public CutoutEdits WithStroke(EraseStroke stroke) => this with { Erase = [.. Erase, stroke] };
}

/// <summary>Bản nháp sau khi tách nền: vùng quanh người (màu + alpha chưa cắt) và gương mặt nếu tìm thấy.</summary>
public sealed class CutoutDraft(RgbImage rgb, Plane alpha, DetectedFace? face)
{
    public RgbImage Rgb { get; } = rgb;

    public Plane Alpha { get; } = alpha;

    public DetectedFace? Face { get; } = face;

    public int Width => Alpha.Width;

    public int Height => Alpha.Height;
}

/// <summary>Hình đã dựng xong: BGRA 8 bit, alpha thẳng (chưa nhân trước), kèm điểm tim theo tỷ lệ.</summary>
public sealed record RenderedCutout(int Width, int Height, byte[] Bgra, NormalizedPoint KissPoint, int Drift);

/// <summary>Các bước sau model: tìm vùng người, làm mịn mask. Cùng thuật toán với tools/prepare_asset.py.</summary>
public static class CutoutPipeline
{
    /// <summary>Người chiếm ít hơn tỷ lệ này của ảnh thì chạy model lần hai trên vùng quanh người cho mép tóc chi tiết hơn.</summary>
    public const double SecondPassAreaRatio = 0.55;

    /// <summary>Vùng quanh người trên mask thô của cả ảnh, chừa 3% lề.</summary>
    public static PixelBox FindRegion(Plane mask, PointF? seed)
    {
        var keep = ImageMath.MainBlob(mask, 0.5f, seed);
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (var y = 0; y < mask.Height; y++)
        {
            for (var x = 0; x < mask.Width; x++)
            {
                if (keep[y * mask.Width + x])
                {
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y);
                    y1 = y;
                }
            }
        }

        if (x1 < 0)
        {
            return new PixelBox(0, 0, mask.Width, mask.Height);
        }

        int mx = (int)Math.Round(0.03 * mask.Width), my = (int)Math.Round(0.03 * mask.Height);
        var left = Math.Max(0, x0 - mx);
        var top = Math.Max(0, y0 - my);
        var right = Math.Min(mask.Width, x1 + mx + 1);
        var bottom = Math.Min(mask.Height, y1 + my + 1);
        return new PixelBox(left, top, right - left, bottom - top);
    }

    public static bool NeedsSecondPass(PixelBox region, int width, int height) =>
        region.Area < SecondPassAreaRatio * width * height;

    /// <summary>Hình đã tách nền sẵn (PNG nền trong suốt): ít nhất 1% điểm ảnh trong suốt.</summary>
    public static bool IsCutOut(Plane alpha)
    {
        var transparent = 0;
        foreach (var a in alpha.Data)
        {
            if (a < 0.98f)
            {
                transparent++;
            }
        }

        return transparent >= alpha.Data.Length * 0.01;
    }

    /// <summary>Ghép hình trong suốt lên nền xám (để dò gương mặt không bị nhầm bởi màu ở vùng trong suốt).</summary>
    public static RgbImage OverGray(RgbImage rgb, Plane alpha)
    {
        Plane[] src = [rgb.R, rgb.G, rgb.B];
        var dst = new Plane[3];
        for (var c = 0; c < 3; c++)
        {
            dst[c] = new Plane(rgb.Width, rgb.Height);
            for (var i = 0; i < alpha.Data.Length; i++)
            {
                dst[c].Data[i] = src[c].Data[i] * alpha.Data[i] + 0.5f * (1 - alpha.Data[i]);
            }
        }

        return new RgbImage(dst[0], dst[1], dst[2]);
    }

    /// <summary>Bản nháp từ hình người dùng đã tự tách nền: giữ nguyên alpha, chỉ cắt bớt viền trống (chừa 3%).</summary>
    public static CutoutDraft FromCutOut(RgbImage rgb, Plane alpha, DetectedFace? face)
    {
        var bounds = ImageMath.Bounds(alpha, 0.02f) ?? new PixelBox(0, 0, alpha.Width, alpha.Height);
        int mx = (int)Math.Round(0.03 * alpha.Width), my = (int)Math.Round(0.03 * alpha.Height);
        var left = Math.Max(0, bounds.X - mx);
        var top = Math.Max(0, bounds.Y - my);
        var box = new PixelBox(left, top, Math.Min(alpha.Width, bounds.Right + mx) - left, Math.Min(alpha.Height, bounds.Bottom + my) - top);
        return new CutoutDraft(rgb.Crop(box), alpha.Crop(box), face?.Offset(-box.X, -box.Y));
    }

    /// <summary>Mask thô của vùng → alpha: bám mép theo ảnh, lọc vệt mờ, chỉ giữ người chính, vá lỗ li ti.</summary>
    public static Plane Refine(RgbImage rgb, Plane rawMask, PointF? seed, float cleanup = 0.08f)
    {
        var alpha = ImageMath.GuidedFilter(rgb.Luma(), rawMask, 4, 1e-3f);
        for (var i = 0; i < alpha.Data.Length; i++)
        {
            alpha.Data[i] = Math.Clamp((Math.Clamp(alpha.Data[i], 0, 1) - cleanup) / (0.92f - cleanup), 0, 1);
        }

        var keep = ImageMath.MainBlob(alpha, 0.3f, seed);
        for (var i = 0; i < alpha.Data.Length; i++)
        {
            if (!keep[i])
            {
                alpha.Data[i] = 0;
            }
        }

        ImageMath.FillPinholes(alpha);
        return alpha;
    }
}

/// <summary>Đề xuất đường cắt, điểm tim và hướng tim từ gương mặt; người dùng chỉnh lại được.</summary>
public static class CutoutPlanner
{
    /// <summary>Cắt dưới cằm bao nhiêu lần chiều cao mặt (lấy tới ngực). Đo từ các hình đã chỉnh tay: thường 0.8–1.4.</summary>
    public const float CutBelowFace = 1.2f;

    public static CutoutEdits Suggest(CutoutDraft draft)
    {
        var bounds = ImageMath.Bounds(draft.Alpha, 0.3f) ?? new PixelBox(0, 0, draft.Width, draft.Height);
        if (draft.Face is { } f)
        {
            var bottom = (int)Math.Round(f.Box.Bottom + CutBelowFace * f.Box.Height);
            bottom = Math.Clamp(bottom, (int)Math.Ceiling(f.Box.Bottom), draft.Height);

            // Tim bay về phía còn rộng của hình, đặt ngay ngoài mép mặt, ngang miệng.
            var drift = f.Box.CenterX > bounds.X + bounds.Width / 2f ? -1 : 1;
            var kx = drift < 0 ? f.Box.X - 0.25f * f.Box.Width : f.Box.Right + 0.25f * f.Box.Width;
            var ky = f.Mouth.Y - 0.1f * f.Box.Height;
            return new CutoutEdits(bottom, Clamp(new PointF(kx, ky), draft, bottom), drift, []);
        }

        // Không thấy mặt: giữ cả người, tim bay ra gần đỉnh đầu.
        var kiss = new PointF(bounds.X + bounds.Width * 0.6f, bounds.Y + bounds.Height * 0.2f);
        return new CutoutEdits(draft.Height, Clamp(kiss, draft, draft.Height), 1, []);
    }

    public static PointF Clamp(PointF p, CutoutDraft draft, int bottom) =>
        new(Math.Clamp(p.X, 0, draft.Width - 1), Math.Clamp(p.Y, 0, Math.Max(0, bottom - 1)));
}

/// <summary>Dựng hình PNG cuối cùng từ bản nháp và các chỉnh sửa.</summary>
public static class CutoutRenderer
{
    public const int MaxOutputHeight = 960;
    public const int MinCutHeight = 16;
    private const int CropMargin = 6;

    public static RenderedCutout Render(CutoutDraft draft, CutoutEdits edits, int maxHeight = MaxOutputHeight)
    {
        var keepRows = Math.Clamp(edits.BottomCut, MinCutHeight, draft.Height);
        var box = new PixelBox(0, 0, draft.Width, keepRows);
        var alpha = draft.Alpha.Crop(box);
        var rgb = draft.Rgb.Crop(box);
        ImageMath.ApplyErase(alpha, edits.Erase);

        var (left, right, top) = ImageMath.TouchedEdges(alpha);
        ImageMath.FadeCutEdges(alpha, left, right, top);
        rgb = ImageMath.Decontaminate(rgb, alpha);

        var bounds = ImageMath.Bounds(alpha, 0.02f)
            ?? throw new InvalidOperationException("Hình không còn phần nào sau khi cắt và tẩy.");
        var x0 = Math.Max(0, bounds.X - CropMargin);
        var x1 = Math.Min(alpha.Width, bounds.Right + CropMargin);
        var y0 = Math.Max(0, bounds.Y - CropMargin);
        var crop = new PixelBox(x0, y0, x1 - x0, alpha.Height - y0);
        alpha = alpha.Crop(crop);
        rgb = rgb.Crop(crop);

        var kiss = new NormalizedPoint(
            Math.Round(Math.Clamp((edits.KissAt.X - x0) / crop.Width, 0, 1), 3),
            Math.Round(Math.Clamp((edits.KissAt.Y - y0) / crop.Height, 0, 1), 3));

        // Thu nhỏ trên màu đã nhân alpha để mép không bị viền tối.
        Plane[] premul = [rgb.R, rgb.G, rgb.B];
        foreach (var c in premul)
        {
            for (var i = 0; i < c.Data.Length; i++)
            {
                c.Data[i] *= alpha.Data[i];
            }
        }

        int outW = crop.Width, outH = crop.Height;
        if (outH > maxHeight)
        {
            outW = Math.Max(1, (int)Math.Round(crop.Width * (double)maxHeight / crop.Height));
            outH = maxHeight;
            alpha = ImageMath.Resample(alpha, outW, outH);
            for (var c = 0; c < 3; c++)
            {
                premul[c] = ImageMath.Resample(premul[c], outW, outH);
            }
        }

        var bgra = new byte[outW * outH * 4];
        for (var i = 0; i < outW * outH; i++)
        {
            var a = Math.Clamp(alpha.Data[i], 0, 1);
            var a8 = (byte)Math.Round(a * 255);
            if (a8 == 0)
            {
                continue; // pixel trong suốt hoàn toàn thì xóa luôn màu
            }

            bgra[i * 4] = ToByte(premul[2].Data[i] / a);
            bgra[i * 4 + 1] = ToByte(premul[1].Data[i] / a);
            bgra[i * 4 + 2] = ToByte(premul[0].Data[i] / a);
            bgra[i * 4 + 3] = a8;
        }

        return new RenderedCutout(outW, outH, bgra, kiss, edits.Drift < 0 ? -1 : 1);
    }

    private static byte ToByte(float v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}

/// <summary>
/// Bản xem trước nhỏ cho màn chỉnh: dựng lại tức thì mỗi lần kéo đường cắt hay tẩy.
/// Phần dưới đường cắt vẫn hiện mờ để người dùng thấy mình đang bỏ đi những gì.
/// </summary>
public sealed class CutoutPreview
{
    private readonly RgbImage _rgb;
    private readonly Plane _alpha;

    public CutoutPreview(CutoutDraft draft, int maxSide)
    {
        Scale = Math.Min(1f, maxSide / (float)Math.Max(draft.Width, draft.Height));
        Width = Math.Max(1, (int)Math.Round(draft.Width * Scale));
        Height = Math.Max(1, (int)Math.Round(draft.Height * Scale));
        _alpha = ImageMath.Resample(draft.Alpha, Width, Height);

        // Khử viền màu nền như lúc dựng hình thật, để bản xem trước không lộ đốm sáng ở kẽ tóc.
        _rgb = ImageMath.Decontaminate(ImageMath.Resample(draft.Rgb, Width, Height), _alpha);
    }

    /// <summary>Tỷ lệ từ tọa độ bản nháp sang tọa độ bản xem trước.</summary>
    public float Scale { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Điểm ảnh BGRA đã nhân alpha (Pbgra32 của WPF).</summary>
    public byte[] Render(CutoutEdits edits, float cutOffOpacity = 0.25f)
    {
        var alpha = _alpha.Clone();
        ImageMath.ApplyErase(alpha, edits.Erase, Scale);
        var cut = (int)Math.Round(edits.BottomCut * Scale);
        var bgra = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            var dim = y >= cut ? cutOffOpacity : 1f;
            for (var x = 0; x < Width; x++)
            {
                var i = y * Width + x;
                var a = Math.Clamp(alpha.Data[i], 0, 1) * dim;
                bgra[i * 4] = (byte)Math.Round(Math.Clamp(_rgb.B.Data[i], 0, 1) * a * 255);
                bgra[i * 4 + 1] = (byte)Math.Round(Math.Clamp(_rgb.G.Data[i], 0, 1) * a * 255);
                bgra[i * 4 + 2] = (byte)Math.Round(Math.Clamp(_rgb.R.Data[i], 0, 1) * a * 255);
                bgra[i * 4 + 3] = (byte)Math.Round(a * 255);
            }
        }

        return bgra;
    }
}

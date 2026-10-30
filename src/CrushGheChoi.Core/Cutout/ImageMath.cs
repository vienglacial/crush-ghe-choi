namespace CrushGheChoi.Core.Cutout;

/// <summary>Các phép xử lý ảnh dùng khi tách nền. Cùng thuật toán với tools/prepare_asset.py.</summary>
public static class ImageMath
{
    /// <summary>Cạnh dài của bản thu nhỏ dùng để tìm vùng liên thông, đủ chi tiết mà vẫn nhanh.</summary>
    public const int BlobAnalysisSide = 480;

    /// <summary>Trung bình cửa sổ (2r+1)², mép ảnh được kéo dài (lặp lại pixel mép).</summary>
    public static Plane BoxBlur(Plane src, int r)
    {
        if (r <= 0)
        {
            return src.Clone();
        }

        int w = src.Width, h = src.Height;
        var s = src.Data;
        var tmp = new float[w * h];
        var dst = new float[w * h];
        var inv = 1.0 / (2 * r + 1);

        Parallel.For(0, h, y =>
        {
            var o = y * w;
            double sum = 0;
            for (var i = -r; i <= r; i++)
            {
                sum += s[o + Math.Clamp(i, 0, w - 1)];
            }

            for (var x = 0; x < w; x++)
            {
                tmp[o + x] = (float)(sum * inv);
                sum += s[o + Math.Min(x + r + 1, w - 1)] - s[o + Math.Max(x - r, 0)];
            }
        });

        // Chạy theo từng dải cột để đọc bộ nhớ liền mạch.
        const int band = 64;
        Parallel.For(0, (w + band - 1) / band, chunk =>
        {
            int x0 = chunk * band, x1 = Math.Min(w, x0 + band);
            var sums = new double[x1 - x0];
            for (var i = -r; i <= r; i++)
            {
                var row = Math.Clamp(i, 0, h - 1) * w;
                for (var x = x0; x < x1; x++)
                {
                    sums[x - x0] += tmp[row + x];
                }
            }

            for (var y = 0; y < h; y++)
            {
                var o = y * w;
                var add = Math.Min(y + r + 1, h - 1) * w;
                var sub = Math.Max(y - r, 0) * w;
                for (var x = x0; x < x1; x++)
                {
                    dst[o + x] = (float)(sums[x - x0] * inv);
                    sums[x - x0] += tmp[add + x] - tmp[sub + x];
                }
            }
        });

        return new Plane(w, h, dst);
    }

    /// <summary>Nở vùng đúng ra r pixel theo cả hai chiều (ô vuông).</summary>
    public static bool[] Dilate(bool[] mask, int w, int h, int r)
    {
        if (r <= 0)
        {
            return (bool[])mask.Clone();
        }

        var tmp = new bool[w * h];
        var dst = new bool[w * h];
        Parallel.For(0, h, y =>
        {
            var o = y * w;
            var count = 0;
            for (var i = 0; i <= Math.Min(r, w - 1); i++)
            {
                count += mask[o + i] ? 1 : 0;
            }

            for (var x = 0; x < w; x++)
            {
                tmp[o + x] = count > 0;
                if (x + r + 1 < w && mask[o + x + r + 1])
                {
                    count++;
                }

                if (x - r >= 0 && mask[o + x - r])
                {
                    count--;
                }
            }
        });

        Parallel.For(0, w, x =>
        {
            var count = 0;
            for (var i = 0; i <= Math.Min(r, h - 1); i++)
            {
                count += tmp[i * w + x] ? 1 : 0;
            }

            for (var y = 0; y < h; y++)
            {
                dst[y * w + x] = count > 0;
                if (y + r + 1 < h && tmp[(y + r + 1) * w + x])
                {
                    count++;
                }

                if (y - r >= 0 && tmp[(y - r) * w + x])
                {
                    count--;
                }
            }
        });

        return dst;
    }

    /// <summary>Đổi kích thước bằng bộ lọc tam giác; khi thu nhỏ thì nới rộng bộ lọc để không răng cưa.</summary>
    public static Plane Resample(Plane src, int width, int height)
    {
        if (width == src.Width && height == src.Height)
        {
            return src.Clone();
        }

        var horizontal = Weights(src.Width, width);
        var vertical = Weights(src.Height, height);
        var tmp = new float[width * src.Height];
        Parallel.For(0, src.Height, y =>
        {
            var o = y * src.Width;
            for (var x = 0; x < width; x++)
            {
                var (start, weights) = horizontal[x];
                double v = 0;
                for (var k = 0; k < weights.Length; k++)
                {
                    v += src.Data[o + start + k] * weights[k];
                }

                tmp[y * width + x] = (float)v;
            }
        });

        var dst = new float[width * height];
        Parallel.For(0, height, y =>
        {
            var (start, weights) = vertical[y];
            for (var x = 0; x < width; x++)
            {
                double v = 0;
                for (var k = 0; k < weights.Length; k++)
                {
                    v += tmp[(start + k) * width + x] * weights[k];
                }

                dst[y * width + x] = (float)v;
            }
        });

        return new Plane(width, height, dst);
    }

    public static RgbImage Resample(RgbImage src, int width, int height) =>
        new(Resample(src.R, width, height), Resample(src.G, width, height), Resample(src.B, width, height));

    private static (int Start, float[] Weights)[] Weights(int inSize, int outSize)
    {
        var scale = (double)inSize / outSize;
        var filterScale = Math.Max(1.0, scale);
        var result = new (int, float[])[outSize];
        for (var i = 0; i < outSize; i++)
        {
            var center = (i + 0.5) * scale;
            var lo = Math.Max(0, (int)Math.Floor(center - filterScale));
            var hi = Math.Min(inSize, (int)Math.Ceiling(center + filterScale));
            if (hi <= lo)
            {
                (lo, hi) = (Math.Clamp((int)center, 0, inSize - 1), Math.Clamp((int)center, 0, inSize - 1) + 1);
            }

            var w = new float[hi - lo];
            double sum = 0;
            for (var j = lo; j < hi; j++)
            {
                var t = Math.Max(0, 1 - Math.Abs((j + 0.5 - center) / filterScale));
                w[j - lo] = (float)t;
                sum += t;
            }

            if (sum <= 0)
            {
                w[Math.Clamp((int)center - lo, 0, w.Length - 1)] = 1;
                sum = 1;
            }

            for (var k = 0; k < w.Length; k++)
            {
                w[k] = (float)(w[k] / sum);
            }

            result[i] = (lo, w);
        }

        return result;
    }

    /// <summary>Guided filter một kênh (He và cộng sự): mép mask bám theo chi tiết của ảnh gốc, như sợi tóc.</summary>
    public static Plane GuidedFilter(Plane guide, Plane src, int r, float eps)
    {
        var n = guide.Data.Length;
        var gp = new Plane(guide.Width, guide.Height);
        var gg = new Plane(guide.Width, guide.Height);
        for (var i = 0; i < n; i++)
        {
            gp.Data[i] = guide.Data[i] * src.Data[i];
            gg.Data[i] = guide.Data[i] * guide.Data[i];
        }

        var meanI = BoxBlur(guide, r);
        var meanP = BoxBlur(src, r);
        var corrIp = BoxBlur(gp, r);
        var corrII = BoxBlur(gg, r);

        var a = gp;
        var b = gg;
        for (var i = 0; i < n; i++)
        {
            var cov = corrIp.Data[i] - meanI.Data[i] * meanP.Data[i];
            var variance = corrII.Data[i] - meanI.Data[i] * meanI.Data[i];
            a.Data[i] = cov / (variance + eps);
            b.Data[i] = meanP.Data[i] - a.Data[i] * meanI.Data[i];
        }

        var meanA = BoxBlur(a, r);
        var meanB = BoxBlur(b, r);
        var dst = new Plane(guide.Width, guide.Height);
        for (var i = 0; i < n; i++)
        {
            dst.Data[i] = meanA.Data[i] * guide.Data[i] + meanB.Data[i];
        }

        return dst;
    }

    /// <summary>Loang 4 hướng từ một điểm trong vùng đặc.</summary>
    public static bool[] Flood(bool[] solid, int w, int h, int seed)
    {
        var keep = new bool[solid.Length];
        if (!solid[seed])
        {
            return keep;
        }

        var queue = new Queue<int>();
        keep[seed] = true;
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            int x = i % w, y = i / w;
            Visit(x > 0, i - 1);
            Visit(x < w - 1, i + 1);
            Visit(y > 0, i - w);
            Visit(y < h - 1, i + w);
        }

        return keep;

        void Visit(bool inside, int j)
        {
            if (inside && solid[j] && !keep[j])
            {
                keep[j] = true;
                queue.Enqueue(j);
            }
        }
    }

    /// <summary>
    /// Vùng liên thông chính: chứa điểm <paramref name="seed"/> (ví dụ gương mặt), hoặc chỗ dày đặc nhất.
    /// Tính trên bản thu nhỏ cho nhanh rồi nở ra vài pixel để không cắt lẹm mép.
    /// </summary>
    public static bool[] MainBlob(Plane alpha, float threshold, PointF? seed)
    {
        int w = alpha.Width, h = alpha.Height;
        var s = BlobAnalysisSide / (float)Math.Max(w, h);
        int sw = Math.Max(1, (int)Math.Round(w * s)), sh = Math.Max(1, (int)Math.Round(h * s));
        var small = Resample(alpha, sw, sh);
        var solid = new bool[sw * sh];
        for (var i = 0; i < solid.Length; i++)
        {
            solid[i] = small.Data[i] > threshold;
        }

        var seedIndex = -1;
        if (seed is { } p)
        {
            var sx = Math.Clamp((int)Math.Round(p.X * s), 0, sw - 1);
            var sy = Math.Clamp((int)Math.Round(p.Y * s), 0, sh - 1);
            if (solid[sy * sw + sx])
            {
                seedIndex = sy * sw + sx;
            }
        }

        if (seedIndex < 0)
        {
            var solidPlane = new Plane(sw, sh);
            for (var i = 0; i < solid.Length; i++)
            {
                solidPlane.Data[i] = solid[i] ? 1 : 0;
            }

            var density = BoxBlur(solidPlane, 8);
            var best = float.MinValue;
            for (var i = 0; i < solid.Length; i++)
            {
                if (solid[i] && density.Data[i] > best)
                {
                    best = density.Data[i];
                    seedIndex = i;
                }
            }
        }

        if (seedIndex < 0)
        {
            return new bool[w * h];
        }

        var keep = Flood(solid, sw, sh, seedIndex);
        var big = new bool[w * h];
        Parallel.For(0, h, y =>
        {
            var syi = Math.Min(sh - 1, (int)((y + 0.5) * sh / h));
            for (var x = 0; x < w; x++)
            {
                big[y * w + x] = keep[syi * sw + Math.Min(sw - 1, (int)((x + 0.5) * sw / w))];
            }
        });

        return Dilate(big, w, h, Math.Max(3, (int)Math.Round(6 / s / 4)));
    }

    /// <summary>Khung bao quanh các pixel có alpha lớn hơn ngưỡng; null nếu không có pixel nào.</summary>
    public static PixelBox? Bounds(Plane alpha, float threshold)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (var y = 0; y < alpha.Height; y++)
        {
            var o = y * alpha.Width;
            for (var x = 0; x < alpha.Width; x++)
            {
                if (alpha.Data[o + x] > threshold)
                {
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y);
                    y1 = y;
                }
            }
        }

        return x1 < 0 ? null : new PixelBox(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>Lỗ li ti giữa người (mặt, áo) do model đoán sót: chỗ gần như bị phần đặc bao kín thì làm đặc lại.</summary>
    public static void FillPinholes(Plane alpha)
    {
        var r = Math.Max(3, (int)Math.Round(0.004 * Math.Max(alpha.Width, alpha.Height)));
        var solid = new Plane(alpha.Width, alpha.Height);
        for (var i = 0; i < solid.Data.Length; i++)
        {
            solid.Data[i] = alpha.Data[i] > 0.6f ? 1 : 0;
        }

        var enclosed = BoxBlur(solid, r);
        for (var i = 0; i < alpha.Data.Length; i++)
        {
            if (enclosed.Data[i] > 0.85f)
            {
                alpha.Data[i] = 1;
            }
        }
    }

    /// <summary>Người chạm cạnh khung (ảnh selfie cắt ngang tay, vai): mờ dần ở cạnh đó thay vì một đường thẳng.</summary>
    public static void FadeCutEdges(Plane alpha, bool left, bool right, bool top)
    {
        int w = alpha.Width, h = alpha.Height;
        var n = Math.Max(8, (int)Math.Round(0.05 * w));
        for (var y = 0; y < h; y++)
        {
            var o = y * w;
            for (var i = 0; i < Math.Min(n, w); i++)
            {
                var ramp = (float)i / n;
                if (left)
                {
                    alpha.Data[o + i] *= ramp;
                }

                if (right)
                {
                    alpha.Data[o + w - 1 - i] *= ramp;
                }
            }
        }

        if (top)
        {
            for (var y = 0; y < Math.Min(n, h); y++)
            {
                var ramp = (float)y / n;
                for (var x = 0; x < w; x++)
                {
                    alpha.Data[y * w + x] *= ramp;
                }
            }
        }
    }

    /// <summary>Có phần người đặc chạm cạnh nào của khung không (xét dải 4 pixel sát cạnh).</summary>
    public static (bool Left, bool Right, bool Top) TouchedEdges(Plane alpha)
    {
        const int edge = 4;
        bool left = false, right = false, top = false;
        for (var y = 0; y < alpha.Height; y++)
        {
            for (var k = 0; k < Math.Min(edge, alpha.Width); k++)
            {
                left |= alpha[k, y] > 0.5f;
                right |= alpha[alpha.Width - 1 - k, y] > 0.5f;
            }
        }

        for (var y = 0; y < Math.Min(edge, alpha.Height); y++)
        {
            for (var x = 0; x < alpha.Width; x++)
            {
                top |= alpha[x, y] > 0.5f;
            }
        }

        return (left, right, top);
    }

    /// <summary>Thay màu ở mép bán trong suốt bằng màu của phần người kế bên, tránh viền màu của nền cũ.</summary>
    public static RgbImage Decontaminate(RgbImage rgb, Plane alpha)
    {
        var n = alpha.Data.Length;
        var filled = new Plane(alpha.Width, alpha.Height);
        for (var i = 0; i < n; i++)
        {
            filled.Data[i] = alpha.Data[i] > 0.92f ? 1 : 0;
        }

        Plane[] fg = [rgb.R.Clone(), rgb.G.Clone(), rgb.B.Clone()];
        var weighted = new Plane(alpha.Width, alpha.Height);
        foreach (var r in new[] { 1, 2, 4, 8, 16, 32 })
        {
            var wsum = BoxBlur(filled, r);
            var need = new bool[n];
            var any = false;
            for (var i = 0; i < n; i++)
            {
                need[i] = filled.Data[i] < 0.5f && wsum.Data[i] > 1e-4f;
                any |= need[i];
            }

            if (!any)
            {
                continue;
            }

            foreach (var channel in fg)
            {
                for (var i = 0; i < n; i++)
                {
                    weighted.Data[i] = channel.Data[i] * filled.Data[i];
                }

                var c = BoxBlur(weighted, r);
                for (var i = 0; i < n; i++)
                {
                    if (need[i])
                    {
                        channel.Data[i] = c.Data[i] / Math.Max(wsum.Data[i], 1e-6f);
                    }
                }
            }

            for (var i = 0; i < n; i++)
            {
                if (need[i])
                {
                    filled.Data[i] = 1;
                }
            }
        }

        Plane[] src = [rgb.R, rgb.G, rgb.B];
        for (var c = 0; c < 3; c++)
        {
            for (var i = 0; i < n; i++)
            {
                if (alpha.Data[i] >= 0.92f)
                {
                    fg[c].Data[i] = src[c].Data[i];
                }
            }
        }

        return new RgbImage(fg[0], fg[1], fg[2]);
    }

    /// <summary>Xóa theo các nét cục tẩy: trong 70% bán kính xóa hẳn, phần ngoài mờ dần.</summary>
    public static void ApplyErase(Plane alpha, IEnumerable<EraseStroke> strokes, float scale = 1)
    {
        foreach (var s in strokes)
        {
            float cx = s.X * scale, cy = s.Y * scale, r = Math.Max(0.5f, s.Radius * scale);
            int x0 = Math.Max(0, (int)Math.Floor(cx - r)), x1 = Math.Min(alpha.Width - 1, (int)Math.Ceiling(cx + r));
            int y0 = Math.Max(0, (int)Math.Floor(cy - r)), y1 = Math.Min(alpha.Height - 1, (int)Math.Ceiling(cy + r));
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    var keep = Math.Clamp((d - 0.7f * r) / (0.3f * r), 0, 1);
                    alpha[x, y] *= keep;
                }
            }
        }
    }
}

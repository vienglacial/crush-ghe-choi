namespace CrushGheChoi.Core.Cutout;

/// <summary>Chuẩn bị đầu vào và đọc đầu ra của BiRefNet (bản ONNX của rembg): ảnh 1024², chuẩn hóa ImageNet.</summary>
public static class Segmentation
{
    public const int InputSize = 1024;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    /// <summary>Tensor NCHW [1, 3, 1024, 1024].</summary>
    public static float[] PrepareInput(RgbImage image)
    {
        var resized = ImageMath.Resample(image, InputSize, InputSize);
        Plane[] channels = [resized.R, resized.G, resized.B];

        // Giống rembg: chia cho giá trị sáng nhất của ảnh (thường là 1) trước khi chuẩn hóa.
        var max = 1e-6f;
        foreach (var c in channels)
        {
            foreach (var v in c.Data)
            {
                max = Math.Max(max, v);
            }
        }

        const int plane = InputSize * InputSize;
        var tensor = new float[3 * plane];
        for (var c = 0; c < 3; c++)
        {
            var src = channels[c].Data;
            for (var i = 0; i < plane; i++)
            {
                tensor[c * plane + i] = (src[i] / max - Mean[c]) / Std[c];
            }
        }

        return tensor;
    }

    /// <summary>Logit [1, 1, 1024, 1024] → mask 0..1 (sigmoid rồi kéo giãn về đủ 0..1).</summary>
    public static Plane ToMask(ReadOnlySpan<float> logits)
    {
        var mask = new Plane(InputSize, InputSize);
        float lo = float.MaxValue, hi = float.MinValue;
        for (var i = 0; i < mask.Data.Length; i++)
        {
            var v = 1f / (1f + MathF.Exp(-logits[i]));
            mask.Data[i] = v;
            lo = Math.Min(lo, v);
            hi = Math.Max(hi, v);
        }

        var range = Math.Max(hi - lo, 1e-6f);
        for (var i = 0; i < mask.Data.Length; i++)
        {
            mask.Data[i] = (mask.Data[i] - lo) / range;
        }

        return mask;
    }
}

/// <summary>Gương mặt tìm được: khung và 5 điểm (mắt phải, mắt trái, mũi, khóe miệng phải, khóe miệng trái).</summary>
public sealed record DetectedFace(float Score, RectF Box, IReadOnlyList<PointF> Landmarks)
{
    public PointF Mouth => new((Landmarks[3].X + Landmarks[4].X) / 2, (Landmarks[3].Y + Landmarks[4].Y) / 2);

    public DetectedFace Offset(float dx, float dy) => new(
        Score,
        Box with { X = Box.X + dx, Y = Box.Y + dy },
        Landmarks.Select(p => new PointF(p.X + dx, p.Y + dy)).ToArray());
}

/// <summary>Dò gương mặt bằng YuNet (OpenCV Zoo, bản 2023mar, đầu vào cố định 640×640, BGR 0..255).</summary>
public static class FaceDetection
{
    public const int InputSize = 640;

    public static readonly int[] Strides = [8, 16, 32];

    /// <summary>Thu ảnh vừa khung 640, đặt ở góc trên trái, phần thừa để đen. <paramref name="scale"/> = 640 / cạnh dài.</summary>
    public static float[] PrepareInput(RgbImage image, out float scale)
    {
        scale = InputSize / (float)Math.Max(image.Width, image.Height);
        int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
        var small = ImageMath.Resample(image, Math.Min(w, InputSize), Math.Min(h, InputSize));
        const int plane = InputSize * InputSize;
        var tensor = new float[3 * plane];
        Plane[] bgr = [small.B, small.G, small.R];
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < small.Height; y++)
            {
                for (var x = 0; x < small.Width; x++)
                {
                    tensor[c * plane + y * InputSize + x] = bgr[c][x, y] * 255f;
                }
            }
        }

        return tensor;
    }

    /// <summary>Giải mã đầu ra cls_*, obj_*, bbox_*, kps_* (theo FaceDetectorYN của OpenCV) rồi lọc trùng (NMS).</summary>
    public static List<DetectedFace> Decode(Func<string, float[]> output, float scale, float threshold = 0.6f, float nmsIou = 0.3f)
    {
        var faces = new List<DetectedFace>();
        foreach (var stride in Strides)
        {
            var cols = InputSize / stride;
            var cls = output($"cls_{stride}");
            var obj = output($"obj_{stride}");
            var bbox = output($"bbox_{stride}");
            var kps = output($"kps_{stride}");
            for (var i = 0; i < cls.Length; i++)
            {
                var score = MathF.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                if (score < threshold)
                {
                    continue;
                }

                int row = i / cols, col = i % cols;
                var cx = (col + bbox[i * 4]) * stride;
                var cy = (row + bbox[i * 4 + 1]) * stride;
                var w = MathF.Exp(bbox[i * 4 + 2]) * stride;
                var h = MathF.Exp(bbox[i * 4 + 3]) * stride;
                var points = new PointF[5];
                for (var n = 0; n < 5; n++)
                {
                    points[n] = new PointF((kps[i * 10 + 2 * n] + col) * stride / scale, (kps[i * 10 + 2 * n + 1] + row) * stride / scale);
                }

                faces.Add(new DetectedFace(score, new RectF((cx - w / 2) / scale, (cy - h / 2) / scale, w / scale, h / scale), points));
            }
        }

        var kept = new List<DetectedFace>();
        foreach (var f in faces.OrderByDescending(f => f.Score))
        {
            if (kept.All(k => Iou(k.Box, f.Box) <= nmsIou))
            {
                kept.Add(f);
            }
        }

        return kept;
    }

    /// <summary>Gương mặt chính: to nhất (ảnh chụp chung thì lấy người đứng gần máy nhất).</summary>
    public static DetectedFace? Main(IEnumerable<DetectedFace> faces) =>
        faces.OrderByDescending(f => f.Box.Width * f.Box.Height * f.Score).FirstOrDefault();

    private static float Iou(RectF a, RectF b)
    {
        var ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        var iy = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
        var inter = ix * iy;
        return inter / Math.Max(1e-6f, a.Width * a.Height + b.Width * b.Height - inter);
    }
}

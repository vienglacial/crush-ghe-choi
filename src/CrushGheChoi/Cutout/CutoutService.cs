using System.Runtime.InteropServices;
using CrushGheChoi.Core.Cutout;
using CrushGheChoi.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CrushGheChoi.Cutout;

/// <summary>
/// Tách nền ngay trên máy: dò gương mặt (YuNet) rồi tách người (BiRefNet), không gửi ảnh đi đâu.
/// Model tách nền cần khoảng 6 GB RAM khi chạy nên được giải phóng ngay khi xong một lượt.
/// </summary>
internal sealed class CutoutService : IDisposable
{
    /// <summary>RAM trống nên có trước khi tách nền (đo được khoảng 6 GB lúc cao nhất).</summary>
    public const long RecommendedFreeBytes = 6L * 1024 * 1024 * 1024;

    private InferenceSession? _segmenter;
    private InferenceSession? _faces;

    /// <summary>
    /// Chuẩn bị một ảnh thành bản nháp. Hình đã tách nền (PNG trong suốt) thì dùng luôn, chỉ dò gương mặt (dưới 1 giây).
    /// Ảnh còn nền thì tách bằng model nếu <paramref name="canSegment"/> (khoảng 45–90 giây trên CPU), không thì trả null.
    /// Chạy đồng bộ (gọi trong Task.Run).
    /// </summary>
    public CutoutDraft? Process(string path, bool canSegment, IProgress<string>? status, CancellationToken ct)
    {
        status?.Report("Đang đọc ảnh…");
        var (image, cutOut) = PhotoIO.LoadPhoto(path);
        ct.ThrowIfCancellationRequested();

        if (cutOut is not null)
        {
            status?.Report("Đang tìm gương mặt…");
            var found = DetectFace(CutoutPipeline.OverGray(image, cutOut));
            Log.Info($"Hình đã tách nền {System.IO.Path.GetFileName(path)}: {image.Width}×{image.Height}, {(found is null ? "không thấy mặt" : "có mặt")}");
            return CutoutPipeline.FromCutOut(image, cutOut, found);
        }

        if (!canSegment)
        {
            return null;
        }

        status?.Report("Đang tìm gương mặt…");
        var face = DetectFace(image);
        ct.ThrowIfCancellationRequested();

        status?.Report("Đang tách nền…");
        var mask = Segment(image, ct);

        PointF? seed = face is null ? null : new PointF(face.Box.CenterX, face.Box.CenterY);
        var region = CutoutPipeline.FindRegion(mask, seed);
        var rgb = image.Crop(region);
        Plane raw;
        if (CutoutPipeline.NeedsSecondPass(region, image.Width, image.Height))
        {
            // Người nhỏ trong khung: cho model nhìn sát vùng quanh người để mép tóc chi tiết hơn.
            status?.Report("Đang làm mịn mép tóc…");
            raw = Segment(rgb, ct);
        }
        else
        {
            raw = mask.Crop(region);
        }

        status?.Report("Đang hoàn thiện…");
        var regionFace = face?.Offset(-region.X, -region.Y);
        PointF? regionSeed = regionFace is null ? null : new PointF(regionFace.Box.CenterX, regionFace.Box.CenterY);
        var alpha = CutoutPipeline.Refine(rgb, raw, regionSeed);
        Log.Info($"Tách nền {System.IO.Path.GetFileName(path)}: {image.Width}×{image.Height}, vùng {region.Width}×{region.Height}, {(face is null ? "không thấy mặt" : "có mặt")}");
        return new CutoutDraft(rgb, alpha, regionFace);
    }

    /// <summary>Giải phóng model tách nền (vài GB) khi không còn ảnh nào chờ.</summary>
    public void ReleaseSegmenter()
    {
        _segmenter?.Dispose();
        _segmenter = null;
    }

    public void Dispose()
    {
        ReleaseSegmenter();
        _faces?.Dispose();
        _faces = null;
    }

    public static ulong AvailablePhysicalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.AvailPhys : ulong.MaxValue;
    }

    private static InferenceSession CreateSession(string path, bool large)
    {
        var options = new SessionOptions();
        if (large)
        {
            // Arena giữ lại bộ nhớ giữa các lần chạy; với model lớn sẽ hết RAM từ ảnh thứ hai.
            options.EnableCpuMemArena = false;
            options.EnableMemoryPattern = false;
        }

        return new InferenceSession(path, options);
    }

    private DetectedFace? DetectFace(RgbImage image)
    {
        _faces ??= CreateSession(ModelStore.FaceDetectorPath, large: false);
        var input = FaceDetection.PrepareInput(image, out var scale);
        var tensor = new DenseTensor<float>(input, [1, 3, FaceDetection.InputSize, FaceDetection.InputSize]);
        using var results = _faces.Run([NamedOnnxValue.CreateFromTensor(_faces.InputMetadata.Keys.First(), tensor)]);
        var outputs = results.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToArray());
        return FaceDetection.Main(FaceDetection.Decode(name => outputs[name], scale));
    }

    private Plane Segment(RgbImage image, CancellationToken ct)
    {
        _segmenter ??= CreateSession(ModelStore.SegmenterPath, large: true);
        var input = Segmentation.PrepareInput(image);
        var tensor = new DenseTensor<float>(input, [1, 3, Segmentation.InputSize, Segmentation.InputSize]);

        // Một lượt chạy mất cả chục giây: cho dừng giữa chừng khi đóng cửa sổ.
        using var run = new RunOptions();
        using var stop = ct.Register(() => run.Terminate = true);
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
        try
        {
            results = _segmenter.Run(
                [NamedOnnxValue.CreateFromTensor(_segmenter.InputMetadata.Keys.First(), tensor)],
                [_segmenter.OutputMetadata.Keys.First()],
                run);
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        using var _ = results;
        ct.ThrowIfCancellationRequested();
        var logits = results.First().AsTensor<float>();
        var mask = logits is DenseTensor<float> dense
            ? Segmentation.ToMask(dense.Buffer.Span)
            : Segmentation.ToMask(logits.ToArray());
        return ImageMath.Resample(mask, image.Width, image.Height);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

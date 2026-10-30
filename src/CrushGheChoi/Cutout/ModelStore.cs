using System.IO;
using System.Security.Cryptography;
using CrushGheChoi.Services;

namespace CrushGheChoi.Cutout;

/// <summary>Một tệp model: nơi tải, kích thước và mã SHA-256 để kiểm tra tệp tải về còn nguyên vẹn.</summary>
internal sealed record ModelFile(string FileName, Uri Url, long Size, string Sha256);

/// <summary>
/// Model tách nền (BiRefNet-portrait, khoảng 1 GB) không đi kèm app và app cũng không tự tải: ai muốn app tự tách nền
/// thì tự tải tệp về (app chỉ mở trang tải bằng trình duyệt) rồi chọn tệp đó; app kiểm tra mã SHA-256 rồi chép vào
/// thư mục models. Không có model thì người dùng thêm hình PNG đã tự tách nền.
/// Model dò gương mặt (YuNet, 230 KB) đi kèm sẵn trong thư mục Assets/Models.
/// </summary>
internal static class ModelStore
{
    public static readonly ModelFile Segmenter = new(
        "BiRefNet-portrait-epoch_150.onnx",
        new Uri("https://github.com/danielgatis/rembg/releases/download/v0.0.0/BiRefNet-portrait-epoch_150.onnx"),
        972_666_916,
        "1ba1c8ff5a7bbfadc8d8d13fb11d7be793f91f23d9d466549e37a854f6668f99");

    public static string SegmenterPath => Path.Combine(AppPaths.ModelDirectory, Segmenter.FileName);

    public static string FaceDetectorPath => Path.Combine(AppPaths.BundledModelDirectory, "face_detection_yunet_2023mar.onnx");

    /// <summary>Đã cài model tách nền (đúng kích thước; mã SHA-256 được kiểm tra lúc cài).</summary>
    public static bool SegmenterReady => File.Exists(SegmenterPath) && new FileInfo(SegmenterPath).Length == Segmenter.Size;

    /// <summary>Cài tệp model người dùng đã tự tải (bằng trình duyệt): kiểm tra rồi chép vào thư mục models.</summary>
    public static async Task ImportAsync(string file, CancellationToken ct)
    {
        var length = new FileInfo(file).Length;
        if (length != Segmenter.Size)
        {
            throw new InvalidDataException($"Tệp model không đúng kích thước ({length:N0} byte, cần {Segmenter.Size:N0}).");
        }

        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (hash != Segmenter.Sha256)
            {
                throw new InvalidDataException("Tệp model bị hỏng hoặc không phải bản đúng (mã kiểm tra không khớp). Hãy tải lại.");
            }
        }

        Directory.CreateDirectory(AppPaths.ModelDirectory);
        var tmp = SegmenterPath + ".copy";
        await using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            await src.CopyToAsync(dst, ct);
        }

        File.Move(tmp, SegmenterPath, overwrite: true);
        Log.Info("Đã cài model tách nền");
    }
}

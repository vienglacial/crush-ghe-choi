using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrushGheChoi.Core.Animation;

/// <summary>Các động tác một bộ hình có thể cung cấp dưới dạng chuỗi PNG.</summary>
public enum ClipKind
{
    Idle,
    Wave,
    KissPrepare,
    Kiss,
    Goodbye,
}

/// <summary>Điểm trên khung hình, tính theo tỷ lệ 0..1 từ góc trên trái.</summary>
public readonly record struct NormalizedPoint(double X, double Y);

public sealed record ClipDefinition(
    ClipKind Kind,
    IReadOnlyList<string> FramePaths,
    bool Loop,
    int? KissReleaseFrame,
    NormalizedPoint? KissPoint)
{
    public double Duration(double fps) => FramePaths.Count / fps;
}

/// <summary>
/// Một ảnh đứng của bộ hình. <paramref name="HeartDrift"/> là -1/1 nếu bộ hình chỉ định hướng tim bay lệch,
/// null thì app tự chọn phía có bàn tay (theo điểm hun).
/// </summary>
public sealed record StillVariant(string Path, int Width, int Height, NormalizedPoint KissPoint, int? HeartDrift = null);

public sealed record AnimationSetDefinition(
    string Name,
    string Version,
    string RootDirectory,
    string StillPath,
    int FrameWidth,
    int FrameHeight,
    double Fps,
    NormalizedPoint KissPoint,
    IReadOnlyDictionary<ClipKind, ClipDefinition> Clips,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<StillVariant>? Variants = null)
{
    public bool IsStillOnly => Clips.Count == 0;

    /// <summary>Ảnh đứng chính: cùng kích thước và điểm neo với các clip chuyển động.</summary>
    public StillVariant Primary => Stills[0];

    /// <summary>Mọi ảnh đứng, ảnh chính đứng đầu. Bộ chỉ có ảnh đứng thì mỗi lần ghé bốc một ảnh.</summary>
    public IReadOnlyList<StillVariant> Stills =>
        Variants is { Count: > 0 } v ? v : [new StillVariant(StillPath, FrameWidth, FrameHeight, KissPoint)];

    public ClipDefinition? Clip(ClipKind kind) => Clips.GetValueOrDefault(kind);
}

public sealed class AnimationSetException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Đọc <c>manifest.json</c> của một bộ hình. Ảnh đứng là bắt buộc; động tác nào thiếu hoặc lỗi thì bỏ qua
/// kèm cảnh báo và app tự tạo chuyển động từ ảnh đứng.
/// </summary>
public static class AnimationSetLoader
{
    public const string ManifestFileName = "manifest.json";
    public const int MaxFramesPerClip = 600;
    public const int MaxStills = 200;
    public const double DefaultFps = 24;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AnimationSetDefinition Load(string directory)
    {
        var root = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new AnimationSetException($"Không thấy {ManifestFileName} trong thư mục bộ hình “{root}”.");
        }

        ManifestDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new AnimationSetException($"{ManifestFileName} rỗng.");
        }
        catch (JsonException ex)
        {
            throw new AnimationSetException($"{ManifestFileName} không đọc được: {ex.Message}", ex);
        }

        var stillPath = ResolveInside(root, dto.Still ?? dto.Stills?.FirstOrDefault()?.File ?? "still.png")
            ?? throw new AnimationSetException("Đường dẫn ảnh đứng phải nằm trong thư mục bộ hình.");
        if (!File.Exists(stillPath))
        {
            throw new AnimationSetException($"Thiếu ảnh đứng “{Path.GetFileName(stillPath)}”.");
        }

        if (!PngInfo.TryReadSize(stillPath, out var width, out var height) || width < 16 || height < 16)
        {
            throw new AnimationSetException("Ảnh đứng phải là tệp PNG hợp lệ (nên có nền trong suốt).");
        }

        var warnings = new List<string>();
        var fps = dto.Fps is > 0 and <= 60 ? dto.Fps.Value : DefaultFps;
        var primaryEntry = dto.Stills?.FirstOrDefault(s => s.File is not null && ResolveInside(root, s.File) is { } p
            && string.Equals(p, stillPath, StringComparison.OrdinalIgnoreCase));
        var kissPoint = ToPoint(primaryEntry?.KissPoint) ?? ToPoint(dto.KissPoint) ?? new NormalizedPoint(0.5, 0.3);
        var variants = LoadStills(root, dto.Stills, new StillVariant(stillPath, width, height, kissPoint, ToDrift(primaryEntry?.Drift)), warnings);
        var clips = new Dictionary<ClipKind, ClipDefinition>();

        foreach (var (key, clip) in dto.Clips ?? [])
        {
            if (!TryParseKind(key, out var kind))
            {
                warnings.Add($"Bỏ qua động tác lạ “{key}”.");
                continue;
            }

            var clipDef = LoadClip(root, kind, clip, width, height, warnings);
            if (clipDef is not null)
            {
                clips[kind] = clipDef;
            }
        }

        return new AnimationSetDefinition(
            string.IsNullOrWhiteSpace(dto.Name) ? Path.GetFileName(root) : dto.Name.Trim(),
            string.IsNullOrWhiteSpace(dto.Version) ? "1" : dto.Version.Trim(),
            root,
            stillPath,
            width,
            height,
            fps,
            kissPoint,
            clips,
            warnings,
            variants);
    }

    private static List<StillVariant> LoadStills(string root, List<StillDto>? entries, StillVariant primary, List<string> warnings)
    {
        var variants = new List<StillVariant> { primary };
        foreach (var entry in entries ?? [])
        {
            if (variants.Count >= MaxStills)
            {
                warnings.Add($"Chỉ dùng {MaxStills} ảnh đứng đầu tiên.");
                break;
            }

            var path = string.IsNullOrWhiteSpace(entry.File) ? null : ResolveInside(root, entry.File);
            if (path is null || variants.Any(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!File.Exists(path) || !PngInfo.TryReadSize(path, out var w, out var h) || w < 16 || h < 16)
            {
                warnings.Add($"Bỏ qua ảnh “{entry.File}”: thiếu tệp hoặc không phải PNG hợp lệ.");
                continue;
            }

            variants.Add(new StillVariant(path, w, h, ToPoint(entry.KissPoint) ?? new NormalizedPoint(0.5, 0.3), ToDrift(entry.Drift)));
        }

        return variants;
    }

    private static int? ToDrift(int? drift) => drift is -1 or 1 ? drift : null;

    private static ClipDefinition? LoadClip(string root, ClipKind kind, ClipDto clip, int width, int height, List<string> warnings)
    {
        var label = kind.ToString();
        var dir = string.IsNullOrWhiteSpace(clip.Frames) ? null : ResolveInside(root, clip.Frames);
        if (dir is null || !Directory.Exists(dir))
        {
            warnings.Add($"Động tác {label}: không thấy thư mục khung hình “{clip.Frames}”.");
            return null;
        }

        var frames = Directory.EnumerateFiles(dir, "*.png")
            .Order(NaturalStringComparer.Instance)
            .ToList();
        if (frames.Count == 0)
        {
            warnings.Add($"Động tác {label}: thư mục “{clip.Frames}” không có tệp PNG.");
            return null;
        }

        if (frames.Count > MaxFramesPerClip)
        {
            warnings.Add($"Động tác {label}: chỉ dùng {MaxFramesPerClip} khung đầu.");
            frames = frames.Take(MaxFramesPerClip).ToList();
        }

        // Kiểm tra khung đầu, giữa và cuối; toàn bộ bộ hình phải cùng kích thước và điểm neo.
        foreach (var probe in new[] { frames[0], frames[frames.Count / 2], frames[^1] })
        {
            if (!PngInfo.TryReadSize(probe, out var w, out var h) || w != width || h != height)
            {
                warnings.Add($"Động tác {label}: “{Path.GetFileName(probe)}” không cùng kích thước {width}×{height} với ảnh đứng.");
                return null;
            }
        }

        int? release = null;
        if (clip.Events is not null && clip.Events.TryGetValue("kiss_release", out var frame))
        {
            release = Math.Clamp(frame, 0, frames.Count - 1);
        }
        else if (kind == ClipKind.Kiss)
        {
            warnings.Add("Động tác Kiss chưa có mốc kiss_release; tim sẽ bay ra ở khoảng 1/3 clip.");
        }

        return new ClipDefinition(kind, frames, clip.Loop || kind == ClipKind.Idle, release, ToPoint(clip.KissPoint));
    }

    private static bool TryParseKind(string key, out ClipKind kind)
    {
        var compact = key.Replace("_", string.Empty).Replace("-", string.Empty);
        return Enum.TryParse(compact, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    private static NormalizedPoint? ToPoint(PointDto? p) =>
        p is { X: >= 0 and <= 1, Y: >= 0 and <= 1 } ? new NormalizedPoint(p.X, p.Y) : null;

    /// <summary>Ghép đường dẫn tương đối và từ chối mọi đường thoát ra ngoài thư mục bộ hình.</summary>
    private static string? ResolveInside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private sealed class ManifestDto
    {
        public int Schema { get; set; } = 1;

        public string? Name { get; set; }

        public string? Version { get; set; }

        public string? Still { get; set; }

        public double? Fps { get; set; }

        public PointDto? KissPoint { get; set; }

        public List<StillDto>? Stills { get; set; }

        public Dictionary<string, ClipDto>? Clips { get; set; }
    }

    private sealed class StillDto
    {
        public string? File { get; set; }

        public PointDto? KissPoint { get; set; }

        public int? Drift { get; set; }
    }

    private sealed class ClipDto
    {
        public string? Frames { get; set; }

        public bool Loop { get; set; }

        public Dictionary<string, int>? Events { get; set; }

        public PointDto? KissPoint { get; set; }
    }

    private sealed class PointDto
    {
        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }
    }
}

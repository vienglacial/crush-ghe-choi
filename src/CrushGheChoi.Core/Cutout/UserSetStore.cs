using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CrushGheChoi.Core.Animation;

namespace CrushGheChoi.Core.Cutout;

/// <summary>Một hình trong bộ "Crush của tôi".</summary>
public sealed record UserPhoto(string Id, string File, NormalizedPoint KissPoint, int? Drift);

/// <summary>
/// Bộ hình người dùng tự thêm: <c>stills/&lt;id&gt;.png</c> và <c>manifest.json</c> đúng định dạng bộ hình thường,
/// cộng bản nháp trong <c>drafts/</c> để mở chỉnh lại mà không phải tách nền lại.
/// </summary>
public sealed class UserSetStore(string directory)
{
    public const string SetName = "Crush của tôi";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string Directory { get; } = Path.GetFullPath(directory);

    public string ManifestPath => Path.Combine(Directory, AnimationSetLoader.ManifestFileName);

    public string StillPath(string id) => Path.Combine(Directory, "stills", id + ".png");

    public string DraftImagePath(string id) => Path.Combine(Directory, "drafts", id + ".png");

    public string DraftEditsPath(string id) => Path.Combine(Directory, "drafts", id + ".json");

    public bool HasDraft(string id) => File.Exists(DraftImagePath(id)) && File.Exists(DraftEditsPath(id));

    /// <summary>Các hình theo thứ tự trong manifest; hình mất tệp thì bỏ qua.</summary>
    public IReadOnlyList<UserPhoto> Load()
    {
        if (!File.Exists(ManifestPath))
        {
            return [];
        }

        ManifestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(ManifestPath), Json);
        }
        catch (JsonException)
        {
            return [];
        }

        var photos = new List<UserPhoto>();
        foreach (var s in dto?.Stills ?? [])
        {
            if (string.IsNullOrWhiteSpace(s.File) || s.File.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            var id = Path.GetFileNameWithoutExtension(s.File);
            if (photos.Any(p => p.Id == id) || !File.Exists(Path.Combine(Directory, s.File)))
            {
                continue;
            }

            photos.Add(new UserPhoto(id, s.File.Replace('\\', '/'), s.KissPoint is { } k ? new NormalizedPoint(k.X, k.Y) : new NormalizedPoint(0.5, 0.3), s.Drift is -1 or 1 ? s.Drift : null));
        }

        return photos;
    }

    /// <summary>Thêm một hình. <paramref name="writeStill"/> ghi tệp PNG vào đường dẫn được đưa.</summary>
    public UserPhoto Add(Action<string> writeStill, NormalizedPoint kissPoint, int drift, DateTime now)
    {
        var photos = Load().ToList();
        var id = NewId(now, photos.Select(p => p.Id));
        WriteFile(StillPath(id), writeStill);
        var photo = new UserPhoto(id, $"stills/{id}.png", kissPoint, drift);
        photos.Add(photo);
        Save(photos);
        return photo;
    }

    public void Update(string id, Action<string> writeStill, NormalizedPoint kissPoint, int drift)
    {
        var photos = Load().ToList();
        var index = photos.FindIndex(p => p.Id == id);
        if (index < 0)
        {
            throw new InvalidOperationException("Hình này không còn trong bộ.");
        }

        WriteFile(StillPath(id), writeStill);
        photos[index] = photos[index] with { KissPoint = kissPoint, Drift = drift };
        Save(photos);
    }

    /// <summary>Xóa một hình cùng bản nháp. Hết hình thì xóa luôn manifest để app quay về hình mẫu.</summary>
    public void Remove(string id)
    {
        var photos = Load().ToList();
        var photo = photos.FirstOrDefault(p => p.Id == id);
        if (photo is null)
        {
            return;
        }

        photos.Remove(photo);
        Save(photos);
        foreach (var path in new[] { Path.Combine(Directory, photo.File), DraftImagePath(id), DraftEditsPath(id) })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public void SaveDraft(string id, Action<string> writeImage, CutoutEdits edits, DetectedFace? face)
    {
        WriteFile(DraftImagePath(id), writeImage);
        var dto = new DraftDto
        {
            BottomCut = edits.BottomCut,
            KissAt = [edits.KissAt.X, edits.KissAt.Y],
            Drift = edits.Drift,
            Erase = edits.Erase.Select(e => new[] { e.X, e.Y, e.Radius }).ToList(),
            Face = face is null ? null : new FaceDto
            {
                Score = face.Score,
                Box = [face.Box.X, face.Box.Y, face.Box.Width, face.Box.Height],
                Landmarks = face.Landmarks.SelectMany(p => new[] { p.X, p.Y }).ToArray(),
            },
        };
        WriteFile(DraftEditsPath(id), path => File.WriteAllText(path, JsonSerializer.Serialize(dto, Json), Utf8NoBom));
    }

    public (CutoutEdits Edits, DetectedFace? Face)? LoadDraftEdits(string id)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<DraftDto>(File.ReadAllText(DraftEditsPath(id)), Json);
            if (dto?.KissAt is not { Length: 2 } kiss)
            {
                return null;
            }

            var erase = (dto.Erase ?? []).Where(e => e.Length == 3).Select(e => new EraseStroke(e[0], e[1], e[2])).ToList();
            DetectedFace? face = null;
            if (dto.Face is { Box.Length: 4, Landmarks.Length: 10 } f)
            {
                var points = Enumerable.Range(0, 5).Select(n => new PointF(f.Landmarks[2 * n], f.Landmarks[2 * n + 1])).ToArray();
                face = new DetectedFace(f.Score, new RectF(f.Box[0], f.Box[1], f.Box[2], f.Box[3]), points);
            }

            return (new CutoutEdits(dto.BottomCut, new PointF(kiss[0], kiss[1]), dto.Drift < 0 ? -1 : 1, erase), face);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Tên tệp theo thời điểm thêm, thêm hậu tố nếu trùng.</summary>
    public static string NewId(DateTime now, IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = now.ToString("yyyyMMdd-HHmmss-fff");
        for (var n = 2; taken.Contains(id); n++)
        {
            id = $"{now:yyyyMMdd-HHmmss-fff}-{n}";
        }

        return id;
    }

    private void Save(List<UserPhoto> photos)
    {
        if (photos.Count == 0)
        {
            if (File.Exists(ManifestPath))
            {
                File.Delete(ManifestPath);
            }

            return;
        }

        var dto = new ManifestDto
        {
            Schema = 1,
            Name = SetName,
            Version = $"{photos.Count}-{DateTime.Now:yyyyMMddHHmmss}",
            Still = photos[0].File,
            Stills = photos.Select(p => new StillDto { File = p.File, KissPoint = new PointDto { X = p.KissPoint.X, Y = p.KissPoint.Y }, Drift = p.Drift }).ToList(),
        };
        WriteFile(ManifestPath, path => File.WriteAllText(path, JsonSerializer.Serialize(dto, Json), Utf8NoBom));
    }

    /// <summary>Ghi ra tệp tạm rồi đổi tên, để tệp không bao giờ bị ghi dở.</summary>
    private static void WriteFile(string path, Action<string> write)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        write(tmp);
        File.Move(tmp, path, overwrite: true);
    }

    private sealed class ManifestDto
    {
        public int Schema { get; set; } = 1;

        public string? Name { get; set; }

        public string? Version { get; set; }

        public string? Still { get; set; }

        public List<StillDto>? Stills { get; set; }
    }

    private sealed class StillDto
    {
        public string? File { get; set; }

        public PointDto? KissPoint { get; set; }

        public int? Drift { get; set; }
    }

    private sealed class PointDto
    {
        public double X { get; set; }

        public double Y { get; set; }
    }

    private sealed class DraftDto
    {
        public int BottomCut { get; set; }

        public float[]? KissAt { get; set; }

        public int Drift { get; set; }

        public List<float[]>? Erase { get; set; }

        public FaceDto? Face { get; set; }
    }

    private sealed class FaceDto
    {
        public float Score { get; set; }

        public float[]? Box { get; set; }

        public float[]? Landmarks { get; set; }
    }
}

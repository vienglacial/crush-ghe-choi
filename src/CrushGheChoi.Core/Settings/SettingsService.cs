using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrushGheChoi.Core.Settings;

public static class SettingsSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },

        // Giữ tiếng Việt và emoji đọc được khi mở tệp bằng tay.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static AppSettings Deserialize(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, Options)
        ?? throw new InvalidDataException("Tệp cấu hình rỗng.");
}

public enum SettingsLoadStatus
{
    Loaded,
    CreatedDefault,
    RecoveredFromCorrupt,
}

public sealed record SettingsLoadResult(
    AppSettings Settings,
    SettingsLoadStatus Status,
    string? BackupPath,
    IReadOnlyList<string> Fixes);

/// <summary>Đọc, lưu, sao lưu và khôi phục cấu hình. Tệp hỏng thì giữ lại một bản rồi dùng mặc định.</summary>
public sealed class SettingsService(string filePath)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string FilePath { get; } = filePath;

    public SettingsLoadResult Load(DateTime now)
    {
        if (!File.Exists(FilePath))
        {
            return new(new AppSettings(), SettingsLoadStatus.CreatedDefault, null, []);
        }

        try
        {
            var settings = Migrate(SettingsSerializer.Deserialize(File.ReadAllText(FilePath)));
            var fixes = settings.Normalize(now);
            return new(settings, SettingsLoadStatus.Loaded, null, fixes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or IOException)
        {
            var fresh = new AppSettings();

            // Đã cài đặt xong lần đầu thì không bắt người dùng làm lại màn giới thiệu.
            fresh.FirstRunCompleted = true;
            return new(fresh, SettingsLoadStatus.RecoveredFromCorrupt, BackupCorrupt(), []);
        }
    }

    public void Save(AppSettings settings) => WriteAtomic(FilePath, SettingsSerializer.Serialize(settings));

    public static void Export(AppSettings settings, string path) => WriteAtomic(path, SettingsSerializer.Serialize(settings));

    /// <summary>Đọc một bản sao lưu. Tệp không hợp lệ thì ném <see cref="InvalidDataException"/>.</summary>
    public static AppSettings Import(string path, DateTime now)
    {
        AppSettings settings;
        try
        {
            settings = Migrate(SettingsSerializer.Deserialize(File.ReadAllText(path)));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Tệp này không phải bản sao cấu hình hợp lệ.", ex);
        }

        settings.Normalize(now);
        return settings;
    }

    /// <summary>Câu chào mặc định đã bỏ, kèm câu thay thế, để cấu hình đã lưu từ bản cũ cũng được đổi.</summary>
    private static readonly Dictionary<string, string> RetiredGreetings = new()
    {
        ["Cố lên nha, sắp xong rồi đó!"] = "Tui ghé ngang thôi, làm tiếp đi nè.",
    };

    private static AppSettings Migrate(AppSettings settings)
    {
        // Schema 1 là bản đầu tiên. Các bản sau thêm bước nâng cấp ở đây, theo thứ tự phiên bản.
        if (settings.Greetings is { } greetings)
        {
            settings.Greetings = greetings
                // Phần tử null (tệp sửa tay) để nguyên; Normalize sẽ bỏ đi.
                .Select(g => g is not null && RetiredGreetings.TryGetValue(g.Trim(), out var replacement) ? replacement : g!)
                .Distinct()
                .ToList();
        }

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return settings;
    }

    private static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }

    private string? BackupCorrupt()
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
            var backup = Path.Combine(dir, $"settings.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(FilePath, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

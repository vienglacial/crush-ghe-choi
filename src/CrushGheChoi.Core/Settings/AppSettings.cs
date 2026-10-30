using System.Text.Json.Serialization;
using CrushGheChoi.Core.Scheduling;

namespace CrushGheChoi.Core.Settings;

public enum ScreenCorner
{
    BottomLeft,
    BottomRight,
}

public enum PlacementMode
{
    /// <summary>Mỗi lần ghé một chỗ ngẫu nhiên trên vùng làm việc của màn hình.</summary>
    Random,

    /// <summary>Luôn ở góc đã chọn (kèm khoảng cách tới mép).</summary>
    Corner,
}

/// <summary>Một mức tần suất chọn nhanh: crush ghé ngẫu nhiên trong khoảng [Min, Max] phút.</summary>
public sealed record IntervalPreset(string Label, int MinMinutes, int MaxMinutes);

public enum DisplayMode
{
    /// <summary>Hiện trên cửa sổ đang làm việc vài giây rồi đi.</summary>
    Visit,

    /// <summary>Chỉ hiện khi desktop đang ở phía trước.</summary>
    DesktopOnly,
}

public enum HeartPalette
{
    PinkRed,
    Purple,
    Pastel,
}

/// <summary>Toàn bộ tùy chọn được lưu xuống đĩa. Thêm trường mới thì tăng <see cref="CurrentSchemaVersion"/> khi cần nâng cấp dữ liệu.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public const double MinHeightDip = 160;
    public const double MaxHeightDip = 560;
    public const double DefaultHeightDip = 300;
    public const double SmallHeightDip = 220;
    public const double LargeHeightDip = 400;

    public const int MaxHearts = 8;
    public const int MaxGreetings = 50;
    public const int MaxGreetingLength = 140;
    public const double MaxEdgeOffsetDip = 4000;

    /// <summary>Giá trị <see cref="MonitorDeviceName"/> nghĩa là mỗi lần ghé chọn ngẫu nhiên một màn hình.</summary>
    public const string AnyMonitor = "*";

    public static IReadOnlyList<IntervalPreset> IntervalPresets { get; } =
    [
        new("5–15 phút", 5, 15),
        new("15–30 phút", 15, 30),
        new("30–60 phút", 30, 60),
        new("45–90 phút", 45, 90),
        new("1–2 giờ", 60, 120),
    ];

    public static IReadOnlyList<string> DefaultGreetings { get; } =
    [
        "Hé lô, tui ghé xíu nè.",
        "Làm việc tiếp nha, gửi bạn một cái tim.",
        "Nghỉ mắt chút đi nè.",
        "Có người đang cổ vũ bạn đó 💗",
        "Hun gió một cái rồi tui đi.",
        "Hé lô, đang làm gì đó?",
        "Uống miếng nước rồi làm tiếp nha.",
        "Ngồi thẳng lưng lên nè, hihi.",
        "Tui ghé coi bạn có ổn không nè.",
        "Tui ghé ngang thôi, làm tiếp đi nè.",
    ];

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // Crush
    /// <summary>Thư mục bộ hình; null là bộ có sẵn trong app.</summary>
    public string? AnimationSetPath { get; set; }

    public string? AnimationSetVersion { get; set; }

    public double HeightDip { get; set; } = DefaultHeightDip;

    // Ghé chơi
    public bool AutoVisitEnabled { get; set; } = true;

    public int MinIntervalMinutes { get; set; } = 45;

    public int MaxIntervalMinutes { get; set; } = 90;

    public bool QuietHoursEnabled { get; set; } = true;

    public TimeOnly QuietStart { get; set; } = new(22, 30);

    public TimeOnly QuietEnd { get; set; } = new(8, 30);

    public DateTime? PausedUntil { get; set; }

    public DateTime? LastAutoVisit { get; set; }

    public List<string> Greetings { get; set; } = [.. DefaultGreetings];

    // Màn hình
    /// <summary>Tên thiết bị màn hình (ví dụ <c>\\.\DISPLAY2</c>); null là màn hình chính, <see cref="AnyMonitor"/> là ngẫu nhiên.</summary>
    public string? MonitorDeviceName { get; set; }

    public PlacementMode Placement { get; set; } = PlacementMode.Random;

    public ScreenCorner Corner { get; set; } = ScreenCorner.BottomRight;

    /// <summary>Khoảng cách (DIP) từ mép cửa sổ crush tới mép trái/phải vùng làm việc, tùy góc.</summary>
    public double EdgeOffsetXDip { get; set; } = 8;

    /// <summary>Khoảng cách (DIP) từ mép dưới cửa sổ crush tới mép dưới vùng làm việc.</summary>
    public double EdgeOffsetYDip { get; set; }

    public DisplayMode DisplayMode { get; set; } = DisplayMode.Visit;

    /// <summary>Hoãn lần ghé tự động khi có ứng dụng toàn màn hình (trình chiếu, game, video).</summary>
    public bool DeferOnFullScreen { get; set; } = true;

    /// <summary>
    /// Tên tiến trình (không có .exe) vẫn cho crush ghé dù đang phủ kín màn hình.
    /// Các app dựng phim/đồ họa hay chạy toàn màn hình khi làm việc bình thường.
    /// </summary>
    public List<string> FullScreenAllowedApps { get; set; } = [.. DefaultFullScreenAllowedApps];

    public static IReadOnlyList<string> DefaultFullScreenAllowedApps { get; } =
        ["Adobe Premiere Pro", "AfterFX", "Photoshop", "Illustrator", "Resolve", "CapCut"];

    // Hiệu ứng
    public HeartPalette HeartPalette { get; set; } = HeartPalette.PinkRed;

    public int HeartCount { get; set; } = 4;

    public bool ReducedMotion { get; set; }

    // Ứng dụng
    public bool SoundEnabled { get; set; }

    public bool RunAtStartup { get; set; }

    public bool FirstRunCompleted { get; set; }

    [JsonIgnore]
    public QuietHours QuietHours => new(QuietHoursEnabled, QuietStart, QuietEnd);

    public ScheduleSettings ToSchedule() => new(
        AutoVisitEnabled,
        MinIntervalMinutes,
        MaxIntervalMinutes,
        QuietHours,
        PausedUntil,
        LastAutoVisit);

    public AppSettings Clone() => SettingsSerializer.Deserialize(SettingsSerializer.Serialize(this));

    /// <summary>Đưa mọi giá trị về khoảng hợp lệ. Trả về tên các mục đã phải sửa.</summary>
    public IReadOnlyList<string> Normalize(DateTime now)
    {
        var fixes = new List<string>();

        if (!double.IsFinite(HeightDip) || HeightDip is < MinHeightDip or > MaxHeightDip)
        {
            HeightDip = double.IsFinite(HeightDip) ? Math.Clamp(HeightDip, MinHeightDip, MaxHeightDip) : DefaultHeightDip;
            fixes.Add("kích thước");
        }

        var (min, max) = VisitPlanner.ClampInterval(MinIntervalMinutes, MaxIntervalMinutes);
        if (min != MinIntervalMinutes || max != MaxIntervalMinutes)
        {
            (MinIntervalMinutes, MaxIntervalMinutes) = (min, max);
            fixes.Add("khoảng thời gian ghé");
        }

        if (!Enum.IsDefined(Corner))
        {
            Corner = ScreenCorner.BottomRight;
            fixes.Add("góc xuất hiện");
        }

        if (!Enum.IsDefined(Placement))
        {
            Placement = PlacementMode.Random;
            fixes.Add("vị trí xuất hiện");
        }

        if (!Enum.IsDefined(DisplayMode))
        {
            DisplayMode = DisplayMode.Visit;
            fixes.Add("chế độ hiển thị");
        }

        if (!Enum.IsDefined(HeartPalette))
        {
            HeartPalette = HeartPalette.PinkRed;
            fixes.Add("màu tim");
        }

        EdgeOffsetXDip = ClampOffset(EdgeOffsetXDip, fixes);
        EdgeOffsetYDip = ClampOffset(EdgeOffsetYDip, fixes);

        if (HeartCount is < 1 or > MaxHearts)
        {
            HeartCount = Math.Clamp(HeartCount, 1, MaxHearts);
            fixes.Add("số tim");
        }

        var cleaned = (Greetings ?? [])
            .Select(g => (g ?? string.Empty).Trim())
            .Where(g => g.Length > 0)
            .Select(g => g.Length > MaxGreetingLength ? g[..MaxGreetingLength] : g)
            .Take(MaxGreetings)
            .ToList();
        if (cleaned.Count == 0)
        {
            cleaned = [.. DefaultGreetings];
        }

        if (Greetings is null || !cleaned.SequenceEqual(Greetings))
        {
            fixes.Add("câu chào");
        }

        Greetings = cleaned;

        FullScreenAllowedApps = (FullScreenAllowedApps ?? [])
            .Select(a => (a ?? string.Empty).Trim())
            .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();

        if (PausedUntil is { } paused && (paused <= now || paused > now.AddDays(8)))
        {
            PausedUntil = null;
        }

        // Đồng hồ máy bị chỉnh lùi thì mốc này vô nghĩa.
        if (LastAutoVisit > now)
        {
            LastAutoVisit = null;
        }

        if (string.IsNullOrWhiteSpace(AnimationSetPath))
        {
            AnimationSetPath = null;
        }

        if (string.IsNullOrWhiteSpace(MonitorDeviceName))
        {
            MonitorDeviceName = null;
        }

        SchemaVersion = CurrentSchemaVersion;
        return fixes;
    }

    private static double ClampOffset(double value, List<string> fixes)
    {
        if (double.IsFinite(value) && value is >= 0 and <= MaxEdgeOffsetDip)
        {
            return value;
        }

        fixes.Add("vị trí");
        return double.IsFinite(value) ? Math.Clamp(value, 0, MaxEdgeOffsetDip) : 0;
    }
}

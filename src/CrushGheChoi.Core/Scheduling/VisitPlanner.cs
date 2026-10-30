namespace CrushGheChoi.Core.Scheduling;

/// <summary>Các tùy chọn lịch mà bộ lập lịch cần, tách khỏi <c>AppSettings</c> để dễ kiểm thử.</summary>
public readonly record struct ScheduleSettings(
    bool AutoEnabled,
    int MinIntervalMinutes,
    int MaxIntervalMinutes,
    QuietHours Quiet,
    DateTime? PausedUntil,
    DateTime? LastAutoVisit);

/// <summary>Trạng thái máy tại lúc tới hẹn.</summary>
public readonly record struct VisitConditions(
    bool SessionLocked = false,
    bool FullScreenActive = false,
    bool DesktopOnly = false,
    bool DesktopInFront = true,
    bool VisitInProgress = false);

public enum TickOutcome
{
    /// <summary>Chưa tới giờ.</summary>
    Waiting,

    /// <summary>Tới giờ và đủ điều kiện: cho crush ghé.</summary>
    Play,

    /// <summary>Tới giờ nhưng chưa tiện; hẹn lại một lúc sau.</summary>
    Deferred,

    /// <summary>Đã tính một lịch mới (lần đầu, sau khi bỏ lỡ hoặc đang tạm nghỉ).</summary>
    Rescheduled,

    /// <summary>Lịch tự động đang tắt.</summary>
    Disabled,
}

/// <summary>
/// Tính lần ghé tiếp theo và quyết định tại mỗi nhịp kiểm tra có phát hay không.
/// Không giữ hàng đợi: lần nào bỏ lỡ thì tính lịch mới, không phát bù.
/// </summary>
public sealed class VisitPlanner
{
    public const int MinimumIntervalMinutes = 1;
    public const int MaximumIntervalMinutes = 600;

    /// <summary>Sau lần ghé do người dùng bấm, lịch tự động đợi ít nhất chừng này (hoặc khoảng ngắn nhất nếu nhỏ hơn).</summary>
    public static readonly TimeSpan MaximumGapAfterManualVisit = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MissedTolerance = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan DeferWhenBusy = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DeferWhenNotOnDesktop = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DeferWhenPlaying = TimeSpan.FromMinutes(1);

    private readonly Random _rng;

    public VisitPlanner(Random? rng = null) => _rng = rng ?? Random.Shared;

    public DateTime? NextDue { get; private set; }

    public DateTime? Reschedule(DateTime now, ScheduleSettings s)
    {
        NextDue = ComputeNext(now, s);
        return NextDue;
    }

    public DateTime? ComputeNext(DateTime now, ScheduleSettings s)
    {
        if (!s.AutoEnabled)
        {
            return null;
        }

        var (min, max) = ClampInterval(s.MinIntervalMinutes, s.MaxIntervalMinutes);
        var next = s.PausedUntil is { } paused && paused > now
            ? paused + Minutes(5, 20) // nghỉ xong thì ghé lại sớm, không bắt đợi thêm cả khoảng
            : now + Minutes(min, max);

        // Hai lần tự ghé không bao giờ gần nhau hơn khoảng ngắn nhất người dùng đặt.
        if (s.LastAutoVisit is { } last && next < last + TimeSpan.FromMinutes(min))
        {
            next = last + TimeSpan.FromMinutes(min);
        }

        return SkipQuiet(next, s.Quiet);
    }

    public TickOutcome Tick(DateTime now, ScheduleSettings s, VisitConditions c)
    {
        if (!s.AutoEnabled)
        {
            NextDue = null;
            return TickOutcome.Disabled;
        }

        if (NextDue is not { } due)
        {
            Reschedule(now, s);
            return TickOutcome.Rescheduled;
        }

        if (now < due)
        {
            return TickOutcome.Waiting;
        }

        // Quá hẹn lâu nghĩa là máy vừa ngủ hoặc bị treo: tính lịch mới thay vì phát bù.
        if (now - due > MissedTolerance || s.PausedUntil > now)
        {
            Reschedule(now, s);
            return TickOutcome.Rescheduled;
        }

        if (s.Quiet.Contains(now))
        {
            NextDue = SkipQuiet(now, s.Quiet);
            return TickOutcome.Deferred;
        }

        TimeSpan? defer = c switch
        {
            { SessionLocked: true } => DeferWhenBusy,
            { VisitInProgress: true } => DeferWhenPlaying,
            { FullScreenActive: true } => DeferWhenBusy,
            { DesktopOnly: true, DesktopInFront: false } => DeferWhenNotOnDesktop,
            _ => null,
        };

        if (defer is { } d)
        {
            NextDue = SkipQuiet(now + d, s.Quiet);
            return TickOutcome.Deferred;
        }

        NextDue = null;
        return TickOutcome.Play;
    }

    /// <summary>Sau lần ghé do người dùng bấm, không để lịch tự động ghé ngay sát sau đó.</summary>
    public void AfterManualVisit(DateTime now, ScheduleSettings s)
    {
        var gap = ManualVisitGap(s);
        if (NextDue is { } due && due < now + gap)
        {
            NextDue = SkipQuiet(now + gap + Minutes(0, gap.TotalMinutes), s.Quiet);
        }
    }

    public static TimeSpan ManualVisitGap(ScheduleSettings s)
    {
        var (min, _) = ClampInterval(s.MinIntervalMinutes, s.MaxIntervalMinutes);
        return TimeSpan.FromMinutes(Math.Min(min, MaximumGapAfterManualVisit.TotalMinutes));
    }

    public static (int Min, int Max) ClampInterval(int min, int max)
    {
        min = Math.Clamp(min, MinimumIntervalMinutes, MaximumIntervalMinutes);
        max = Math.Clamp(max, min, MaximumIntervalMinutes);
        return (min, max);
    }

    private DateTime SkipQuiet(DateTime t, QuietHours quiet)
    {
        // Vài vòng là đủ kể cả khi khoảng ngoài giờ yên lặng rất ngắn.
        for (var i = 0; i < 3 && quiet.Contains(t); i++)
        {
            t = quiet.EndAfter(t) + Minutes(5, 25);
        }

        return t;
    }

    private TimeSpan Minutes(double min, double max) =>
        TimeSpan.FromMinutes(min + _rng.NextDouble() * (max - min));
}

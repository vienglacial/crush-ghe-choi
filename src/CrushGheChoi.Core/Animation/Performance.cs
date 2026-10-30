namespace CrushGheChoi.Core.Animation;

/// <summary>Ba màn biểu diễn của bản đầu.</summary>
public enum PerformanceKind
{
    Greet,
    Kiss,
    GreetThenKiss,
}

public enum StepKind
{
    Rise,
    Wave,
    KissPrepare,
    Kiss,
    Idle,
    Goodbye,
}

public sealed record PerformanceStep(StepKind Kind, double Start, double Duration)
{
    public double End => Start + Duration;
}

/// <summary>Kịch bản một lần ghé: các bước theo thời gian (giây), lúc hiện bong bóng và mốc tim bay.</summary>
public sealed record Performance(
    PerformanceKind Kind,
    IReadOnlyList<PerformanceStep> Steps,
    double BubbleStart,
    double BubbleEnd,
    double? KissRelease)
{
    public double Duration => Steps[^1].End;

    public PerformanceStep StepAt(double t)
    {
        foreach (var step in Steps)
        {
            if (t < step.End)
            {
                return step;
            }
        }

        return Steps[^1];
    }
}

/// <summary>
/// Dựng kịch bản theo bảng thời gian trong kế hoạch (chào rồi hun ≈ 9 giây).
/// Nếu bộ hình có clip thật cho một động tác thì bước đó kéo dài đúng bằng clip.
/// </summary>
public static class Choreographer
{
    public const double RiseDuration = 0.8;
    public const double SinkDuration = 1.1;
    public const double BubbleDelay = 0.1;

    /// <summary>Không có clip hun thì tim bay ra lúc người đẩy nhẹ về phía trước.</summary>
    public const double ProceduralKissRelease = 0.25;

    public static Performance Build(PerformanceKind kind, AnimationSetDefinition? set = null)
    {
        double Length(StepKind step, double fallback)
        {
            var clip = set is null ? null : ClipFor(set, step);
            return clip is { Loop: false } ? Math.Max(clip.Duration(set!.Fps), 0.3) : fallback;
        }

        var steps = new List<PerformanceStep>();
        var t = 0.0;
        PerformanceStep Add(StepKind step, double duration)
        {
            var s = new PerformanceStep(step, t, duration);
            steps.Add(s);
            t += duration;
            return s;
        }

        Add(StepKind.Rise, RiseDuration);
        var bubbleStart = RiseDuration + BubbleDelay;
        double bubbleEnd;
        double? release = null;

        switch (kind)
        {
            case PerformanceKind.Greet:
                Add(StepKind.Wave, Length(StepKind.Wave, 2.5));
                Add(StepKind.Idle, 1.5);
                bubbleEnd = t - 0.2;
                break;

            case PerformanceKind.Kiss:
                Add(StepKind.KissPrepare, Length(StepKind.KissPrepare, 1.0));
                release = KissReleaseTime(Add(StepKind.Kiss, Length(StepKind.Kiss, 0.8)), set);
                bubbleEnd = t;
                Add(StepKind.Idle, 2.6);
                break;

            default:
                Add(StepKind.Wave, Length(StepKind.Wave, 2.0));
                bubbleEnd = t + 0.8;
                Add(StepKind.KissPrepare, Length(StepKind.KissPrepare, 1.0));
                release = KissReleaseTime(Add(StepKind.Kiss, Length(StepKind.Kiss, 0.8)), set);
                Add(StepKind.Idle, 2.6);
                break;
        }

        Add(StepKind.Goodbye, Math.Max(Length(StepKind.Goodbye, 1.8), SinkDuration + 0.3));
        return new Performance(kind, steps, bubbleStart, bubbleEnd, release);
    }

    /// <summary>Clip dùng cho một bước; lúc nhô lên và lúc chờ dùng clip đứng chờ nếu có.</summary>
    public static ClipDefinition? ClipFor(AnimationSetDefinition set, StepKind step) => step switch
    {
        StepKind.Wave => set.Clip(ClipKind.Wave),
        StepKind.KissPrepare => set.Clip(ClipKind.KissPrepare),
        StepKind.Kiss => set.Clip(ClipKind.Kiss),
        StepKind.Goodbye => set.Clip(ClipKind.Goodbye) ?? set.Clip(ClipKind.Idle),
        _ => set.Clip(ClipKind.Idle),
    };

    private static double KissReleaseTime(PerformanceStep kissStep, AnimationSetDefinition? set)
    {
        var clip = set?.Clip(ClipKind.Kiss);
        if (clip is null)
        {
            return kissStep.Start + ProceduralKissRelease;
        }

        var offset = clip.KissReleaseFrame is { } frame
            ? frame / set!.Fps
            : kissStep.Duration / 3;
        return kissStep.Start + Math.Clamp(offset, 0, kissStep.Duration);
    }
}

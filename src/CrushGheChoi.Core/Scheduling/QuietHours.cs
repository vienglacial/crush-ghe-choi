namespace CrushGheChoi.Core.Scheduling;

/// <summary>Khoảng giờ yên lặng theo giờ máy; hỗ trợ khoảng vắt qua nửa đêm (ví dụ 22:30–08:30).</summary>
public readonly record struct QuietHours(bool Enabled, TimeOnly Start, TimeOnly End)
{
    public static QuietHours None => new(false, default, default);

    public bool Contains(DateTime local)
    {
        if (!Enabled || Start == End)
        {
            return false;
        }

        var t = TimeOnly.FromDateTime(local);
        return Start < End
            ? t >= Start && t < End
            : t >= Start || t < End;
    }

    /// <summary>Thời điểm giờ yên lặng kết thúc lần tới tính từ <paramref name="local"/>.</summary>
    public DateTime EndAfter(DateTime local)
    {
        var endToday = local.Date + End.ToTimeSpan();
        return endToday > local ? endToday : endToday.AddDays(1);
    }
}

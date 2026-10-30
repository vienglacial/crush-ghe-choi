using CrushGheChoi.Core.Scheduling;

namespace CrushGheChoi.Tests;

public class QuietHoursTests
{
    private static readonly QuietHours Overnight = new(true, new TimeOnly(22, 30), new TimeOnly(8, 30));

    [Theory]
    [InlineData("2026-10-28 22:30", true)]
    [InlineData("2026-10-28 23:59", true)]
    [InlineData("2026-10-29 00:00", true)]
    [InlineData("2026-10-29 08:29", true)]
    [InlineData("2026-10-29 08:30", false)]
    [InlineData("2026-10-28 12:00", false)]
    [InlineData("2026-10-28 22:29", false)]
    public void Overnight_window_crosses_midnight(string at, bool expected) =>
        Assert.Equal(expected, Overnight.Contains(DateTime.Parse(at)));

    [Fact]
    public void Same_day_window()
    {
        var lunch = new QuietHours(true, new TimeOnly(12, 0), new TimeOnly(13, 0));
        Assert.True(lunch.Contains(DateTime.Parse("2026-10-28 12:30")));
        Assert.False(lunch.Contains(DateTime.Parse("2026-10-28 13:00")));
        Assert.False(lunch.Contains(DateTime.Parse("2026-10-28 11:59")));
    }

    [Fact]
    public void Disabled_or_empty_window_never_contains()
    {
        Assert.False((Overnight with { Enabled = false }).Contains(DateTime.Parse("2026-10-28 23:00")));
        Assert.False(new QuietHours(true, new TimeOnly(9, 0), new TimeOnly(9, 0)).Contains(DateTime.Parse("2026-10-28 09:00")));
    }

    [Theory]
    [InlineData("2026-10-28 23:00", "2026-10-29 08:30")]
    [InlineData("2026-10-29 02:00", "2026-10-29 08:30")]
    public void End_after_returns_next_end(string at, string expected) =>
        Assert.Equal(DateTime.Parse(expected), Overnight.EndAfter(DateTime.Parse(at)));
}

public class VisitPlannerTests
{
    private static readonly DateTime Noon = new(2026, 10, 28, 12, 0, 0);

    private static ScheduleSettings Settings(
        bool auto = true, int min = 45, int max = 90, bool quiet = true, DateTime? paused = null, DateTime? last = null) =>
        new(auto, min, max, new QuietHours(quiet, new TimeOnly(22, 30), new TimeOnly(8, 30)), paused, last);

    [Fact]
    public void Next_visit_falls_inside_interval()
    {
        var planner = new VisitPlanner(new Random(1));
        for (var i = 0; i < 500; i++)
        {
            var next = planner.ComputeNext(Noon, Settings())!.Value;
            Assert.InRange(next, Noon.AddMinutes(45), Noon.AddMinutes(90));
        }
    }

    [Fact]
    public void Short_custom_range_like_five_to_fifteen_minutes()
    {
        var planner = new VisitPlanner(new Random(2));
        for (var i = 0; i < 300; i++)
        {
            var next = planner.ComputeNext(Noon, Settings(min: 5, max: 15))!.Value;
            Assert.InRange(next, Noon.AddMinutes(5), Noon.AddMinutes(15));
        }
    }

    [Fact]
    public void Interval_floor_is_one_minute()
    {
        var planner = new VisitPlanner(new Random(2));
        Assert.True(planner.ComputeNext(Noon, Settings(min: 0, max: 0)) >= Noon.AddMinutes(1));
        Assert.Equal((1, 1), VisitPlanner.ClampInterval(-5, 0));
        Assert.Equal((5, 600), VisitPlanner.ClampInterval(5, 9999));
    }

    [Fact]
    public void Waits_at_least_the_minimum_interval_after_last_auto_visit()
    {
        var planner = new VisitPlanner(new Random(3));
        var next = planner.ComputeNext(Noon, Settings(min: 5, max: 5, last: Noon.AddMinutes(4)));
        Assert.True(next >= Noon.AddMinutes(9));
    }

    [Fact]
    public void Never_schedules_inside_quiet_hours()
    {
        var planner = new VisitPlanner(new Random(4));
        var quiet = Settings().Quiet;
        for (var minute = 0; minute < 24 * 60; minute += 7)
        {
            var now = Noon.Date.AddMinutes(minute);
            var next = planner.ComputeNext(now, Settings())!.Value;
            Assert.False(quiet.Contains(next), $"now={now:t} next={next:g}");
        }
    }

    [Fact]
    public void Evening_schedule_moves_to_next_morning()
    {
        var planner = new VisitPlanner(new Random(5));
        var next = planner.ComputeNext(new DateTime(2026, 10, 28, 22, 0, 0), Settings())!.Value;
        Assert.InRange(next, new DateTime(2026, 10, 29, 8, 35, 0), new DateTime(2026, 10, 29, 8, 55, 0));
    }

    [Fact]
    public void Pause_pushes_next_visit_after_pause_end()
    {
        var planner = new VisitPlanner(new Random(6));
        var until = Noon.AddHours(2);
        var next = planner.ComputeNext(Noon, Settings(paused: until))!.Value;
        Assert.InRange(next, until.AddMinutes(5), until.AddMinutes(20));
    }

    [Fact]
    public void Disabled_auto_has_no_schedule()
    {
        var planner = new VisitPlanner();
        Assert.Null(planner.Reschedule(Noon, Settings(auto: false)));
        Assert.Equal(TickOutcome.Disabled, planner.Tick(Noon, Settings(auto: false), default));
    }

    [Fact]
    public void Tick_waits_then_plays_once()
    {
        var planner = new VisitPlanner(new Random(7));
        var due = planner.Reschedule(Noon, Settings())!.Value;

        Assert.Equal(TickOutcome.Waiting, planner.Tick(due.AddSeconds(-1), Settings(), new VisitConditions()));
        Assert.Equal(TickOutcome.Play, planner.Tick(due.AddSeconds(10), Settings(), new VisitConditions()));

        // Không phát lần hai khi chưa có lịch mới.
        Assert.Null(planner.NextDue);
        Assert.NotEqual(TickOutcome.Play, planner.Tick(due.AddSeconds(30), Settings(), new VisitConditions()));
    }

    [Fact]
    public void Missed_visit_after_sleep_is_rescheduled_not_replayed()
    {
        var planner = new VisitPlanner(new Random(8));
        var due = planner.Reschedule(Noon, Settings())!.Value;
        var wake = due.AddHours(3);

        Assert.Equal(TickOutcome.Rescheduled, planner.Tick(wake, Settings(), new VisitConditions()));
        Assert.True(planner.NextDue >= wake.AddMinutes(45));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Busy_conditions_defer(bool locked, bool fullScreen, bool desktopOnlyNotFront, bool playing)
    {
        var planner = new VisitPlanner(new Random(9));
        var due = planner.Reschedule(Noon, Settings())!.Value;
        var c = new VisitConditions(locked, fullScreen, desktopOnlyNotFront, !desktopOnlyNotFront, playing);

        Assert.Equal(TickOutcome.Deferred, planner.Tick(due, Settings(), c));
        Assert.True(planner.NextDue > due);
    }

    [Fact]
    public void Desktop_only_plays_when_desktop_is_in_front()
    {
        var planner = new VisitPlanner(new Random(10));
        var due = planner.Reschedule(Noon, Settings())!.Value;
        Assert.Equal(TickOutcome.Play, planner.Tick(due, Settings(), new VisitConditions(DesktopOnly: true, DesktopInFront: true)));
    }

    [Fact]
    public void Due_time_inside_quiet_hours_is_deferred_to_morning()
    {
        var planner = new VisitPlanner(new Random(11));
        planner.Reschedule(new DateTime(2026, 10, 28, 20, 0, 0), Settings(quiet: false));
        var due = planner.NextDue!.Value;

        // Người dùng vừa bật giờ yên lặng bao trùm giờ hẹn.
        var lateQuiet = new ScheduleSettings(true, 45, 90, new QuietHours(true, new TimeOnly(20, 0), new TimeOnly(8, 30)), null, null);
        Assert.Equal(TickOutcome.Deferred, planner.Tick(due, lateQuiet, new VisitConditions()));
        Assert.True(planner.NextDue >= new DateTime(2026, 10, 29, 8, 30, 0));
    }

    [Fact]
    public void Manual_visit_keeps_auto_visit_away()
    {
        var planner = new VisitPlanner(new Random(12));
        planner.Reschedule(Noon, Settings());
        var soon = planner.NextDue!.Value.AddMinutes(-2);

        planner.AfterManualVisit(soon, Settings());
        Assert.True(planner.NextDue >= soon + VisitPlanner.ManualVisitGap(Settings()));
        Assert.Equal(TimeSpan.FromMinutes(5), VisitPlanner.ManualVisitGap(Settings(min: 5, max: 15)));
    }
}

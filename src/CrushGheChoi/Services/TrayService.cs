using System.Windows;
using CrushGheChoi.Core.Animation;
using CrushGheChoi.Core.Settings;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace CrushGheChoi.Services;

/// <summary>Trạng thái hiển thị trên menu khay.</summary>
internal sealed record TrayState(
    bool AutoEnabled,
    DateTime? NextVisit,
    DateTime? PausedUntil,
    bool QuietNow,
    int MinIntervalMinutes,
    int MaxIntervalMinutes,
    PlacementMode Placement,
    ScreenCorner Corner);

/// <summary>Các lệnh menu khay gọi về bộ điều phối.</summary>
internal interface ITrayCommands
{
    void VisitNow(PerformanceKind? kind);

    void PauseFor(TimeSpan duration);

    void PauseUntilTomorrow();

    void ResumeVisits();

    void ToggleAutoVisit();

    void SetInterval(int minMinutes, int maxMinutes);

    void SetPlacement(PlacementMode placement, ScreenCorner? corner);

    void BeginAdjustPosition();

    void OpenSettings();

    void OpenAddPhotos();

    void Exit();

    TrayState GetTrayState();
}

/// <summary>Biểu tượng ở khay hệ thống và menu nhanh. Nhấp đúp để gọi crush ghé ngay.</summary>
internal sealed class TrayService : IDisposable
{
    private readonly ITrayCommands _commands;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Forms.ToolStripMenuItem _autoItem;
    private readonly Forms.ToolStripMenuItem _resumeItem;
    private readonly Forms.ToolStripMenuItem _quietTwoHoursItem;
    private readonly Forms.ToolStripMenuItem _intervalMenu;
    private readonly List<(Forms.ToolStripMenuItem Item, IntervalPreset Preset)> _intervalItems = [];
    private readonly Forms.ToolStripMenuItem _placeRandom;
    private readonly Forms.ToolStripMenuItem _placeLeft;
    private readonly Forms.ToolStripMenuItem _placeRight;

    public TrayService(ITrayCommands commands)
    {
        _commands = commands;

        var menu = new Forms.ContextMenuStrip
        {
            Font = new Drawing.Font("Segoe UI", 9.5f),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Renderer = new Forms.ToolStripProfessionalRenderer(new SoftColors()),
        };

        var visitNow = Item("Ghé chơi ngay", () => _commands.VisitNow(null));
        visitNow.Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold);

        var performances = new Forms.ToolStripMenuItem("Chọn màn");
        performances.DropDownItems.AddRange(
        [
            Item("Chào", () => _commands.VisitNow(PerformanceKind.Greet)),
            Item("Hun gió", () => _commands.VisitNow(PerformanceKind.Kiss)),
            Item("Chào rồi hun", () => _commands.VisitNow(PerformanceKind.GreetThenKiss)),
        ]);

        _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
        _quietTwoHoursItem = Item("Yên lặng 2 giờ", () => _commands.PauseFor(TimeSpan.FromHours(2)));
        _resumeItem = Item("Tiếp tục ghé chơi", _commands.ResumeVisits);

        var pause = new Forms.ToolStripMenuItem("Tạm nghỉ");
        pause.DropDownItems.AddRange(
        [
            Item("30 phút", () => _commands.PauseFor(TimeSpan.FromMinutes(30))),
            Item("1 giờ", () => _commands.PauseFor(TimeSpan.FromHours(1))),
            Item("2 giờ", () => _commands.PauseFor(TimeSpan.FromHours(2))),
            Item("Đến ngày mai", _commands.PauseUntilTomorrow),
        ]);

        _autoItem = Item("Tự động ghé chơi", _commands.ToggleAutoVisit);

        _intervalMenu = new Forms.ToolStripMenuItem("Tần suất ghé");
        foreach (var preset in AppSettings.IntervalPresets)
        {
            var item = Item($"Ngẫu nhiên {preset.Label}", () => _commands.SetInterval(preset.MinMinutes, preset.MaxMinutes));
            _intervalItems.Add((item, preset));
            _intervalMenu.DropDownItems.Add(item);
        }

        _intervalMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        _intervalMenu.DropDownItems.Add(Item("Khoảng khác…", _commands.OpenSettings));

        _placeRandom = Item("Ngẫu nhiên khắp màn hình", () => _commands.SetPlacement(PlacementMode.Random, null));
        _placeRight = Item("Góc phải dưới", () => _commands.SetPlacement(PlacementMode.Corner, ScreenCorner.BottomRight));
        _placeLeft = Item("Góc trái dưới", () => _commands.SetPlacement(PlacementMode.Corner, ScreenCorner.BottomLeft));
        var placement = new Forms.ToolStripMenuItem("Vị trí xuất hiện");
        placement.DropDownItems.AddRange([_placeRandom, _placeRight, _placeLeft, new Forms.ToolStripSeparator(), Item("Kéo tới chỗ cố định…", _commands.BeginAdjustPosition)]);

        menu.Items.AddRange(
        [
            visitNow,
            performances,
            new Forms.ToolStripSeparator(),
            _statusItem,
            _resumeItem,
            _quietTwoHoursItem,
            pause,
            _autoItem,
            _intervalMenu,
            placement,
            new Forms.ToolStripSeparator(),
            Item("Thêm hình crush…", _commands.OpenAddPhotos),
            Item("Cài đặt…", _commands.OpenSettings),
            new Forms.ToolStripSeparator(),
            Item("Thoát", _commands.Exit),
        ]);
        menu.Opening += (_, _) => Refresh();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Text = "Crush ghé chơi",
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Run(() => _commands.VisitNow(null));
        Refresh();
    }

    public void Refresh()
    {
        var state = _commands.GetTrayState();
        var now = DateTime.Now;
        var paused = state.PausedUntil is { } p && p > now;

        _statusItem.Text = !state.AutoEnabled ? "Lịch tự động đang tắt"
            : paused ? $"Đang tạm nghỉ đến {Describe(state.PausedUntil!.Value)}"
            : state.QuietNow ? "Đang trong giờ yên lặng"
            : state.NextVisit is { } next ? $"Lần ghé tiếp: khoảng {Describe(next)}"
            : "Đang chờ lịch ghé";
        _resumeItem.Visible = paused;
        _quietTwoHoursItem.Visible = !paused;
        _autoItem.Checked = state.AutoEnabled;

        var matched = false;
        foreach (var (item, preset) in _intervalItems)
        {
            item.Checked = preset.MinMinutes == state.MinIntervalMinutes && preset.MaxMinutes == state.MaxIntervalMinutes;
            matched |= item.Checked;
        }

        _intervalMenu.Text = matched
            ? "Tần suất ghé"
            : $"Tần suất ghé ({state.MinIntervalMinutes}–{state.MaxIntervalMinutes} phút)";
        _placeRandom.Checked = state.Placement == PlacementMode.Random;
        _placeRight.Checked = state.Placement == PlacementMode.Corner && state.Corner == ScreenCorner.BottomRight;
        _placeLeft.Checked = state.Placement == PlacementMode.Corner && state.Corner == ScreenCorner.BottomLeft;

        // Chú thích khay tối đa 127 ký tự.
        var tip = $"Crush ghé chơi\n{_statusItem.Text}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public void ShowBalloon(string text, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        _icon.ShowBalloonTip(5000, "Crush ghé chơi", text, icon);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }

    private static string Describe(DateTime t)
    {
        var day = t.Date == DateTime.Today ? string.Empty
            : t.Date == DateTime.Today.AddDays(1) ? " ngày mai"
            : $" {t:dd/MM}";
        return $"{t:HH:mm}{day}";
    }

    private static Drawing.Icon LoadIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        if (info?.Stream is not { } stream)
        {
            return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        }

        using (stream)
        {
            // Chọn đúng cỡ icon khay theo tỷ lệ màn hình.
            return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
    }

    private static Forms.ToolStripMenuItem Item(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => Run(action);
        return item;
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("Lỗi khi chạy lệnh trên menu khay", ex);
        }
    }

    private sealed class SoftColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(255, 232, 240);
        private static readonly Drawing.Color Edge = Drawing.Color.FromArgb(245, 163, 192);

        public override Drawing.Color MenuItemSelected => Hover;

        public override Drawing.Color MenuItemBorder => Edge;

        public override Drawing.Color MenuBorder => Edge;

        public override Drawing.Color ToolStripDropDownBackground => Drawing.Color.White;

        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;

        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;

        public override Drawing.Color MenuItemPressedGradientBegin => Hover;

        public override Drawing.Color MenuItemPressedGradientEnd => Hover;

        public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(250, 210, 225);

        public override Drawing.Color ImageMarginGradientBegin => Drawing.Color.White;

        public override Drawing.Color ImageMarginGradientMiddle => Drawing.Color.White;

        public override Drawing.Color ImageMarginGradientEnd => Drawing.Color.White;
    }
}

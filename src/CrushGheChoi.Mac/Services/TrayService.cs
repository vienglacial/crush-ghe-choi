using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using CrushGheChoi.Core.Settings;
using PlacementMode = CrushGheChoi.Core.Settings.PlacementMode;

namespace CrushGheChoi.Services;

internal sealed record TrayState(
    bool AutoEnabled,
    DateTime? NextVisit,
    DateTime? PausedUntil,
    bool QuietNow,
    int MinIntervalMinutes,
    int MaxIntervalMinutes,
    PlacementMode Placement,
    ScreenCorner Corner);

/// <summary>Biểu tượng trái tim trên thanh menu của macOS, kèm menu điều khiển.</summary>
internal sealed class TrayService : IDisposable
{
    private readonly AppController _commands;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _status = new("Đang chờ lịch ghé") { IsEnabled = false };
    private readonly NativeMenuItem _resume = new("Tiếp tục ghé chơi");
    private readonly NativeMenuItem _auto = new("Tự động ghé chơi") { ToggleType = NativeMenuItemToggleType.CheckBox };
    private readonly List<(NativeMenuItem Item, IntervalPreset Preset)> _presets = [];
    private readonly NativeMenuItem _placeRandom = Radio("Ngẫu nhiên khắp màn hình");
    private readonly NativeMenuItem _placeRight = Radio("Góc phải dưới");
    private readonly NativeMenuItem _placeLeft = Radio("Góc trái dưới");

    public TrayService(AppController commands)
    {
        _commands = commands;

        var pause = new NativeMenuItem("Tạm nghỉ") { Menu = [] };
        pause.Menu.Add(Item("30 phút", () => _commands.PauseFor(TimeSpan.FromMinutes(30))));
        pause.Menu.Add(Item("1 giờ", () => _commands.PauseFor(TimeSpan.FromHours(1))));
        pause.Menu.Add(Item("2 giờ", () => _commands.PauseFor(TimeSpan.FromHours(2))));
        pause.Menu.Add(Item("Đến mai", _commands.PauseUntilTomorrow));

        var interval = new NativeMenuItem("Tần suất ghé") { Menu = [] };
        foreach (var preset in AppSettings.IntervalPresets)
        {
            var item = Radio(preset.Label);
            item.Click += (_, _) => _commands.SetInterval(preset.MinMinutes, preset.MaxMinutes);
            interval.Menu.Add(item);
            _presets.Add((item, preset));
        }

        interval.Menu.Add(new NativeMenuItemSeparator());
        interval.Menu.Add(Item("Khoảng khác…", _commands.OpenSettings));

        _placeRandom.Click += (_, _) => _commands.SetPlacement(PlacementMode.Random, null);
        _placeRight.Click += (_, _) => _commands.SetPlacement(PlacementMode.Corner, ScreenCorner.BottomRight);
        _placeLeft.Click += (_, _) => _commands.SetPlacement(PlacementMode.Corner, ScreenCorner.BottomLeft);
        var placement = new NativeMenuItem("Vị trí xuất hiện") { Menu = [_placeRandom, _placeRight, _placeLeft] };

        _resume.Click += (_, _) => _commands.ResumeVisits();
        _auto.Click += (_, _) => _commands.ToggleAutoVisit();

        var menu = new NativeMenu
        {
            Item("Ghé chơi ngay", () => _commands.VisitNow(null)),
            new NativeMenuItemSeparator(),
            _status,
            _resume,
            pause,
            _auto,
            interval,
            placement,
            new NativeMenuItemSeparator(),
            Item("Thêm hình crush…", _commands.OpenAddPhotos),
            Item("Cài đặt…", _commands.OpenSettings),
            new NativeMenuItemSeparator(),
            Item("Thoát", _commands.Exit),
        };

        _icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CrushGheChoi/Assets/tray.png"))),
            ToolTipText = "Crush ghé chơi",
            Menu = menu,
            IsVisible = true,
        };

        // Trên Windows (khi chạy thử) bấm trái vào biểu tượng là gọi crush; trên macOS bấm là mở menu.
        _icon.Clicked += (_, _) => _commands.VisitNow(null);
        if (Application.Current is { } app)
        {
            TrayIcon.SetIcons(app, [_icon]);
        }
    }

    public void Refresh()
    {
        var s = _commands.GetTrayState();
        var now = DateTime.Now;
        var paused = s.PausedUntil is { } p && p > now;
        _status.Header = !s.AutoEnabled ? "Lịch tự động đang tắt"
            : paused ? $"Tạm nghỉ đến {s.PausedUntil:HH:mm}"
            : s.QuietNow ? "Đang trong giờ yên lặng"
            : s.NextVisit is { } next ? $"Lần ghé tới khoảng {next:HH:mm}"
            : "Đang chờ lịch ghé";
        _resume.IsEnabled = paused;
        _auto.IsChecked = s.AutoEnabled;
        foreach (var (item, preset) in _presets)
        {
            item.IsChecked = s.AutoEnabled && preset.MinMinutes == s.MinIntervalMinutes && preset.MaxMinutes == s.MaxIntervalMinutes;
        }

        _placeRandom.IsChecked = s.Placement == PlacementMode.Random;
        _placeRight.IsChecked = s.Placement == PlacementMode.Corner && s.Corner == ScreenCorner.BottomRight;
        _placeLeft.IsChecked = s.Placement == PlacementMode.Corner && s.Corner == ScreenCorner.BottomLeft;
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private static NativeMenuItem Radio(string header) => new(header) { ToggleType = NativeMenuItemToggleType.Radio };
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CrushGheChoi.Core.Animation;
using CrushGheChoi.Core.Cutout;
using CrushGheChoi.Core.Greetings;
using CrushGheChoi.Core.Scheduling;
using CrushGheChoi.Core.Settings;
using CrushGheChoi.Cutout;
using CrushGheChoi.Overlay;
using CrushGheChoi.Services;
using CrushGheChoi.UI;
using CorePixelRect = CrushGheChoi.Core.Layout.PixelRect;
using PlacementMode = CrushGheChoi.Core.Settings.PlacementMode;

namespace CrushGheChoi;

/// <summary>
/// Bộ điều phối của bản macOS: giữ cài đặt và bộ hình, chạy lịch ghé và bảo đảm mỗi lúc chỉ có một màn đang phát.
/// Dùng chung phần lõi (lịch, cài đặt, bộ hình, tách nền) với bản Windows.
/// </summary>
internal sealed class AppController : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

    private readonly SettingsService _settingsService = new(AppPaths.SettingsFile);
    private readonly VisitPlanner _planner = new();
    private readonly GreetingPicker _greetings = new();
    private readonly ShuffleBag _stills = new();
    private readonly DispatcherTimer _timer;

    private AppSettings _settings = new();
    private AnimationSetDefinition _set = null!;
    private OverlayWindow _overlay = null!;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private WelcomeWindow? _welcomeWindow;
    private AddPhotosWindow? _addPhotosWindow;
    private CancellationTokenSource? _visitCts;
    private string? _lastDeferReason;

    public AppController()
    {
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, (_, _) => OnTick());
        _timer.Stop();
    }

    public AppSettings Settings => _settings;

    public AnimationSetDefinition CurrentSet => _set;

    public string? SetError { get; private set; }

    public DateTime? NextVisit => _planner.NextDue;

    /// <summary>Bộ hình "Crush của tôi" do người dùng tự thêm qua cửa sổ Thêm hình.</summary>
    public UserSetStore UserSet { get; } = new(AppPaths.UserSetDirectory);

    /// <summary>Đang dùng hình mẫu (người dùng chưa thêm hình nào).</summary>
    public bool UsingPlaceholder =>
        string.Equals(_set.RootDirectory, Path.GetFullPath(AppPaths.BuiltInSetDirectory), StringComparison.OrdinalIgnoreCase);

    public int ScreenCount => _overlay.Screens.ScreenCount;

    public void Start(string[] args)
    {
        var now = DateTime.Now;
        var load = _settingsService.Load(now);
        _settings = load.Settings;
        if (load.Fixes.Count > 0)
        {
            Log.Warn($"Đã sửa cấu hình không hợp lệ: {string.Join(", ", load.Fixes)}");
        }

        (_set, SetError) = LoadSetOrFallback(_settings.AnimationSetPath);
        _settings.AnimationSetVersion = _set.Version;
        if (SetError is not null)
        {
            Log.Warn(SetError);
        }

        _overlay = new OverlayWindow();
        _tray = new TrayService(this);

        if (_settings.RunAtStartup)
        {
            // Cập nhật đường dẫn trong trường hợp app đã bị chuyển sang thư mục khác.
            TryApplyStartup(true);
        }

        _planner.Reschedule(now, _settings.ToSchedule());
        _timer.Start();
        SaveSettings();
        _tray.Refresh();

        var photos = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && PhotoIO.LooksLikePhoto(a) && File.Exists(a)).ToList();
        var wantsAdd = args.Contains("--add-photos", StringComparer.OrdinalIgnoreCase) || photos.Count > 0;

        if (!_settings.FirstRunCompleted)
        {
            ShowWelcome();
        }

        if (wantsAdd)
        {
            OpenAddPhotos(photos);
        }
        else if (_settings.FirstRunCompleted && args.Contains("--visit", StringComparer.OrdinalIgnoreCase))
        {
            VisitNow(null);
        }

        if (_settings.FirstRunCompleted && args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            OpenSettings();
        }
    }

    // ----- Lệnh từ menu trên thanh menu -----

    public void VisitNow(PerformanceKind? kind) =>
        _ = PlayVisitAsync(kind ?? PickKind(), isAuto: false, _settings.Clone(), _set);

    public void PauseFor(TimeSpan duration)
    {
        CancelVisit();
        _settings.PausedUntil = DateTime.Now + duration;
        AfterScheduleChange();
    }

    public void PauseUntilTomorrow()
    {
        CancelVisit();
        var now = DateTime.Now;
        var morning = _settings.QuietHoursEnabled ? _settings.QuietEnd.ToTimeSpan() : TimeSpan.FromHours(8);
        var until = now.Date + morning;

        // Bấm lúc rạng sáng (trước giờ sáng vài tiếng) thì "ngày mai" chính là sáng nay.
        if (until <= now.AddHours(3))
        {
            until = until.AddDays(1);
        }

        _settings.PausedUntil = until;
        AfterScheduleChange();
    }

    public void ResumeVisits()
    {
        _settings.PausedUntil = null;
        AfterScheduleChange();
    }

    public void ToggleAutoVisit()
    {
        _settings.AutoVisitEnabled = !_settings.AutoVisitEnabled;
        AfterScheduleChange();
    }

    public void SetInterval(int minMinutes, int maxMinutes)
    {
        (_settings.MinIntervalMinutes, _settings.MaxIntervalMinutes) = VisitPlanner.ClampInterval(minMinutes, maxMinutes);
        _settings.AutoVisitEnabled = true;
        AfterScheduleChange();
        _settingsWindow?.ReloadFromController();
    }

    public void SetPlacement(PlacementMode placement, ScreenCorner? corner)
    {
        _settings.Placement = placement;
        if (corner is { } c)
        {
            _settings.Corner = c;
        }

        SaveSettings();
        _tray?.Refresh();
        _settingsWindow?.ReloadFromController();
    }

    public void OpenSettings()
    {
        if (_welcomeWindow is not null)
        {
            Bring(_welcomeWindow);
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        Bring(_settingsWindow);
    }

    public void OpenAddPhotos() => OpenAddPhotos(null);

    /// <summary>Mở cửa sổ Thêm hình (một cửa sổ duy nhất), kèm ảnh cần thêm hoặc một hình đã có cần chỉnh lại.</summary>
    public void OpenAddPhotos(IEnumerable<string>? files, UserPhoto? edit = null)
    {
        if (_addPhotosWindow is null)
        {
            _addPhotosWindow = new AddPhotosWindow(this, UserSet);
            _addPhotosWindow.Closed += (_, _) => _addPhotosWindow = null;
            _addPhotosWindow.Show();
        }

        Bring(_addPhotosWindow);
        if (files is not null)
        {
            _addPhotosWindow.AddFiles(files);
        }

        if (edit is not null)
        {
            _addPhotosWindow.EditExisting(edit);
        }
    }

    /// <summary>Bộ "Crush của tôi" vừa đổi (thêm, sửa, xóa hình). Có hình mới thì cho crush ghé luôn bằng hình đó.</summary>
    public void OnUserPhotosChanged(bool showNewest)
    {
        if (_settings.AnimationSetPath is null)
        {
            (_set, SetError) = LoadSetOrFallback(null);
            _stills.Reset();
            _settings.AnimationSetVersion = _set.Version;
            SaveSettings();
        }

        _settingsWindow?.OnPhotosChanged();
        _welcomeWindow?.OnPhotosChanged();
        _tray?.Refresh();

        if (showNewest && !UsingPlaceholder && _settings.AnimationSetPath is null)
        {
            CancelVisit();
            _ = PlayVisitAsync(PerformanceKind.GreetThenKiss, isAuto: false, _settings.Clone(), _set, _set.Stills[^1]);
        }
    }

    public void Exit() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    public TrayState GetTrayState() => new(
        _settings.AutoVisitEnabled,
        _planner.NextDue,
        _settings.PausedUntil,
        _settings.QuietHours.Contains(DateTime.Now),
        _settings.MinIntervalMinutes,
        _settings.MaxIntervalMinutes,
        _settings.Placement,
        _settings.Corner);

    // ----- Dùng bởi cửa sổ Cài đặt và Giới thiệu -----

    /// <summary>Xem thử một màn với các tùy chọn đang chỉnh (chưa lưu). Chạy cả trong giờ yên lặng.</summary>
    public void Preview(AppSettings working, PerformanceKind kind) =>
        _ = PlayVisitAsync(kind, isAuto: false, working.Clone(), _set);

    /// <summary>Áp dụng cài đặt từ cửa sổ. Trả về thông báo cho người dùng nếu có phần không áp dụng được.</summary>
    public string? ApplySettings(AppSettings updated)
    {
        var now = DateTime.Now;
        updated = updated.Clone();

        // Tạm nghỉ và lần ghé cuối có thể đã đổi từ menu trong lúc cửa sổ đang mở.
        updated.PausedUntil = _settings.PausedUntil;
        updated.LastAutoVisit = _settings.LastAutoVisit;
        updated.AnimationSetPath = _settings.AnimationSetPath;
        updated.FirstRunCompleted |= _settings.FirstRunCompleted;
        updated.Normalize(now);
        updated.AnimationSetVersion = _set.Version;

        string? message = null;
        if (updated.RunAtStartup != _settings.RunAtStartup && !TryApplyStartup(updated.RunAtStartup))
        {
            updated.RunAtStartup = _settings.RunAtStartup;
            message = "Không đổi được tùy chọn mở cùng máy (xem nhật ký để biết chi tiết).";
        }

        if (!updated.Greetings.SequenceEqual(_settings.Greetings))
        {
            _greetings.Reset();
        }

        var scheduleChanged = updated.ToSchedule() != _settings.ToSchedule();
        _settings = updated;
        SaveSettings();
        if (scheduleChanged)
        {
            _planner.Reschedule(now, _settings.ToSchedule());
        }

        _tray?.Refresh();
        return message;
    }

    public void CompleteWelcome(AppSettings chosen)
    {
        chosen.FirstRunCompleted = true;
        ApplySettings(chosen);
    }

    public void RecoverFromError()
    {
        try
        {
            CancelVisit();
        }
        catch (Exception ex)
        {
            Log.Error("Không phục hồi được sau lỗi", ex);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        CancelVisit();
        _addPhotosWindow?.ForceClose();
        _settingsWindow?.Close();
        _welcomeWindow?.ForceClose();
        _overlay?.Close();
        _tray?.Dispose();
    }

    // ----- Nội bộ -----

    private static PerformanceKind PickKind() => Random.Shared.Next(4) switch
    {
        0 => PerformanceKind.Greet,
        1 => PerformanceKind.Kiss,
        _ => PerformanceKind.GreetThenKiss,
    };

    private static void Bring(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        NativeWindow.ActivateApp();
        window.Activate();
    }

    /// <summary>Bộ "Crush của tôi" nếu đã có hình (hoặc thư mục ghi trong cài đặt); chưa có hay bị lỗi thì dùng hình mẫu.</summary>
    private (AnimationSetDefinition Set, string? Error) LoadSetOrFallback(string? path)
    {
        string? error = null;
        var custom = !string.IsNullOrWhiteSpace(path);
        if (custom || File.Exists(UserSet.ManifestPath))
        {
            try
            {
                return (AnimationSetLoader.Load(custom ? path! : UserSet.Directory), null);
            }
            catch (Exception ex) when (ex is AnimationSetException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                error = $"Bộ hình crush bị lỗi ({ex.Message}). Tạm dùng hình mẫu.";
            }
        }

        return (AnimationSetLoader.Load(AppPaths.BuiltInSetDirectory), error);
    }

    private MonitorInfo ResolveMonitor(string? name)
    {
        var screens = _overlay.Screens;
        var all = screens.All;
        var screen = name == AppSettings.AnyMonitor && all.Count > 0
            ? all[Random.Shared.Next(all.Count)]
            : screens.Primary ?? all.FirstOrDefault();
        if (screen is null)
        {
            return new MonitorInfo("?", new CorePixelRect(0, 0, 1440, 900), 1, 1);
        }

        // macOS đặt vị trí cửa sổ theo đơn vị giao diện (point) nên tỷ lệ vị trí là 1 dù màn hình Retina;
        // Windows đặt theo pixel thật nên phải nhân với tỷ lệ của màn hình.
        var area = screen.WorkingArea;
        var positionScale = OperatingSystem.IsWindows() ? screen.Scaling : _overlay.DesktopScaling;
        return new MonitorInfo(
            screen.DisplayName ?? "Màn hình",
            new CorePixelRect(area.X, area.Y, area.Width, area.Height),
            positionScale,
            Math.Max(positionScale, screen.Scaling));
    }

    private async Task PlayVisitAsync(PerformanceKind kind, bool isAuto, AppSettings settings, AnimationSetDefinition set, StillVariant? forcedStill = null)
    {
        // Gọi liên tục không tạo thêm màn chồng lên nhau.
        if (_visitCts is not null || _overlay.IsPlaying)
        {
            return;
        }

        var cts = _visitCts = new CancellationTokenSource();
        try
        {
            var monitor = ResolveMonitor(settings.MonitorDeviceName);
            var greeting = _greetings.Pick(settings.Greetings);
            var still = forcedStill ?? set.Stills[_stills.Next(set.Stills.Count)];
            Log.Info($"Ghé chơi ({(isAuto ? "tự động" : "theo yêu cầu")}): {kind}, hình {Path.GetFileName(still.Path)}, trên {monitor.Name} ({monitor.Scale:0.##}/{monitor.Density:0.##})");
            await _overlay.PlayAsync(new VisitRequest(kind, greeting, settings, still, monitor), cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error("Lỗi khi phát lần ghé", ex);
        }
        finally
        {
            if (ReferenceEquals(_visitCts, cts))
            {
                _visitCts = null;
            }

            cts.Dispose();
            var now = DateTime.Now;
            if (isAuto)
            {
                _settings.LastAutoVisit = now;
                SaveSettings();
                _planner.Reschedule(now, _settings.ToSchedule());
                Log.Info($"Lịch: lần tới {_planner.NextDue:g}");
            }
            else
            {
                _planner.AfterManualVisit(now, _settings.ToSchedule());
            }

            _tray?.Refresh();
        }
    }

    private void OnTick()
    {
        var now = DateTime.Now;
        if (_settings.PausedUntil is { } paused && paused <= now)
        {
            _settings.PausedUntil = null;
            SaveSettings();
        }

        if (_planner.NextDue is { } due && now < due)
        {
            _tray?.Refresh();
            return;
        }

        // Bản macOS chưa nhận biết khóa màn hình hay app toàn màn hình; cửa sổ crush không hiện trên app toàn màn hình.
        var conditions = new VisitConditions(
            SessionLocked: false,
            FullScreenActive: false,
            DesktopOnly: false,
            DesktopInFront: true,
            VisitInProgress: _visitCts is not null || _overlay.IsPlaying);

        var outcome = _planner.Tick(now, _settings.ToSchedule(), conditions);
        if (outcome is TickOutcome.Play or TickOutcome.Rescheduled)
        {
            Log.Info(outcome == TickOutcome.Play ? "Lịch: tới giờ ghé" : $"Lịch: tính lại, lần tới {_planner.NextDue:g}");
            _lastDeferReason = null;
        }
        else if (outcome == TickOutcome.Deferred)
        {
            var reason = _settings.QuietHours.Contains(now) ? "giờ yên lặng" : "đang có màn khác";
            if (reason != _lastDeferReason)
            {
                Log.Info($"Hoãn lần ghé: {reason}; thử lại lúc {_planner.NextDue:HH:mm}");
                _lastDeferReason = reason;
            }
        }

        if (outcome == TickOutcome.Play)
        {
            _ = PlayVisitAsync(PickKind(), isAuto: true, _settings.Clone(), _set);
        }

        _tray?.Refresh();
    }

    private void CancelVisit()
    {
        _visitCts?.Cancel();
        _overlay?.Cancel();
    }

    private void AfterScheduleChange()
    {
        SaveSettings();
        _planner.Reschedule(DateTime.Now, _settings.ToSchedule());
        _tray?.Refresh();
        _settingsWindow?.RefreshStatus();
    }

    private bool TryApplyStartup(bool enabled)
    {
        try
        {
            StartupService.Apply(enabled);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log.Error("Không cập nhật được tùy chọn mở cùng máy", ex);
            return false;
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Không lưu được cấu hình", ex);
        }
    }

    private void ShowWelcome()
    {
        _welcomeWindow = new WelcomeWindow(this);
        _welcomeWindow.Closed += (_, _) => _welcomeWindow = null;
        _welcomeWindow.Show();
        Bring(_welcomeWindow);
    }
}

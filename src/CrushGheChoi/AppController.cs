using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CrushGheChoi.Core.Animation;
using CrushGheChoi.Core.Cutout;
using CrushGheChoi.Core.Greetings;
using CrushGheChoi.Core.Layout;
using CrushGheChoi.Core.Scheduling;
using CrushGheChoi.Core.Settings;
using CrushGheChoi.Cutout;
using CrushGheChoi.Overlay;
using CrushGheChoi.Services;
using CrushGheChoi.UI;

namespace CrushGheChoi;

/// <summary>
/// Bộ điều phối: giữ cài đặt và bộ hình, chạy lịch ghé, phản ứng với khóa máy/ngủ/đổi màn hình,
/// và bảo đảm mỗi lúc chỉ có một màn đang phát.
/// </summary>
internal sealed class AppController : ITrayCommands, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);
    private static readonly NormalizedPoint EmergencyKissPoint = new(0.6, 0.54);

    private readonly Dispatcher _dispatcher;
    private readonly SettingsService _settingsService = new(AppPaths.SettingsFile);
    private readonly VisitPlanner _planner = new();
    private readonly GreetingPicker _greetings = new();
    private readonly ShuffleBag _stills = new();
    private readonly DispatcherTimer _timer;

    private AppSettings _settings = new();
    private AnimationSetDefinition _set = null!;
    private DesktopOverlayWindow _overlay = null!;
    private TrayService? _tray;
    private SystemStateService? _system;
    private SettingsWindow? _settingsWindow;
    private WelcomeWindow? _welcomeWindow;
    private AddPhotosWindow? _addPhotosWindow;
    private CancellationTokenSource? _visitCts;
    private AdjustRequest? _adjust;
    private bool _saveErrorShown;
    private string? _lastDeferReason;

    public AppController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TickInterval };
        _timer.Tick += OnTick;
    }

    public AppSettings Settings => _settings;

    public AnimationSetDefinition CurrentSet => _set;

    public string? SetError { get; private set; }

    /// <summary>Bộ hình "Crush của tôi" do người dùng tự thêm qua cửa sổ Thêm hình.</summary>
    public UserSetStore UserSet { get; } = new(AppPaths.UserSetDirectory);

    /// <summary>Đang dùng hình mẫu (người dùng chưa thêm hình nào).</summary>
    public bool UsingPlaceholder => string.Equals(_set.RootDirectory, Path.GetFullPath(AppPaths.BuiltInSetDirectory), StringComparison.OrdinalIgnoreCase)
        || _set.StillPath.Length == 0;

    public DateTime? NextVisit => _planner.NextDue;

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

        _overlay = new DesktopOverlayWindow();
        _overlay.Initialize();
        _overlay.AdjustSaved += OnAdjustSaved;
        _overlay.AdjustCancelled += () => _adjust = null;

        _system = new SystemStateService(_dispatcher);
        _system.Locked += (_, _) => Pause("khóa máy");
        _system.Suspending += (_, _) => Pause("máy ngủ");
        _system.Unlocked += (_, _) => ResumeAfterWake();
        _system.Resumed += (_, _) => ResumeAfterWake();
        _system.DisplaysChanged += (_, _) => CancelVisit();

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

        if (load.Status == SettingsLoadStatus.RecoveredFromCorrupt)
        {
            _tray.ShowBalloon("Tệp cấu hình bị lỗi nên tui dùng cài đặt mặc định. Bản lỗi vẫn được giữ trong thư mục dữ liệu.");
        }
        else if (SetError is not null)
        {
            _tray.ShowBalloon(SetError);
        }

        // Ảnh truyền vào (mở bằng app, kéo thả lên exe, hoặc --add-photos) thì mở cửa sổ Thêm hình.
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
        else if (_settings.FirstRunCompleted && !args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            _tray.ShowBalloon("Crush đang ở khay hệ thống nè. Nhấp đúp vào trái tim để gọi crush ghé chơi.");
        }
    }

    public void HandleSecondLaunch(SecondLaunch what)
    {
        switch (what)
        {
            case SecondLaunch.Visit:
                VisitNow(null);
                break;
            case SecondLaunch.Exit:
                Exit();
                break;
            default:
                OpenSettings();
                break;
        }
    }

    // ----- Lệnh từ menu khay -----

    public void VisitNow(PerformanceKind? kind) =>
        _ = PlayVisitAsync(kind ?? PickKind(), isAuto: false, _settings.Clone(), _set);

    public void PauseFor(TimeSpan duration)
    {
        CancelVisit();
        _settings.PausedUntil = DateTime.Now + duration;
        AfterScheduleChange();
        _tray?.ShowBalloon($"Crush tạm nghỉ tới {_settings.PausedUntil:HH:mm} nha.");
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
        _tray?.ShowBalloon($"Crush nghỉ tới {until:HH:mm} {(until.Date == now.Date ? "sáng nay" : "ngày mai")} nha.");
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
        _settingsWindow?.OnScheduleChangedOutside(_settings);
        _tray?.ShowBalloon($"Crush sẽ ghé ngẫu nhiên mỗi {_settings.MinIntervalMinutes}–{_settings.MaxIntervalMinutes} phút nha.");
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
        _settingsWindow?.OnPositionChangedOutside(_settings);
    }

    public void BeginAdjustPosition() => BeginAdjust(_settings, (corner, ox, oy) =>
    {
        _settings.Placement = PlacementMode.Corner;
        _settings.Corner = corner;
        _settings.EdgeOffsetXDip = ox;
        _settings.EdgeOffsetYDip = oy;
        SaveSettings();
        _settingsWindow?.OnPositionChangedOutside(_settings);
    });

    public void OpenSettings()
    {
        if (_welcomeWindow is not null)
        {
            _welcomeWindow.Activate();
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }

    public void OpenAddPhotos() => OpenAddPhotos(null);

    /// <summary>Mở cửa sổ Thêm hình (một cửa sổ duy nhất), kèm ảnh cần tách nền hoặc một hình đã có cần chỉnh lại.</summary>
    public void OpenAddPhotos(IEnumerable<string>? files, UserPhoto? edit = null)
    {
        if (_addPhotosWindow is null)
        {
            _addPhotosWindow = new AddPhotosWindow(this, UserSet);
            _addPhotosWindow.Closed += (_, _) => _addPhotosWindow = null;
            _addPhotosWindow.Show();
        }
        else if (_addPhotosWindow.WindowState == WindowState.Minimized)
        {
            _addPhotosWindow.WindowState = WindowState.Normal;
        }

        _addPhotosWindow.Activate();
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

    public void Exit() => Application.Current.Shutdown();

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
    public string? Preview(AppSettings working, PerformanceKind kind)
    {
        var (set, error) = SetFor(working);
        _ = PlayVisitAsync(kind, isAuto: false, working.Clone(), set);
        return error;
    }

    public void BeginAdjust(AppSettings basis, Action<ScreenCorner, double, double> onSaved)
    {
        CancelVisit();
        var (set, _) = SetFor(basis);
        _adjust = new AdjustRequest(basis.Clone(), set, onSaved);
        var device = basis.MonitorDeviceName == AppSettings.AnyMonitor ? null : basis.MonitorDeviceName;
        _overlay.BeginAdjust(_adjust.Basis, set, ScreenService.Resolve(device));
    }

    /// <summary>Áp dụng cài đặt từ cửa sổ. Trả về thông báo cho người dùng nếu có phần không áp dụng được.</summary>
    public string? ApplySettings(AppSettings updated)
    {
        var now = DateTime.Now;
        updated = updated.Clone();

        // Tạm nghỉ và lần ghé cuối có thể đã đổi từ menu khay trong lúc cửa sổ đang mở.
        updated.PausedUntil = _settings.PausedUntil;
        updated.LastAutoVisit = _settings.LastAutoVisit;
        updated.FirstRunCompleted |= _settings.FirstRunCompleted;
        updated.Normalize(now);

        string? message = null;
        if (!string.Equals(updated.AnimationSetPath, _settings.AnimationSetPath, StringComparison.OrdinalIgnoreCase))
        {
            (_set, SetError) = LoadSetOrFallback(updated.AnimationSetPath);
            _stills.Reset();
            message = SetError;
        }

        updated.AnimationSetVersion = _set.Version;

        if (updated.RunAtStartup != _settings.RunAtStartup && !TryApplyStartup(updated.RunAtStartup))
        {
            updated.RunAtStartup = _settings.RunAtStartup;
            message = "Không đổi được tùy chọn chạy cùng Windows (xem nhật ký để biết chi tiết).";
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
        var message = ApplySettings(chosen);
        _tray?.ShowBalloon(message ?? "Crush đang ở khay hệ thống nè. Nhấp đúp vào trái tim để gọi crush ghé chơi.");
    }

    /// <summary>
    /// Bộ hình theo cài đặt: thư mục đã chọn, hoặc (mặc định) bộ "Crush của tôi"; chưa có hình nào hay bộ bị lỗi
    /// thì dùng hình mẫu đi kèm app.
    /// </summary>
    public (AnimationSetDefinition Set, string? Error) LoadSetOrFallback(string? path)
    {
        string? error = null;
        var custom = !string.IsNullOrWhiteSpace(path);
        if (custom || File.Exists(UserSet.ManifestPath))
        {
            try
            {
                var set = AnimationSetLoader.Load(custom ? path! : UserSet.Directory);
                LogWarnings(set);
                return (set, null);
            }
            catch (Exception ex) when (ex is AnimationSetException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                error = custom
                    ? $"Bộ hình đã chọn bị lỗi ({ex.Message}). Tạm dùng hình mẫu."
                    : $"Bộ hình crush của bạn bị lỗi ({ex.Message}). Tạm dùng hình mẫu.";
                Log.Warn(error);
            }
        }

        try
        {
            var builtIn = AnimationSetLoader.Load(AppPaths.BuiltInSetDirectory);
            LogWarnings(builtIn);
            return (builtIn, error);
        }
        catch (Exception ex) when (ex is AnimationSetException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Bộ hình có sẵn bị thiếu hoặc hỏng", ex);
            return (EmergencySet(), error ?? "Hình mẫu đi kèm app bị thiếu, đang dùng ảnh dự phòng.");
        }
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
        _welcomeWindow?.Close();
        _overlay?.Close();
        _system?.Dispose();
        _tray?.Dispose();
    }

    // ----- Nội bộ -----

    private static PerformanceKind PickKind() => Random.Shared.Next(4) switch
    {
        0 => PerformanceKind.Greet,
        1 => PerformanceKind.Kiss,
        _ => PerformanceKind.GreetThenKiss,
    };

    private static void LogWarnings(AnimationSetDefinition set)
    {
        foreach (var w in set.Warnings)
        {
            Log.Warn($"Bộ hình “{set.Name}”: {w}");
        }
    }

    private static AnimationSetDefinition EmergencySet()
    {
        // Ảnh đứng nhúng trong app; đường dẫn rỗng báo cho cửa sổ crush dùng ảnh nhúng.
        int width = 410, height = 520;
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Crush/default/still.png"));
        if (info?.Stream is { } stream)
        {
            using (stream)
            {
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                (width, height) = (frame.PixelWidth, frame.PixelHeight);
            }
        }

        return new AnimationSetDefinition(
            "Ảnh dự phòng", "0", AppContext.BaseDirectory, string.Empty, width, height, 24,
            EmergencyKissPoint, new Dictionary<ClipKind, ClipDefinition>(), []);
    }

    private (AnimationSetDefinition Set, string? Error) SetFor(AppSettings s) =>
        string.Equals(s.AnimationSetPath, _settings.AnimationSetPath, StringComparison.OrdinalIgnoreCase)
            ? (_set, null)
            : LoadSetOrFallback(s.AnimationSetPath);

    private async Task PlayVisitAsync(PerformanceKind kind, bool isAuto, AppSettings settings, AnimationSetDefinition set, StillVariant? forcedStill = null)
    {
        // Gọi liên tục không tạo thêm màn chồng lên nhau.
        if (_visitCts is not null || _overlay.IsPlaying || _overlay.IsAdjusting || _system?.IsSessionLocked == true)
        {
            return;
        }

        var cts = _visitCts = new CancellationTokenSource();
        try
        {
            var monitor = ScreenService.Resolve(settings.MonitorDeviceName);
            var greeting = _greetings.Pick(settings.Greetings);

            // Bộ chỉ có ảnh đứng thì mỗi lần ghé một hình khác; có clip chuyển động thì dùng ảnh chính đi cùng clip.
            var still = forcedStill ?? (set.IsStillOnly ? set.Stills[_stills.Next(set.Stills.Count)] : set.Primary);
            Log.Info($"Ghé chơi ({(isAuto ? "tự động" : "theo yêu cầu")}): {kind}, hình {Path.GetFileName(still.Path)}, trên {monitor.DeviceName} ({monitor.Scale * 100:0}%)");
            if (settings.SoundEnabled)
            {
                Chime.Play();
            }

            await _overlay.PlayAsync(new VisitRequest(kind, greeting, settings, set, still, monitor), cts.Token);
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

    private void OnTick(object? sender, EventArgs e)
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

        string? fullScreenApp = null;
        var conditions = new VisitConditions(
            SessionLocked: _system?.IsSessionLocked == true || _system?.IsSuspended == true,
            FullScreenActive: _settings.DeferOnFullScreen
                && SystemStateService.IsFullScreenAppActive(_settings.FullScreenAllowedApps, out fullScreenApp),
            DesktopOnly: _settings.DisplayMode == DisplayMode.DesktopOnly,
            DesktopInFront: SystemStateService.IsDesktopInFront(),
            VisitInProgress: _visitCts is not null || _overlay.IsPlaying || _overlay.IsAdjusting);

        var outcome = _planner.Tick(now, _settings.ToSchedule(), conditions);
        if (outcome is TickOutcome.Play or TickOutcome.Rescheduled)
        {
            // Lúc phát chưa có lịch mới; lịch được tính khi màn biểu diễn kết thúc.
            Log.Info(outcome == TickOutcome.Play ? "Lịch: tới giờ ghé" : $"Lịch: tính lại, lần tới {_planner.NextDue:g}");
            _lastDeferReason = null;
        }
        else if (outcome == TickOutcome.Deferred)
        {
            // Chỉ ghi khi lý do đổi, để nhật ký không bị lặp mỗi phút.
            var reason = DeferReason(conditions, fullScreenApp, now);
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

    private string DeferReason(VisitConditions c, string? fullScreenApp, DateTime now) => c switch
    {
        _ when _settings.QuietHours.Contains(now) => "giờ yên lặng",
        { SessionLocked: true } => "máy đang khóa",
        { VisitInProgress: true } => "đang có màn khác",
        { FullScreenActive: true } => $"ứng dụng toàn màn hình ({fullScreenApp ?? "không rõ"})",
        { DesktopOnly: true, DesktopInFront: false } => "chế độ chỉ trên desktop, desktop chưa ở phía trước",
        _ => "chưa tiện",
    };

    private void Pause(string reason)
    {
        Log.Info($"Dừng lịch vì {reason}");
        CancelVisit();
        _timer.Stop();
    }

    private void ResumeAfterWake()
    {
        if (_system is { IsSessionLocked: true } or { IsSuspended: true })
        {
            return;
        }

        // Tính lịch mới; những lần đã lỡ trong lúc khóa/ngủ không phát bù.
        _planner.Reschedule(DateTime.Now, _settings.ToSchedule());
        _timer.Start();
        _tray?.Refresh();
        Log.Info($"Chạy lại lịch; lần tới {_planner.NextDue:g}");
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

    private void OnAdjustSaved(PixelRect imageRect, MonitorInfo monitor)
    {
        if (_adjust is not { } adjust)
        {
            return;
        }

        _adjust = null;
        var (corner, ox, oy) = OverlayGeometry.AnchorFromImage(
            imageRect, monitor.WorkArea, monitor.Scale, adjust.Basis.HeightDip, adjust.Set.FrameWidth, adjust.Set.FrameHeight);
        adjust.OnSaved(corner, Math.Round(ox), Math.Round(oy));
    }

    private bool TryApplyStartup(bool enabled)
    {
        try
        {
            StartupService.Apply(enabled);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error("Không cập nhật được khóa Run", ex);
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
            if (!_saveErrorShown)
            {
                _saveErrorShown = true;
                _tray?.ShowBalloon("Không lưu được cài đặt xuống đĩa. App vẫn chạy bình thường với cài đặt hiện tại.");
            }
        }
    }

    private void ShowWelcome()
    {
        _welcomeWindow = new WelcomeWindow(this);
        _welcomeWindow.Closed += (_, _) => _welcomeWindow = null;
        _welcomeWindow.Show();
        _welcomeWindow.Activate();
    }

    private sealed record AdjustRequest(AppSettings Basis, AnimationSetDefinition Set, Action<ScreenCorner, double, double> OnSaved);
}

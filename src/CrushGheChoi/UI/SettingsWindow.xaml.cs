using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CrushGheChoi.Core.Animation;
using CrushGheChoi.Core.Cutout;
using CrushGheChoi.Core.Scheduling;
using CrushGheChoi.Core.Settings;
using CrushGheChoi.Cutout;
using CrushGheChoi.Services;
using Microsoft.Win32;

namespace CrushGheChoi.UI;

internal partial class SettingsWindow : Window
{
    private readonly AppController _controller;
    private readonly List<string?> _monitorChoices = [];
    private AppSettings _working;
    private bool _syncing;

    public SettingsWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
        foreach (var preset in AppSettings.IntervalPresets)
        {
            var button = new Button
            {
                Content = preset.Label,
                Style = (Style)FindResource("SoftButton"),
                Padding = new Thickness(10, 3, 10, 3),
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 6, 6),
            };
            button.Click += (_, _) =>
            {
                MinIntervalBox.Text = preset.MinMinutes.ToString();
                MaxIntervalBox.Text = preset.MaxMinutes.ToString();
                AutoVisitBox.IsChecked = true;
            };
            IntervalPresetPanel.Children.Add(button);
        }

        _working = controller.Settings.Clone();
        LoadFrom(_working);

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        AboutText.Text =
            $"Crush ghé chơi {version?.ToString(3)} · .NET {Environment.Version}\n" +
            $"Cấu hình: {AppPaths.SettingsFile}\n" +
            $"Nhật ký: {AppPaths.LogDirectory}";
    }

    /// <summary>Cập nhật dòng trạng thái lịch (gọi khi tạm nghỉ/bật tắt từ menu khay).</summary>
    public void RefreshStatus()
    {
        var s = _controller.Settings;
        var now = DateTime.Now;
        var paused = s.PausedUntil is { } p && p > now;
        var text = !s.AutoVisitEnabled ? "Lịch tự động đang tắt."
            : paused ? $"Đang tạm nghỉ đến {s.PausedUntil:HH:mm dd/MM}."
            : s.QuietHours.Contains(now) ? "Đang trong giờ yên lặng."
            : _controller.NextVisit is { } next ? $"Lần ghé tiếp theo vào khoảng {next:HH:mm}{(next.Date == now.Date ? string.Empty : " ngày " + next.ToString("dd/MM"))}."
            : "Đang chờ lịch ghé.";
        StatusText.Text = text;
        ScheduleText.Text = text;
        ResumeButton.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Bộ "Crush của tôi" vừa thêm, sửa hoặc xóa hình.</summary>
    public void OnPhotosChanged()
    {
        BuildPhotoStrip();
        ShowSetInfo(CustomSetRadio.IsChecked == true ? SetPathBox.Text : null);
    }

    /// <summary>Vị trí vừa được đổi bằng "Chỉnh vị trí" trên menu khay trong lúc cửa sổ đang mở.</summary>
    public void OnPositionChangedOutside(AppSettings s)
    {
        _working.Placement = s.Placement;
        _working.Corner = s.Corner;
        _working.EdgeOffsetXDip = s.EdgeOffsetXDip;
        _working.EdgeOffsetYDip = s.EdgeOffsetYDip;
        ShowPosition(s.Placement, s.Corner, s.EdgeOffsetXDip, s.EdgeOffsetYDip);
    }

    /// <summary>Tần suất vừa được đổi từ menu khay trong lúc cửa sổ đang mở.</summary>
    public void OnScheduleChangedOutside(AppSettings s)
    {
        AutoVisitBox.IsChecked = s.AutoVisitEnabled;
        MinIntervalBox.Text = s.MinIntervalMinutes.ToString();
        MaxIntervalBox.Text = s.MaxIntervalMinutes.ToString();
        RefreshStatus();
    }

    private void LoadFrom(AppSettings s)
    {
        _syncing = true;
        try
        {
            BuiltInSetRadio.IsChecked = s.AnimationSetPath is null;
            CustomSetRadio.IsChecked = s.AnimationSetPath is not null;
            SetPathBox.Text = s.AnimationSetPath ?? string.Empty;

            SizeSlider.Value = s.HeightDip;
            PaletteBox.SelectedIndex = (int)s.HeartPalette;
            HeartSlider.Value = s.HeartCount;
            ReducedMotionBox.IsChecked = s.ReducedMotion;

            AutoVisitBox.IsChecked = s.AutoVisitEnabled;
            MinIntervalBox.Text = s.MinIntervalMinutes.ToString();
            MaxIntervalBox.Text = s.MaxIntervalMinutes.ToString();
            QuietBox.IsChecked = s.QuietHoursEnabled;
            QuietStartBox.Text = s.QuietStart.ToString("HH:mm");
            QuietEndBox.Text = s.QuietEnd.ToString("HH:mm");
            GreetingsBox.Text = string.Join(Environment.NewLine, s.Greetings);

            LoadMonitors(s.MonitorDeviceName);
            ShowPosition(s.Placement, s.Corner, s.EdgeOffsetXDip, s.EdgeOffsetYDip);
            ModeVisit.IsChecked = s.DisplayMode == DisplayMode.Visit;
            ModeDesktop.IsChecked = s.DisplayMode == DisplayMode.DesktopOnly;
            DeferFullScreenBox.IsChecked = s.DeferOnFullScreen;
            AllowedAppsBox.Text = string.Join(", ", s.FullScreenAllowedApps);

            SoundBox.IsChecked = s.SoundEnabled;
            StartupBox.IsChecked = s.RunAtStartup;
        }
        finally
        {
            _syncing = false;
        }

        SyncSizeControls();
        HeartValue.Text = ((int)HeartSlider.Value).ToString();
        BuildPhotoStrip();
        ShowSetInfo(s.AnimationSetPath);
        RefreshStatus();
    }

    private void ShowPosition(PlacementMode placement, ScreenCorner corner, double ox, double oy)
    {
        PlaceRandom.IsChecked = placement == PlacementMode.Random;
        CornerLeft.IsChecked = placement == PlacementMode.Corner && corner == ScreenCorner.BottomLeft;
        CornerRight.IsChecked = placement == PlacementMode.Corner && corner == ScreenCorner.BottomRight;
        OffsetXBox.Text = ox.ToString("0");
        OffsetYBox.Text = oy.ToString("0");
    }

    private void LoadMonitors(string? selected)
    {
        MonitorBox.Items.Clear();
        _monitorChoices.Clear();
        MonitorBox.Items.Add("Màn hình chính (tự chọn)");
        _monitorChoices.Add(null);
        var monitors = ScreenService.GetMonitors();
        if (monitors.Count > 1)
        {
            MonitorBox.Items.Add("Ngẫu nhiên giữa các màn hình");
            _monitorChoices.Add(AppSettings.AnyMonitor);
        }

        foreach (var m in monitors)
        {
            MonitorBox.Items.Add(m.DisplayName);
            _monitorChoices.Add(m.DeviceName);
        }

        var index = _monitorChoices.IndexOf(selected);
        if (index < 0 && selected is not null)
        {
            // Giữ lựa chọn màn hình đang bị rút ra để khi cắm lại crush quay về đó.
            MonitorBox.Items.Add($"{selected} (đang không kết nối — tạm dùng màn hình chính)");
            _monitorChoices.Add(selected);
            index = _monitorChoices.Count - 1;
        }

        MonitorBox.SelectedIndex = Math.Max(0, index);
    }

    private void ShowSetInfo(string? path)
    {
        var (set, error) = string.Equals(path, _controller.Settings.AnimationSetPath, StringComparison.OrdinalIgnoreCase)
            ? (_controller.CurrentSet, _controller.SetError)
            : _controller.LoadSetOrFallback(path);

        var motion = set.IsStillOnly
            ? $"{set.Stills.Count} hình, mỗi lần ghé bốc một hình (chuyển động do app tạo)"
            : $"{set.Clips.Count} động tác: {string.Join(", ", set.Clips.Keys.Select(ClipLabel))}";
        var info = $"{set.Name} · phiên bản {set.Version} · {motion}";
        if (set.Warnings.Count > 0)
        {
            info += $"\n{set.Warnings.Count} cảnh báo: {string.Join(" ", set.Warnings)}";
        }

        if (error is not null)
        {
            info = error + "\n" + info;
        }

        SetInfoText.Text = info;
    }

    private void BuildPhotoStrip()
    {
        PhotoStrip.Children.Clear();
        var photos = _controller.UserSet.Load();
        foreach (var photo in photos)
        {
            PhotoStrip.Children.Add(PhotoTile(photo));
        }

        var custom = _controller.Settings.AnimationSetPath is not null;
        PhotosHint.Text = photos.Count == 0
            ? "Chưa có hình nào, crush đang tạm là một trái tim. Bấm “Thêm hình” để chọn hình crush đã tách nền (PNG nền trong suốt); ảnh thường cũng được nếu đã cài bộ tách nền."
            : $"{photos.Count} hình, mỗi lần ghé bốc một hình. Thêm, sửa, xóa có hiệu lực ngay."
              + (custom ? " Đang dùng bộ hình trong thư mục khác (xem Nâng cao), nên các hình này tạm chưa hiện." : string.Empty);
    }

    private FrameworkElement PhotoTile(UserPhoto photo)
    {
        var image = new Image { Height = 96, MaxWidth = 150, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        try
        {
            image.Source = PhotoIO.Thumbnail(Path.Combine(_controller.UserSet.Directory, photo.File), 192);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Không đọc được hình {photo.File}: {ex.Message}");
        }

        Button Small(string text) => new()
        {
            Content = text,
            Style = (Style)FindResource("SoftButton"),
            Padding = new Thickness(8, 2, 8, 3),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 4, 0),
        };

        var edit = Small("Sửa");
        var canEdit = _controller.UserSet.HasDraft(photo.Id);
        edit.IsEnabled = canEdit;
        edit.ToolTip = canEdit ? "Chỉnh lại đường cắt, chỗ tim bay ra, tẩy nền" : "Hình này không có bản nháp để chỉnh. Muốn chỉnh thì xóa rồi thêm lại ảnh gốc.";
        ToolTipService.SetShowOnDisabled(edit, true);
        edit.Click += (_, _) => _controller.OpenAddPhotos(null, photo);

        var delete = Small("Xóa");
        delete.Click += (_, _) => DeletePhoto(photo);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(edit);
        buttons.Children.Add(delete);
        var stack = new StackPanel();
        stack.Children.Add(new Border { Background = (Brush)FindResource("SoftBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Child = image });
        stack.Children.Add(buttons);
        return new Border { Child = stack, Margin = new Thickness(0, 0, 10, 10) };
    }

    private void DeletePhoto(UserPhoto photo)
    {
        if (MessageBox.Show(this, "Xóa hình này khỏi bộ crush của bạn?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _controller.UserSet.Remove(photo.Id);
            _controller.OnUserPhotosChanged(showNewest: false);
            MessageText.Text = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText.Text = $"Không xóa được: {ex.Message}";
        }
    }

    private void OnAddPhotos(object sender, RoutedEventArgs e) => _controller.OpenAddPhotos();

    private static string ClipLabel(ClipKind kind) => kind switch
    {
        ClipKind.Idle => "đứng chờ",
        ClipKind.Wave => "chào",
        ClipKind.KissPrepare => "chuẩn bị hun",
        ClipKind.Kiss => "hun gió",
        ClipKind.Goodbye => "tạm biệt",
        _ => kind.ToString(),
    };

    private bool TryReadInto(AppSettings s, out string? error)
    {
        error = null;

        if (CustomSetRadio.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(SetPathBox.Text))
            {
                error = "Hãy chọn thư mục bộ hình, hoặc dùng hình crush của bạn.";
                return false;
            }

            s.AnimationSetPath = SetPathBox.Text.Trim();
        }
        else
        {
            s.AnimationSetPath = null;
        }

        s.HeightDip = SizeSlider.Value;
        s.HeartPalette = (HeartPalette)Math.Max(0, PaletteBox.SelectedIndex);
        s.HeartCount = (int)HeartSlider.Value;
        s.ReducedMotion = ReducedMotionBox.IsChecked == true;

        s.AutoVisitEnabled = AutoVisitBox.IsChecked == true;
        if (!int.TryParse(MinIntervalBox.Text.Trim(), out var min) || !int.TryParse(MaxIntervalBox.Text.Trim(), out var max))
        {
            error = "Khoảng thời gian ghé phải là số phút.";
            return false;
        }

        if (min < VisitPlanner.MinimumIntervalMinutes)
        {
            error = $"Khoảng ghé ngắn nhất là {VisitPlanner.MinimumIntervalMinutes} phút.";
            return false;
        }

        if (max < min)
        {
            error = "Số phút “đến” phải lớn hơn hoặc bằng số phút “từ”.";
            return false;
        }

        if (max > VisitPlanner.MaximumIntervalMinutes)
        {
            error = $"Khoảng ghé dài nhất là {VisitPlanner.MaximumIntervalMinutes} phút (10 giờ).";
            return false;
        }

        s.MinIntervalMinutes = min;
        s.MaxIntervalMinutes = max;

        s.QuietHoursEnabled = QuietBox.IsChecked == true;
        if (!TryParseTime(QuietStartBox.Text, out var quietStart) || !TryParseTime(QuietEndBox.Text, out var quietEnd))
        {
            error = "Giờ yên lặng nhập theo dạng 22:30.";
            return false;
        }

        s.QuietStart = quietStart;
        s.QuietEnd = quietEnd;

        s.Greetings = GreetingsBox.Text
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (s.Greetings.Count == 0)
        {
            error = "Cần ít nhất một câu chào.";
            return false;
        }

        if (s.Greetings.Any(g => g.Length > AppSettings.MaxGreetingLength))
        {
            error = $"Mỗi câu chào tối đa {AppSettings.MaxGreetingLength} ký tự.";
            return false;
        }

        s.MonitorDeviceName = MonitorBox.SelectedIndex >= 0 && MonitorBox.SelectedIndex < _monitorChoices.Count
            ? _monitorChoices[MonitorBox.SelectedIndex]
            : null;
        s.Placement = PlaceRandom.IsChecked == true ? PlacementMode.Random : PlacementMode.Corner;
        if (s.Placement == PlacementMode.Corner)
        {
            s.Corner = CornerLeft.IsChecked == true ? ScreenCorner.BottomLeft : ScreenCorner.BottomRight;
        }
        if (!TryParseOffset(OffsetXBox.Text, out var ox) || !TryParseOffset(OffsetYBox.Text, out var oy))
        {
            error = $"Khoảng cách tới mép là số từ 0 đến {AppSettings.MaxEdgeOffsetDip:0}.";
            return false;
        }

        s.EdgeOffsetXDip = ox;
        s.EdgeOffsetYDip = oy;
        s.DisplayMode = ModeDesktop.IsChecked == true ? DisplayMode.DesktopOnly : DisplayMode.Visit;
        s.DeferOnFullScreen = DeferFullScreenBox.IsChecked == true;
        s.FullScreenAllowedApps = AllowedAppsBox.Text
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        s.SoundEnabled = SoundBox.IsChecked == true;
        s.RunAtStartup = StartupBox.IsChecked == true;
        return true;
    }

    /// <summary>Nhận "22:30", "22h30", "7:05" hoặc "22".</summary>
    private static bool TryParseTime(string text, out TimeOnly time)
    {
        time = default;
        var parts = text.Trim().ToLowerInvariant().Replace('h', ':').Replace('.', ':').Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out var hour))
        {
            return false;
        }

        var minute = 0;
        if (parts.Length == 2 && parts[1].Length > 0 && !int.TryParse(parts[1], out minute))
        {
            return false;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return false;
        }

        time = new TimeOnly(hour, minute);
        return true;
    }

    private static bool TryParseOffset(string text, out double value) =>
        double.TryParse(text.Trim(), out value) && value is >= 0 and <= AppSettings.MaxEdgeOffsetDip;

    private bool TryReadWorkingCopy(out AppSettings copy)
    {
        copy = _working.Clone();
        if (TryReadInto(copy, out var error))
        {
            MessageText.Text = string.Empty;
            return true;
        }

        MessageText.Text = error;
        return false;
    }

    private void SyncSizeControls()
    {
        var v = SizeSlider.Value;
        SizeValue.Text = $"{v:0}";
        _syncing = true;
        try
        {
            SizeSmall.IsChecked = Math.Abs(v - AppSettings.SmallHeightDip) < 0.5;
            SizeMedium.IsChecked = Math.Abs(v - AppSettings.DefaultHeightDip) < 0.5;
            SizeLarge.IsChecked = Math.Abs(v - AppSettings.LargeHeightDip) < 0.5;
            SizeCustom.IsChecked = SizeSmall.IsChecked != true && SizeMedium.IsChecked != true && SizeLarge.IsChecked != true;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSizePreset(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        SizeSlider.Value = sender == SizeSmall ? AppSettings.SmallHeightDip
            : sender == SizeLarge ? AppSettings.LargeHeightDip
            : AppSettings.DefaultHeightDip;
    }

    private void OnSizeSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SizeValue is not null && !_syncing)
        {
            SyncSizeControls();
        }
    }

    private void OnHeartSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HeartValue is not null)
        {
            HeartValue.Text = ((int)HeartSlider.Value).ToString();
        }
    }

    private void OnSetChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || SetInfoText is null)
        {
            return;
        }

        if (sender == CustomSetRadio && string.IsNullOrWhiteSpace(SetPathBox.Text))
        {
            OnBrowseSet(sender, e);
            return;
        }

        ShowSetInfo(CustomSetRadio.IsChecked == true ? SetPathBox.Text : null);
    }

    private void OnBrowseSet(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Chọn thư mục bộ hình (có tệp manifest.json)",
            InitialDirectory = Directory.Exists(SetPathBox.Text) ? SetPathBox.Text : AppPaths.DataDirectory,
        };

        if (dialog.ShowDialog(this) != true)
        {
            if (string.IsNullOrWhiteSpace(SetPathBox.Text))
            {
                BuiltInSetRadio.IsChecked = true;
            }

            return;
        }

        try
        {
            var set = AnimationSetLoader.Load(dialog.FolderName);
            SetPathBox.Text = set.RootDirectory;
            _syncing = true;
            CustomSetRadio.IsChecked = true;
            _syncing = false;
            ShowSetInfo(set.RootDirectory);
            MessageText.Text = string.Empty;
        }
        catch (Exception ex) when (ex is AnimationSetException or IOException or UnauthorizedAccessException)
        {
            MessageText.Text = $"Thư mục này chưa dùng được: {ex.Message}";
            if (string.IsNullOrWhiteSpace(SetPathBox.Text))
            {
                BuiltInSetRadio.IsChecked = true;
            }
        }
    }

    private void Preview(PerformanceKind kind)
    {
        if (TryReadWorkingCopy(out var copy) && _controller.Preview(copy, kind) is { } error)
        {
            MessageText.Text = error;
        }
    }

    private void OnPreviewGreet(object sender, RoutedEventArgs e) => Preview(PerformanceKind.Greet);

    private void OnPreviewKiss(object sender, RoutedEventArgs e) => Preview(PerformanceKind.Kiss);

    private void OnPreviewBoth(object sender, RoutedEventArgs e) => Preview(PerformanceKind.GreetThenKiss);

    private void OnResume(object sender, RoutedEventArgs e)
    {
        _controller.ResumeVisits();
        RefreshStatus();
    }

    private void OnResetGreetings(object sender, RoutedEventArgs e) =>
        GreetingsBox.Text = string.Join(Environment.NewLine, AppSettings.DefaultGreetings);

    private void OnAdjustPosition(object sender, RoutedEventArgs e)
    {
        if (!TryReadWorkingCopy(out var copy))
        {
            return;
        }

        _controller.BeginAdjust(copy, (corner, ox, oy) =>
        {
            ShowPosition(PlacementMode.Corner, corner, ox, oy);
            MessageText.Text = "Đã đổi vị trí — bấm Lưu để giữ lại.";
        });
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (!TryReadWorkingCopy(out var copy))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Sao lưu cấu hình",
            Filter = "Cấu hình Crush ghé chơi (*.json)|*.json",
            FileName = $"crush-ghe-choi-{DateTime.Now:yyyyMMdd}.json",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SettingsService.Export(copy, dialog.FileName);
            MessageText.Text = string.Empty;
            MessageBox.Show(this, "Đã sao lưu cấu hình.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText.Text = $"Không sao lưu được: {ex.Message}";
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Khôi phục cấu hình từ bản sao",
            Filter = "Cấu hình Crush ghé chơi (*.json)|*.json|Mọi tệp (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _working = SettingsService.Import(dialog.FileName, DateTime.Now);
            LoadFrom(_working);
            MessageText.Text = "Đã nạp bản sao — bấm Lưu để áp dụng.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageText.Text = ex.Message;
        }
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDirectory}\"") { UseShellExecute = true });
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryReadWorkingCopy(out var copy))
        {
            return;
        }

        if (_controller.ApplySettings(copy) is { } message)
        {
            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

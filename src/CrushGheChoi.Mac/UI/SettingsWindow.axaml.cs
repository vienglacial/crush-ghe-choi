using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CrushGheChoi.Core.Animation;
using CrushGheChoi.Core.Cutout;
using CrushGheChoi.Core.Scheduling;
using CrushGheChoi.Core.Settings;
using CrushGheChoi.Cutout;
using CrushGheChoi.Services;
using PlacementMode = CrushGheChoi.Core.Settings.PlacementMode;

namespace CrushGheChoi.UI;

public partial class SettingsWindow : Window
{
    private readonly AppController? _controller;
    private AppSettings _working = new();

    public SettingsWindow()
    {
        InitializeComponent();
    }

    internal SettingsWindow(AppController controller)
        : this()
    {
        _controller = controller;
        foreach (var preset in AppSettings.IntervalPresets)
        {
            var button = new Button { Content = preset.Label, Padding = new Thickness(10, 3), FontSize = 12.5, Margin = new Thickness(0, 0, 6, 6) };
            button.Classes.Add("soft");
            button.Click += (_, _) =>
            {
                MinIntervalBox.Text = preset.MinMinutes.ToString();
                MaxIntervalBox.Text = preset.MaxMinutes.ToString();
                AutoVisitBox.IsChecked = true;
            };
            IntervalPresetPanel.Children.Add(button);
        }

        AnyMonitorBox.IsVisible = controller.ScreenCount > 1;
        StartupBox.IsVisible = StartupService.Supported;
        StartupHint.IsVisible = StartupService.Supported;
        ReloadFromController();

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        AboutText.Text =
            $"Crush ghé chơi {version?.ToString(3)} cho macOS (bản thử nghiệm) · .NET {Environment.Version}\n" +
            $"Dữ liệu: {AppPaths.DataDirectory}";
    }

    /// <summary>Nạp lại từ cài đặt hiện hành (khi mở, hoặc khi vừa đổi từ menu trên thanh menu).</summary>
    internal void ReloadFromController()
    {
        if (_controller is null)
        {
            return;
        }

        _working = _controller.Settings.Clone();
        var s = _working;
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

        PlaceRandom.IsChecked = s.Placement == PlacementMode.Random;
        CornerLeft.IsChecked = s.Placement == PlacementMode.Corner && s.Corner == ScreenCorner.BottomLeft;
        CornerRight.IsChecked = s.Placement == PlacementMode.Corner && s.Corner == ScreenCorner.BottomRight;
        AnyMonitorBox.IsChecked = s.MonitorDeviceName == AppSettings.AnyMonitor;
        StartupBox.IsChecked = s.RunAtStartup;

        SizeValue.Text = $"{SizeSlider.Value:0}";
        HeartValue.Text = ((int)HeartSlider.Value).ToString();
        OnPhotosChanged();
        RefreshStatus();
    }

    /// <summary>Cập nhật dòng trạng thái lịch (gọi khi tạm nghỉ/bật tắt từ menu).</summary>
    internal void RefreshStatus()
    {
        if (_controller is null)
        {
            return;
        }

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
        ResumeButton.IsVisible = paused;
    }

    /// <summary>Bộ "Crush của tôi" vừa thêm, sửa hoặc xóa hình.</summary>
    internal void OnPhotosChanged()
    {
        if (_controller is null)
        {
            return;
        }

        PhotoStrip.Children.Clear();
        var photos = _controller.UserSet.Load();
        foreach (var photo in photos)
        {
            PhotoStrip.Children.Add(PhotoTile(photo));
        }

        PhotosHint.Text = photos.Count == 0
            ? "Chưa có hình nào, crush đang tạm là một trái tim. Bấm “Thêm hình” để chọn hình crush đã tách nền (PNG nền trong suốt); ảnh thường cũng được nếu đã cài bộ tách nền."
            : $"{photos.Count} hình, mỗi lần ghé bốc một hình. Thêm, sửa, xóa có hiệu lực ngay.";
    }

    private Control PhotoTile(UserPhoto photo)
    {
        var image = new Image { Height = 96, MaxWidth = 150, Stretch = Stretch.Uniform };
        try
        {
            image.Source = PhotoIO.Thumbnail(Path.Combine(_controller!.UserSet.Directory, photo.File), 192);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Không đọc được hình {photo.File}: {ex.Message}");
        }

        Button Small(string text)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 3), FontSize = 11.5 };
            b.Classes.Add("soft");
            return b;
        }

        var edit = Small("Sửa");
        edit.IsEnabled = _controller!.UserSet.HasDraft(photo.Id);
        edit.Click += (_, _) => _controller.OpenAddPhotos(null, photo);
        var delete = Small("Xóa");
        delete.Click += async (_, _) =>
        {
            if (!await Dialogs.ConfirmAsync(this, "Xóa hình này khỏi bộ crush của bạn?", "Xóa", "Thôi"))
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
        };

        return new StackPanel
        {
            Margin = new Thickness(0, 0, 10, 10),
            Spacing = 6,
            Children =
            {
                new Border { Background = new SolidColorBrush(Color.Parse("#FFE8F0")), CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Child = image },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4, Children = { edit, delete } },
            },
        };
    }

    private bool TryRead(out AppSettings s)
    {
        s = _working.Clone();
        var error = ReadInto(s);
        MessageText.Text = error ?? string.Empty;
        return error is null;
    }

    private string? ReadInto(AppSettings s)
    {
        s.HeightDip = SizeSlider.Value;
        s.HeartPalette = (HeartPalette)Math.Max(0, PaletteBox.SelectedIndex);
        s.HeartCount = (int)HeartSlider.Value;
        s.ReducedMotion = ReducedMotionBox.IsChecked == true;

        s.AutoVisitEnabled = AutoVisitBox.IsChecked == true;
        if (!int.TryParse(MinIntervalBox.Text?.Trim(), out var min) || !int.TryParse(MaxIntervalBox.Text?.Trim(), out var max))
        {
            return "Khoảng thời gian ghé phải là số phút.";
        }

        if (min < VisitPlanner.MinimumIntervalMinutes)
        {
            return $"Khoảng ghé ngắn nhất là {VisitPlanner.MinimumIntervalMinutes} phút.";
        }

        if (max < min)
        {
            return "Số phút “đến” phải lớn hơn hoặc bằng số phút “từ”.";
        }

        if (max > VisitPlanner.MaximumIntervalMinutes)
        {
            return $"Khoảng ghé dài nhất là {VisitPlanner.MaximumIntervalMinutes} phút (10 giờ).";
        }

        s.MinIntervalMinutes = min;
        s.MaxIntervalMinutes = max;

        s.QuietHoursEnabled = QuietBox.IsChecked == true;
        if (!TryParseTime(QuietStartBox.Text, out var quietStart) || !TryParseTime(QuietEndBox.Text, out var quietEnd))
        {
            return "Giờ yên lặng nhập theo dạng 22:30.";
        }

        s.QuietStart = quietStart;
        s.QuietEnd = quietEnd;

        s.Greetings = (GreetingsBox.Text ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (s.Greetings.Count == 0)
        {
            return "Cần ít nhất một câu chào.";
        }

        if (s.Greetings.Any(g => g.Length > AppSettings.MaxGreetingLength))
        {
            return $"Mỗi câu chào tối đa {AppSettings.MaxGreetingLength} ký tự.";
        }

        s.Placement = PlaceRandom.IsChecked == true ? PlacementMode.Random : PlacementMode.Corner;
        if (s.Placement == PlacementMode.Corner)
        {
            s.Corner = CornerLeft.IsChecked == true ? ScreenCorner.BottomLeft : ScreenCorner.BottomRight;
        }

        s.MonitorDeviceName = AnyMonitorBox.IsVisible && AnyMonitorBox.IsChecked == true ? AppSettings.AnyMonitor : null;
        s.RunAtStartup = StartupBox.IsVisible && StartupBox.IsChecked == true;
        return null;
    }

    /// <summary>Nhận "22:30", "22h30", "7:05" hoặc "22".</summary>
    private static bool TryParseTime(string? text, out TimeOnly time)
    {
        time = default;
        var parts = (text ?? string.Empty).Trim().ToLowerInvariant().Replace('h', ':').Replace('.', ':').Split(':', StringSplitOptions.TrimEntries);
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

    private void Preview(PerformanceKind kind)
    {
        if (_controller is not null && TryRead(out var copy))
        {
            _controller.Preview(copy, kind);
        }
    }

    private void OnSizeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (SizeValue is not null)
        {
            SizeValue.Text = $"{e.NewValue:0}";
        }
    }

    private void OnHeartChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (HeartValue is not null)
        {
            HeartValue.Text = ((int)e.NewValue).ToString();
        }
    }

    private void OnAddPhotos(object? sender, RoutedEventArgs e) => _controller?.OpenAddPhotos();

    private void OnPreviewGreet(object? sender, RoutedEventArgs e) => Preview(PerformanceKind.Greet);

    private void OnPreviewKiss(object? sender, RoutedEventArgs e) => Preview(PerformanceKind.Kiss);

    private void OnPreviewBoth(object? sender, RoutedEventArgs e) => Preview(PerformanceKind.GreetThenKiss);

    private void OnResume(object? sender, RoutedEventArgs e)
    {
        _controller?.ResumeVisits();
        RefreshStatus();
    }

    private void OnResetGreetings(object? sender, RoutedEventArgs e) =>
        GreetingsBox.Text = string.Join(Environment.NewLine, AppSettings.DefaultGreetings);

    private void OnOpenData(object? sender, RoutedEventArgs e) => Shell.OpenFolder(AppPaths.DataDirectory);

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_controller is null || !TryRead(out var copy))
        {
            return;
        }

        if (_controller.ApplySettings(copy) is { } message)
        {
            await Dialogs.InfoAsync(this, message);
        }

        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

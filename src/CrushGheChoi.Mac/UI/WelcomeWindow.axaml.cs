using Avalonia.Controls;
using Avalonia.Interactivity;
using CrushGheChoi.Core.Animation;
using CrushGheChoi.Core.Settings;
using PlacementMode = CrushGheChoi.Core.Settings.PlacementMode;

namespace CrushGheChoi.UI;

/// <summary>Màn giới thiệu lần đầu: thêm hình crush, xem thử, chỉnh kích thước/vị trí, bật lịch.</summary>
public partial class WelcomeWindow : Window
{
    private readonly AppController? _controller;
    private bool _completed;

    public WelcomeWindow()
    {
        InitializeComponent();
    }

    internal WelcomeWindow(AppController controller)
        : this()
    {
        _controller = controller;
        var current = controller.Settings;
        IntervalBox.ItemsSource = AppSettings.IntervalPresets.Select(p => p.Label).ToList();
        var index = AppSettings.IntervalPresets.ToList().FindIndex(p =>
            p.MinMinutes == current.MinIntervalMinutes && p.MaxMinutes == current.MaxIntervalMinutes);
        IntervalBox.SelectedIndex = index >= 0 ? index : 3;
        OnPhotosChanged();
    }

    /// <summary>Cập nhật số hình sau khi thêm hình từ cửa sổ Thêm hình.</summary>
    internal void OnPhotosChanged()
    {
        var count = _controller?.UserSet.Load().Count ?? 0;
        PhotoCountText.Text = count == 0 ? "Chưa có hình nào" : $"Đã có {count} hình";
    }

    /// <summary>Đóng mà không lưu lựa chọn (khi thoát app).</summary>
    internal void ForceClose()
    {
        _completed = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Đóng bằng nút đóng cửa sổ cũng giữ lại các lựa chọn, để lần sau không hỏi lại.
        if (!_completed && _controller is not null)
        {
            _completed = true;
            _controller.CompleteWelcome(Chosen());
        }

        base.OnClosing(e);
    }

    private AppSettings Chosen()
    {
        var s = _controller!.Settings.Clone();
        s.HeightDip = SizeSmall.IsChecked == true ? AppSettings.SmallHeightDip
            : SizeLarge.IsChecked == true ? AppSettings.LargeHeightDip
            : AppSettings.DefaultHeightDip;
        s.Placement = PlaceRandom.IsChecked == true ? PlacementMode.Random : PlacementMode.Corner;
        s.Corner = CornerLeft.IsChecked == true ? ScreenCorner.BottomLeft : ScreenCorner.BottomRight;
        s.AutoVisitEnabled = AutoBox.IsChecked == true;
        if (IntervalBox.SelectedIndex >= 0)
        {
            var preset = AppSettings.IntervalPresets[IntervalBox.SelectedIndex];
            (s.MinIntervalMinutes, s.MaxIntervalMinutes) = (preset.MinMinutes, preset.MaxMinutes);
        }

        return s;
    }

    private void OnAddPhotos(object? sender, RoutedEventArgs e) => _controller?.OpenAddPhotos();

    private void OnPreview(object? sender, RoutedEventArgs e) => _controller?.Preview(Chosen(), PerformanceKind.GreetThenKiss);

    private void OnDone(object? sender, RoutedEventArgs e) => Close();
}

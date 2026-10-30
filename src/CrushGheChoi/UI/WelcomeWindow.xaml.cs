using System.ComponentModel;
using System.Windows;
using CrushGheChoi.Core.Animation;
using CrushGheChoi.Core.Settings;

namespace CrushGheChoi.UI;

/// <summary>Màn giới thiệu lần đầu: thêm hình crush, xem thử, chỉnh kích thước/vị trí, bật lịch rồi thu xuống khay.</summary>
internal partial class WelcomeWindow : Window
{
    private readonly AppController _controller;
    private bool _completed;

    public WelcomeWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
        var current = controller.Settings;
        foreach (var preset in AppSettings.IntervalPresets)
        {
            IntervalBox.Items.Add(preset.Label);
        }

        var index = AppSettings.IntervalPresets.ToList().FindIndex(p =>
            p.MinMinutes == current.MinIntervalMinutes && p.MaxMinutes == current.MaxIntervalMinutes);
        IntervalBox.SelectedIndex = index >= 0 ? index : 3;
        OnPhotosChanged();
    }

    /// <summary>Cập nhật số hình sau khi thêm hình từ cửa sổ Thêm hình.</summary>
    public void OnPhotosChanged()
    {
        var count = _controller.UserSet.Load().Count;
        PhotoCountText.Text = count == 0 ? "Chưa có hình nào" : $"Đã có {count} hình";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Đóng bằng nút X cũng giữ lại các lựa chọn, để lần sau không hỏi lại.
        if (!_completed)
        {
            Complete();
        }

        base.OnClosing(e);
    }

    private AppSettings Chosen()
    {
        var s = _controller.Settings.Clone();
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

    private void Complete()
    {
        _completed = true;
        _controller.CompleteWelcome(Chosen());
    }

    private void OnAddPhotos(object sender, RoutedEventArgs e) => _controller.OpenAddPhotos();

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        if (_controller.Preview(Chosen(), PerformanceKind.GreetThenKiss) is { } error)
        {
            MessageText.Text = error;
        }
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}

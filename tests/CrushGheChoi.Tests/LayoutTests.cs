using CrushGheChoi.Core.Layout;
using CrushGheChoi.Core.Settings;

namespace CrushGheChoi.Tests;

public class LayoutTests
{
    // Màn hình 2560×1440 ở 150%, taskbar cao 72 px; màn phụ đặt bên trái có tọa độ âm.
    private static readonly PixelRect WorkArea = new(0, 0, 2560, 1368);
    private static readonly PixelRect LeftMonitorWork = new(-1920, 0, 1920, 1032);

    [Fact]
    public void Bottom_right_sits_on_work_area_edge()
    {
        var layout = OverlayGeometry.Layout(300, 410, 520, ScreenCorner.BottomRight);
        var r = OverlayGeometry.Place(WorkArea, 1.5, layout, ScreenCorner.BottomRight, 8, 0);

        Assert.Equal(WorkArea.Bottom, r.Bottom);
        Assert.Equal(WorkArea.Right - 12, r.Right);
        Assert.Equal((int)Math.Ceiling(layout.WindowHeight * 1.5), r.Height);
        Assert.True(layout.InnerSideIsLeft);
    }

    [Fact]
    public void Bottom_left_on_negative_coordinates()
    {
        var layout = OverlayGeometry.Layout(300, 410, 520, ScreenCorner.BottomLeft);
        var r = OverlayGeometry.Place(LeftMonitorWork, 1.0, layout, ScreenCorner.BottomLeft, 20, 10);

        Assert.Equal(-1900, r.X);
        Assert.Equal(LeftMonitorWork.Bottom - 10, r.Bottom);
        Assert.False(layout.InnerSideIsLeft);
    }

    [Fact]
    public void Window_is_kept_inside_work_area()
    {
        var layout = OverlayGeometry.Layout(560, 410, 520, ScreenCorner.BottomRight);
        var r = OverlayGeometry.Place(new PixelRect(0, 0, 800, 600), 1.0, layout, ScreenCorner.BottomRight, 3000, 3000);

        Assert.True(r.X >= 0);
        Assert.True(r.Y >= 0);
    }

    [Theory]
    [InlineData(ScreenCorner.BottomRight, 8, 0, 1.5)]
    [InlineData(ScreenCorner.BottomLeft, 120, 40, 1.25)]
    [InlineData(ScreenCorner.BottomRight, 300, 200, 1.0)]
    public void Anchor_round_trips_through_image_rect(ScreenCorner corner, double ox, double oy, double scale)
    {
        var layout = OverlayGeometry.Layout(300, 410, 520, corner);
        var window = OverlayGeometry.Place(WorkArea, scale, layout, corner, ox, oy);
        var image = new PixelRect(
            window.X + (int)Math.Round(layout.ImageLeft * scale),
            window.Bottom - (int)Math.Round(layout.ImageHeight * scale),
            (int)Math.Round(layout.ImageWidth * scale),
            (int)Math.Round(layout.ImageHeight * scale));

        var (c, x, y) = OverlayGeometry.AnchorFromImage(image, WorkArea, scale, 300, 410, 520);

        Assert.Equal(corner, c);
        Assert.Equal(ox, x, tolerance: 2);
        Assert.Equal(oy, y, tolerance: 2);
    }

    [Fact]
    public void Dragging_to_other_half_switches_corner_without_moving_image()
    {
        const double scale = 1.0;
        var image = new PixelRect(300, 900, 237, 300);
        var (corner, ox, _) = OverlayGeometry.AnchorFromImage(image, WorkArea, scale, 300, 410, 520);

        Assert.Equal(ScreenCorner.BottomLeft, corner);
        var layout = OverlayGeometry.Layout(300, 410, 520, corner);
        var window = OverlayGeometry.Place(WorkArea, scale, layout, corner, ox, WorkArea.Bottom - image.Bottom);
        Assert.Equal(image.X, window.X + (int)layout.ImageLeft);
    }

    [Fact]
    public void Random_placement_stays_inside_work_area_and_faces_center()
    {
        var rng = new Random(5);
        var seenLeft = false;
        var seenRight = false;
        for (var i = 0; i < 500; i++)
        {
            var (r, side) = OverlayGeometry.PlaceRandom(LeftMonitorWork, 1.25, 300, 410, 520, rng);
            Assert.True(r.X >= LeftMonitorWork.X && r.Right <= LeftMonitorWork.Right, $"x {r.X}..{r.Right}");
            Assert.True(r.Y >= LeftMonitorWork.Y && r.Bottom <= LeftMonitorWork.Bottom, $"y {r.Y}..{r.Bottom}");
            var inLeftHalf = r.X + r.Width / 2.0 < LeftMonitorWork.X + LeftMonitorWork.Width / 2.0;
            Assert.Equal(inLeftHalf ? ScreenCorner.BottomLeft : ScreenCorner.BottomRight, side);
            seenLeft |= inLeftHalf;
            seenRight |= !inLeftHalf;
        }

        Assert.True(seenLeft && seenRight);
    }
}

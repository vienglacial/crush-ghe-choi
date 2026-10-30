using CrushGheChoi.Core.Settings;

namespace CrushGheChoi.Core.Layout;

/// <summary>Hình chữ nhật theo pixel vật lý của màn hình.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
}

/// <summary>
/// Bố cục cửa sổ crush (DIP). Ngoài ảnh còn chừa khoảng trên đầu cho tim và bong bóng,
/// khoảng rộng ở phía giữa màn hình (bong bóng) và khoảng hẹp hơn ở phía mép (đủ cho tim bay lệch ra).
/// </summary>
public readonly record struct OverlayLayout(
    double WindowWidth,
    double WindowHeight,
    double ImageLeft,
    double ImageTop,
    double ImageWidth,
    double ImageHeight,
    bool InnerSideIsLeft);

public static class OverlayGeometry
{
    public const double OuterPadding = 80;
    public const double InnerPadding = 210;
    public const double TopPadding = 170;

    public static OverlayLayout Layout(double imageHeightDip, int frameWidth, int frameHeight, ScreenCorner corner)
    {
        var imageWidth = imageHeightDip * frameWidth / frameHeight;
        var innerLeft = corner == ScreenCorner.BottomRight;
        return new OverlayLayout(
            OuterPadding + imageWidth + InnerPadding,
            TopPadding + imageHeightDip,
            innerLeft ? InnerPadding : OuterPadding,
            TopPadding,
            imageWidth,
            imageHeightDip,
            innerLeft);
    }

    /// <summary>Vị trí cửa sổ (pixel vật lý) trên vùng làm việc, không đè lên taskbar.</summary>
    public static PixelRect Place(PixelRect workArea, double scale, OverlayLayout layout, ScreenCorner corner, double offsetXDip, double offsetYDip)
    {
        var w = (int)Math.Ceiling(layout.WindowWidth * scale);
        var h = (int)Math.Ceiling(layout.WindowHeight * scale);
        var ox = (int)Math.Round(offsetXDip * scale);
        var oy = (int)Math.Round(offsetYDip * scale);
        var x = corner == ScreenCorner.BottomLeft ? workArea.X + ox : workArea.Right - ox - w;
        var y = workArea.Bottom - oy - h;
        return ClampInside(new PixelRect(x, y, w, h), workArea);
    }

    /// <summary>
    /// Một chỗ ngẫu nhiên trong vùng làm việc. Trả về cả "góc" tương ứng với nửa màn hình được chọn,
    /// để bố cục đặt bong bóng về phía giữa màn hình.
    /// </summary>
    public static (PixelRect Window, ScreenCorner Side) PlaceRandom(
        PixelRect workArea, double scale, double imageHeightDip, int frameWidth, int frameHeight, Random rng)
    {
        // Hai phía có cùng kích thước cửa sổ, chỉ khác bên chừa trống.
        var layout = Layout(imageHeightDip, frameWidth, frameHeight, ScreenCorner.BottomRight);
        var w = (int)Math.Ceiling(layout.WindowWidth * scale);
        var h = (int)Math.Ceiling(layout.WindowHeight * scale);
        var x = workArea.X + rng.Next(Math.Max(1, workArea.Width - w + 1));
        var y = workArea.Y + rng.Next(Math.Max(1, workArea.Height - h + 1));
        var window = ClampInside(new PixelRect(x, y, w, h), workArea);
        var side = window.X + window.Width / 2.0 < workArea.X + workArea.Width / 2.0
            ? ScreenCorner.BottomLeft
            : ScreenCorner.BottomRight;
        return (window, side);
    }

    public static PixelRect ClampInside(PixelRect r, PixelRect area) => r with
    {
        X = Math.Clamp(r.X, area.X, Math.Max(area.X, area.Right - r.Width)),
        Y = Math.Clamp(r.Y, area.Y, Math.Max(area.Y, area.Bottom - r.Height)),
    };

    /// <summary>
    /// Sau khi kéo: chọn góc theo nửa màn hình chứa ảnh crush và tính khoảng cách tới mép,
    /// giữ ảnh đúng chỗ người dùng thả dù phần chừa trống của cửa sổ đổi bên.
    /// </summary>
    public static (ScreenCorner Corner, double OffsetXDip, double OffsetYDip) AnchorFromImage(
        PixelRect imageRect, PixelRect workArea, double scale, double imageHeightDip, int frameWidth, int frameHeight)
    {
        var imageCenter = imageRect.X + imageRect.Width / 2.0;
        var corner = imageCenter < workArea.X + workArea.Width / 2.0 ? ScreenCorner.BottomLeft : ScreenCorner.BottomRight;
        var layout = Layout(imageHeightDip, frameWidth, frameHeight, corner);
        var windowLeft = imageRect.X - layout.ImageLeft * scale;
        var windowRight = windowLeft + layout.WindowWidth * scale;
        var ox = corner == ScreenCorner.BottomLeft ? windowLeft - workArea.X : workArea.Right - windowRight;
        var oy = workArea.Bottom - imageRect.Bottom;
        return (corner, Math.Max(0, ox / scale), Math.Max(0, oy / scale));
    }
}

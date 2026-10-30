using System.Windows;
using System.Windows.Media;
using CrushGheChoi.Core.Effects;
using CrushGheChoi.Core.Settings;

namespace CrushGheChoi.Overlay;

/// <summary>Vẽ các trái tim của <see cref="HeartSimulation"/> bằng một hình tim dùng chung.</summary>
internal sealed class HeartLayer : FrameworkElement
{
    // Hình tim trong hộp 24×24, dời tâm về gốc tọa độ và thu về kích thước 1.
    private static readonly Geometry HeartShape = CreateHeart();

    private Brush[] _brushes = CreateBrushes(HeartPalette.PinkRed);

    public HeartLayer()
    {
        IsHitTestVisible = false;
    }

    public HeartSimulation? Simulation { get; set; }

    public static int PaletteSize => 4;

    public void SetPalette(HeartPalette palette) => _brushes = CreateBrushes(palette);

    protected override void OnRender(DrawingContext dc)
    {
        if (Simulation is null)
        {
            return;
        }

        foreach (var h in Simulation.Active)
        {
            var opacity = h.Opacity;
            if (opacity <= 0.01)
            {
                continue;
            }

            var size = h.Size * h.Scale;
            var m = Matrix.Identity;
            m.Scale(size, size);
            m.Rotate(h.Rotation);
            m.Translate(h.DrawX, h.Y);

            dc.PushTransform(new MatrixTransform(m));
            dc.PushOpacity(opacity);
            dc.DrawGeometry(_brushes[h.ColorIndex % _brushes.Length], null, HeartShape);
            dc.Pop();
            dc.Pop();
        }
    }

    private static Geometry CreateHeart()
    {
        var g = Geometry.Parse(
            "M 12,21.35 L 10.55,20.03 C 5.4,15.36 2,12.28 2,8.5 C 2,5.42 4.42,3 7.5,3 C 9.24,3 10.91,3.81 12,5.09 " +
            "C 13.09,3.81 14.76,3 16.5,3 C 19.58,3 22,5.42 22,8.5 C 22,12.28 18.6,15.36 13.45,20.04 L 12,21.35 Z");
        var t = new TransformGroup();
        t.Children.Add(new TranslateTransform(-12, -12));
        t.Children.Add(new ScaleTransform(1 / 20.0, 1 / 20.0));
        var shaped = g.Clone();
        shaped.Transform = t;
        var flat = shaped.GetFlattenedPathGeometry(0.002, ToleranceType.Absolute);
        flat.Freeze();
        return flat;
    }

    private static Brush[] CreateBrushes(HeartPalette palette)
    {
        string[] colors = palette switch
        {
            HeartPalette.Purple => ["#B565F2", "#8E44E8", "#C98BFF", "#A35BD6"],
            HeartPalette.Pastel => ["#FFA8C5", "#FFC2D9", "#CDB4DB", "#A9D2F5"],
            _ => ["#FF4F8B", "#FF2D55", "#FF6FA8", "#E8174B"],
        };

        return colors.Select(hex =>
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var light = Color.FromArgb(255, Lift(c.R), Lift(c.G), Lift(c.B));
            var brush = new RadialGradientBrush(light, c)
            {
                GradientOrigin = new Point(0.32, 0.28),
                Center = new Point(0.4, 0.38),
                RadiusX = 0.75,
                RadiusY = 0.75,
            };
            brush.Freeze();
            return (Brush)brush;
        }).ToArray();
    }

    private static byte Lift(byte v) => (byte)(v + (255 - v) * 0.45);
}

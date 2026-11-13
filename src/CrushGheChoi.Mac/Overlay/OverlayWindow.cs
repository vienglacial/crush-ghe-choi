using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CrushGheChoi.Core.Animation;
using CrushGheChoi.Core.Effects;
using CrushGheChoi.Core.Layout;
using CrushGheChoi.Core.Settings;
using CrushGheChoi.Services;
using CorePixelRect = CrushGheChoi.Core.Layout.PixelRect;
using PlacementMode = CrushGheChoi.Core.Settings.PlacementMode;
using Path = Avalonia.Controls.Shapes.Path;

namespace CrushGheChoi.Overlay;

/// <summary>Một màn hình: vùng làm việc (pixel, không tính thanh menu và Dock) và tỷ lệ phóng.</summary>
/// <summary><c>Scale</c> đổi giữa đơn vị vị trí cửa sổ và đơn vị giao diện; <c>Density</c> là số pixel thật trên mỗi đơn vị giao diện (Retina = 2).</summary>
internal sealed record MonitorInfo(string Name, CorePixelRect WorkArea, double Scale, double Density);

/// <summary>Một lần ghé: màn nào, câu chào gì, dùng tùy chọn và ảnh nào, trên màn hình nào.</summary>
internal sealed record VisitRequest(PerformanceKind Kind, string? Greeting, AppSettings Settings, StillVariant Still, MonitorInfo Monitor);

/// <summary>
/// Cửa sổ trong suốt luôn ở trên, không giành bàn phím và cho chuột bấm xuyên.
/// Cùng kịch bản chuyển động với bản Windows; chỉ chạy vòng cập nhật khung hình trong lúc crush đang hiện.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private static readonly IBrush BubbleFill = new SolidColorBrush(Color.Parse("#FFFFFCFD"));
    private static readonly IBrush BubbleStroke = new SolidColorBrush(Color.Parse("#F5A3C0"));
    private static readonly IBrush BubbleInk = new SolidColorBrush(Color.Parse("#5A2340"));

    private readonly HeartSimulation _hearts = new() { PaletteSize = HeartLayer.PaletteSize };
    private readonly Stopwatch _clock = new();
    private readonly Random _rng = new();
    private readonly DispatcherTimer _timer;

    private readonly Canvas _root = new() { IsHitTestVisible = false };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly HeartLayer _heartLayer = new();
    private readonly Panel _bubble = new() { Opacity = 0 };
    private readonly TextBlock _bubbleText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14.5, Foreground = BubbleInk };
    private readonly Path _tail;
    private readonly ScaleTransform _bodyScale = new();
    private readonly RotateTransform _bodyRotate = new();
    private readonly TranslateTransform _bodyTranslate = new();
    private readonly ScaleTransform _bubbleScale = new();
    private readonly ScaleTransform _tailFlip = new();

    /// <summary>Sát mép dưới thì nhô lên từ mép; ở giữa màn hình thì hiện mờ dần và nhích lên nhẹ.</summary>
    private bool _docked;
    private bool _heartsWereActive;
    private double _lastT;
    private Session? _session;
    private OverlayLayout _layout;
    private CorePixelRect _rect;

    public OverlayWindow()
    {
        Title = "Crush ghé chơi";
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 400;
        Height = 400;

        _image.RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative);
        _image.RenderTransform = new TransformGroup { Children = { _bodyScale, _bodyRotate, _bodyTranslate } };
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);

        // Mép cắt nửa người mờ dần cho mềm.
        _image.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 0.9), new GradientStop(Colors.Transparent, 1) },
        };

        _heartLayer.Simulation = _hearts;

        _tail = new Path
        {
            Data = Geometry.Parse("M 0,0 L 22,14.5 L 16,0"),
            Fill = BubbleFill,
            Stroke = BubbleStroke,
            StrokeThickness = 1.5,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = _tailFlip,
        };
        _bubble.RenderTransform = _bubbleScale;
        _bubble.Children.Add(new Border
        {
            Background = BubbleFill,
            BorderBrush = BubbleStroke,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 9, 14, 10),
            MaxWidth = 240,
            Margin = new Thickness(0, 0, 0, 13),
            Child = _bubbleText,
        });
        _bubble.Children.Add(_tail);

        _root.Children.Add(_image);
        _root.Children.Add(_heartLayer);
        _root.Children.Add(_bubble);
        Content = _root;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => OnFrame());
        _timer.Stop();
        Opened += (_, _) => NativeWindow.MakeClickThroughOverlay(this);
    }

    public bool IsPlaying => _session is not null;

    public async Task PlayAsync(VisitRequest request, CancellationToken ct)
    {
        if (_session is not null)
        {
            return;
        }

        var s = request.Settings;
        var decodeHeight = Math.Min(request.Still.Height, (int)Math.Ceiling(s.HeightDip * request.Monitor.Density * 1.1));
        var still = await Task.Run(() => LoadStill(request.Still, decodeHeight));
        if (ct.IsCancellationRequested || _session is not null)
        {
            still.Dispose();
            return;
        }

        var session = new Session(request, still);
        _session = session;
        _hearts.Clear();
        _hearts.ReducedMotion = s.ReducedMotion;
        _heartLayer.SetPalette(s.HeartPalette);
        _image.Source = still;

        Place(request.Monitor, s, request.Still);
        PrepareBubble(request.Greeting);
        ApplyPose(Pose.Hidden(_layout.ImageHeight));

        using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => Finish(session)));
        try
        {
            Position = new PixelPoint(_rect.X, _rect.Y);
            Show();
            Position = new PixelPoint(_rect.X, _rect.Y);
            NativeWindow.MakeClickThroughOverlay(this);
            _clock.Restart();
            _lastT = 0;
            _timer.Start();
            await session.Completion.Task;
        }
        finally
        {
            Finish(session);
        }
    }

    public void Cancel()
    {
        if (_session is { } s)
        {
            Finish(s);
        }
    }

    private static Bitmap LoadStill(StillVariant still, int decodeHeight)
    {
        try
        {
            using var stream = File.OpenRead(still.Path);
            return Bitmap.DecodeToHeight(stream, Math.Max(16, decodeHeight), BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex)
        {
            Log.Warn($"Không đọc được hình, dùng hình nhúng sẵn: {ex.Message}");
            return new Bitmap(AssetLoader.Open(new Uri("avares://CrushGheChoi/Assets/heart.png")));
        }
    }

    private void Place(MonitorInfo monitor, AppSettings s, StillVariant still)
    {
        if (s.Placement == PlacementMode.Random)
        {
            var (rect, side) = OverlayGeometry.PlaceRandom(monitor.WorkArea, monitor.Scale, s.HeightDip, still.Width, still.Height, _rng);
            _layout = OverlayGeometry.Layout(s.HeightDip, still.Width, still.Height, side);
            _rect = rect;
        }
        else
        {
            _layout = OverlayGeometry.Layout(s.HeightDip, still.Width, still.Height, s.Corner);
            _rect = OverlayGeometry.Place(monitor.WorkArea, monitor.Scale, _layout, s.Corner, s.EdgeOffsetXDip, s.EdgeOffsetYDip);
        }

        _docked = _rect.Bottom >= monitor.WorkArea.Bottom - 1;

        Width = _layout.WindowWidth;
        Height = _layout.WindowHeight;
        _root.Width = _layout.WindowWidth;
        _root.Height = _layout.WindowHeight;
        _heartLayer.Width = _layout.WindowWidth;
        _heartLayer.Height = _layout.WindowHeight;
        _image.Width = _layout.ImageWidth;
        _image.Height = _layout.ImageHeight;
        Canvas.SetLeft(_image, _layout.ImageLeft);
        Canvas.SetTop(_image, _layout.ImageTop);
    }

    private void OnFrame()
    {
        if (_session is not { } session)
        {
            _timer.Stop();
            return;
        }

        var t = _clock.Elapsed.TotalSeconds;
        var dt = t - _lastT;
        _lastT = t;
        try
        {
            UpdateFrame(session, t, dt);
        }
        catch (Exception ex)
        {
            Log.Error("Lỗi khi phát chuyển động", ex);
            session.Completion.TrySetResult();
        }
    }

    private void UpdateFrame(Session session, double t, double dt)
    {
        var perf = session.Performance;
        if (t >= perf.Duration)
        {
            session.Completion.TrySetResult();
            return;
        }

        var step = perf.StepAt(t);
        ApplyPose(ComputePose(step, t - step.Start, session.Settings.ReducedMotion));

        if (!session.Released && perf.KissRelease is { } release && t >= release)
        {
            session.Released = true;
            EmitHearts(session);
        }

        _hearts.Update(dt);
        var active = !_hearts.IsIdle;
        if (active || _heartsWereActive)
        {
            _heartLayer.InvalidateVisual();
        }

        _heartsWereActive = active;
        UpdateBubble(session, t);
    }

    private void EmitHearts(Session session)
    {
        var still = session.Request.Still;
        var point = still.KissPoint;
        var x = _layout.ImageLeft + point.X * _layout.ImageWidth;
        var y = _layout.ImageTop + point.Y * _layout.ImageHeight;

        // Tim lệch về phía bàn tay, tức là ra xa gương mặt, để không bay ngang qua mặt.
        var drift = still.HeartDrift ?? (point.X < 0.5 ? -1 : 1);
        _hearts.Burst(x, y, session.Settings.HeartCount, drift);
    }

    private void PrepareBubble(string? text)
    {
        _bubble.Opacity = 0;
        _bubbleText.Text = text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var innerLeft = _layout.InnerSideIsLeft;
        _tail.HorizontalAlignment = innerLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _tail.Margin = innerLeft ? new Thickness(0, 0, 22, 0) : new Thickness(22, 0, 0, 0);
        _tailFlip.ScaleX = innerLeft ? 1 : -1;

        _bubble.Measure(Size.Infinity);
        var size = _bubble.DesiredSize;

        // Đặt trên đầu, lệch về phía giữa màn hình để không che mặt.
        var anchorX = _layout.ImageLeft + _layout.ImageWidth * (innerLeft ? 0.45 : 0.55);
        var left = innerLeft ? anchorX - size.Width : anchorX;
        var top = _layout.ImageTop + _layout.ImageHeight * 0.02 - size.Height;
        Canvas.SetLeft(_bubble, Math.Clamp(left, 4, Math.Max(4, _layout.WindowWidth - size.Width - 4)));
        Canvas.SetTop(_bubble, Math.Max(4, top));
        _bubble.RenderTransformOrigin = new RelativePoint(innerLeft ? 1 : 0, 1, RelativeUnit.Relative);
    }

    private void UpdateBubble(Session session, double t)
    {
        if (string.IsNullOrWhiteSpace(_bubbleText.Text))
        {
            return;
        }

        var perf = session.Performance;
        double opacity = 0, scale = 1;
        if (t >= perf.BubbleStart && t < perf.BubbleEnd)
        {
            var p = Ease.Clamp01((t - perf.BubbleStart) / 0.22);
            opacity = p;
            scale = session.Settings.ReducedMotion ? 1 : 0.85 + 0.15 * Ease.OutBack(p, 1.4);
        }
        else if (t >= perf.BubbleEnd && t < perf.BubbleEnd + 0.25)
        {
            var p = Ease.Clamp01((t - perf.BubbleEnd) / 0.25);
            opacity = 1 - p;
            scale = 1 - 0.05 * p;
        }

        _bubble.Opacity = opacity;
        _bubbleScale.ScaleX = scale;
        _bubbleScale.ScaleY = scale;
    }

    private Pose ComputePose(PerformanceStep step, double local, bool reduced)
    {
        var lean = _layout.InnerSideIsLeft ? -1 : 1; // nghiêng về phía giữa màn hình
        var hide = _layout.ImageHeight + 12;
        var p = Ease.Clamp01(local / step.Duration);

        switch (step.Kind)
        {
            case StepKind.Rise when reduced:
                return Pose.Neutral with { Opacity = Ease.InOut(p) };

            case StepKind.Rise when !_docked:
                // Ở giữa màn hình: hiện mờ dần, nhích lên và nở nhẹ.
                return Pose.Neutral with
                {
                    TranslateY = 26 * (1 - Ease.OutCubic(p)),
                    Scale = 0.94 + 0.06 * Ease.OutBack(p, 1.2),
                    Rotation = lean * 2 * (1 - Ease.OutCubic(p)),
                    Opacity = Ease.OutCubic(p),
                };

            case StepKind.Rise:
                return Pose.Neutral with
                {
                    TranslateY = hide * (1 - Ease.OutBack(p, 0.9)),
                    Rotation = lean * 3 * (1 - Ease.OutCubic(p)),
                    Opacity = Ease.Clamp01(local / 0.2),
                };

            case StepKind.Wave when !reduced:
            {
                // Hai nhịp nghiêng đầu nhẹ như đang vẫy chào.
                var env = Ease.Envelope(local, step.Duration, 0.3);
                var phase = Math.Sin(Math.Tau * local / 1.0);
                return Pose.Neutral with { Rotation = lean * 3.2 * phase * env, TranslateY = -3 * Math.Abs(phase) * env };
            }

            case StepKind.KissPrepare:
            {
                var k = Ease.InOut(p);
                return Pose.Neutral with
                {
                    Scale = 1 + 0.035 * k,
                    Rotation = reduced ? 0 : lean * 2.5 * k,
                    TranslateY = reduced ? 0 : -4 * k,
                };
            }

            case StepKind.Kiss:
            {
                // Đẩy nhẹ người về phía trước lúc hun, rồi trở về dáng thường.
                var scale = p < 0.3
                    ? Ease.Lerp(1.035, 1.06, Ease.OutCubic(p / 0.3))
                    : Ease.Lerp(1.06, 1.0, Ease.InOut((p - 0.3) / 0.7));
                var back = 1 - Ease.InOut(p);
                return Pose.Neutral with
                {
                    Scale = scale,
                    Rotation = reduced ? 0 : lean * 2.5 * back,
                    TranslateY = reduced ? 0 : -4 * back,
                };
            }

            case StepKind.Idle when !reduced:
                return Pose.Neutral with { ScaleY = 1 + 0.006 * Math.Sin(Math.Tau * local / 3.2) };

            case StepKind.Goodbye:
            {
                var sinkStart = step.Duration - Choreographer.SinkDuration;
                if (local < sinkStart)
                {
                    if (reduced)
                    {
                        return Pose.Neutral;
                    }

                    var env = Ease.Envelope(local, sinkStart, 0.2);
                    return Pose.Neutral with { Rotation = lean * 2 * Math.Sin(Math.Tau * local / 0.9) * env };
                }

                var q = Ease.Clamp01((local - sinkStart) / Choreographer.SinkDuration);
                if (reduced)
                {
                    return Pose.Neutral with { Opacity = 1 - q };
                }

                return _docked
                    ? Pose.Neutral with { TranslateY = hide * Ease.InCubic(q), Opacity = q > 0.75 ? 1 - (q - 0.75) / 0.25 : 1 }
                    : Pose.Neutral with { TranslateY = 22 * Ease.InCubic(q), Scale = 1 - 0.04 * q, Opacity = 1 - Ease.InOut(q) };
            }

            default:
                return Pose.Neutral;
        }
    }

    private void ApplyPose(Pose pose)
    {
        _bodyScale.ScaleX = pose.Scale;
        _bodyScale.ScaleY = pose.Scale * pose.ScaleY;
        _bodyRotate.Angle = pose.Rotation;
        _bodyTranslate.Y = pose.TranslateY;
        _image.Opacity = pose.Opacity;
    }

    private void Finish(Session session)
    {
        if (!ReferenceEquals(_session, session))
        {
            return;
        }

        _session = null;
        _timer.Stop();
        _clock.Stop();
        _hearts.Clear();
        _heartsWereActive = false;
        _heartLayer.InvalidateVisual();
        _bubble.Opacity = 0;
        Hide();
        _image.Source = null;
        session.Completion.TrySetResult();
        session.Still.Dispose();
    }

    private readonly record struct Pose(double TranslateY, double Rotation, double Scale, double ScaleY, double Opacity)
    {
        public static Pose Neutral => new(0, 0, 1, 1, 1);

        public static Pose Hidden(double height) => new(height + 12, 0, 1, 1, 0);
    }

    private sealed class Session(VisitRequest request, Bitmap still)
    {
        public VisitRequest Request { get; } = request;

        public AppSettings Settings => Request.Settings;

        public Bitmap Still { get; } = still;

        public Performance Performance { get; } = Choreographer.Build(request.Kind);

        public bool Released { get; set; }

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Vẽ các trái tim của <see cref="HeartSimulation"/> bằng một hình tim dùng chung.</summary>
internal sealed class HeartLayer : Control
{
    private static readonly Geometry HeartShape = Geometry.Parse(
        "M 12,21.35 L 10.55,20.03 C 5.4,15.36 2,12.28 2,8.5 C 2,5.42 4.42,3 7.5,3 C 9.24,3 10.91,3.81 12,5.09 " +
        "C 13.09,3.81 14.76,3 16.5,3 C 19.58,3 22,5.42 22,8.5 C 22,12.28 18.6,15.36 13.45,20.04 L 12,21.35 Z");

    private IBrush[] _brushes = CreateBrushes(HeartPalette.PinkRed);

    public HeartLayer()
    {
        IsHitTestVisible = false;
    }

    public HeartSimulation? Simulation { get; set; }

    public static int PaletteSize => 4;

    public void SetPalette(HeartPalette palette) => _brushes = CreateBrushes(palette);

    public override void Render(DrawingContext context)
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

            // Hình tim nằm trong hộp 24×24: dời tâm về gốc, thu về kích thước 1 rồi mới phóng, xoay, đặt chỗ.
            var size = h.Size * h.Scale / 20.0;
            var m = Matrix.CreateTranslation(-12, -12)
                * Matrix.CreateScale(size, size)
                * Matrix.CreateRotation(h.Rotation * Math.PI / 180)
                * Matrix.CreateTranslation(h.DrawX, h.Y);
            using (context.PushTransform(m))
            using (context.PushOpacity(opacity))
            {
                context.DrawGeometry(_brushes[h.ColorIndex % _brushes.Length], null, HeartShape);
            }
        }
    }

    private static IBrush[] CreateBrushes(HeartPalette palette)
    {
        string[] colors = palette switch
        {
            HeartPalette.Purple => ["#B565F2", "#8E44E8", "#C98BFF", "#A35BD6"],
            HeartPalette.Pastel => ["#FFA8C5", "#FFC2D9", "#CDB4DB", "#A9D2F5"],
            _ => ["#FF4F8B", "#FF2D55", "#FF6FA8", "#E8174B"],
        };

        return colors.Select(hex => (IBrush)new SolidColorBrush(Color.Parse(hex))).ToArray();
    }
}

internal static class Ease
{
    public static double Clamp01(double x) => Math.Clamp(x, 0, 1);

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static double InOut(double x)
    {
        x = Clamp01(x);
        return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    }

    public static double InCubic(double x) => Math.Pow(Clamp01(x), 3);

    public static double OutCubic(double x) => 1 - Math.Pow(1 - Clamp01(x), 3);

    public static double OutBack(double x, double s)
    {
        x = Clamp01(x);
        var c3 = s + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + s * Math.Pow(x - 1, 2);
    }

    /// <summary>Hệ số 0→1→0 mềm ở hai đầu một khoảng, dùng để chuyển động bắt đầu và kết thúc êm.</summary>
    public static double Envelope(double local, double duration, double edge)
    {
        var e = Math.Min(local / edge, (duration - local) / edge);
        return InOut(Clamp01(e));
    }
}

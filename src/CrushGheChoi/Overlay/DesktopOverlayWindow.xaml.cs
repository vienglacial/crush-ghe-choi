using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CrushGheChoi.Core.Animation;
using CrushGheChoi.Core.Effects;
using CrushGheChoi.Core.Layout;
using CrushGheChoi.Core.Settings;
using CrushGheChoi.Interop;
using CrushGheChoi.Services;

namespace CrushGheChoi.Overlay;

/// <summary>Một lần ghé: màn nào, câu chào gì, dùng tùy chọn, bộ hình và ảnh nào, trên màn hình nào.</summary>
internal sealed record VisitRequest(
    PerformanceKind Kind,
    string? Greeting,
    AppSettings Settings,
    AnimationSetDefinition Set,
    StillVariant Still,
    MonitorInfo Monitor)
{
    /// <summary>Clip chuyển động được vẽ khớp với ảnh chính, nên chỉ dùng khi lần này hiện ảnh chính.</summary>
    public bool UsesClips => !Set.IsStillOnly && ReferenceEquals(Still, Set.Primary);
}

/// <summary>
/// Cửa sổ trong suốt luôn ở trên, không có nút taskbar, không giành bàn phím và cho chuột bấm xuyên.
/// Chỉ chạy vòng cập nhật khung hình trong lúc crush đang hiện.
/// </summary>
internal partial class DesktopOverlayWindow : Window
{
    private const string EmbeddedStill = "pack://application:,,,/Assets/Crush/default/still.png";

    private readonly HeartSimulation _hearts = new() { PaletteSize = HeartLayer.PaletteSize };
    private readonly Stopwatch _clock = new();
    private readonly Random _rng = new();
    private IntPtr _hwnd;
    private bool _interactive;
    private bool _rendering;
    private bool _heartsWereActive;

    /// <summary>Sát mép dưới thì nhô lên từ mép; ở giữa màn hình thì hiện mờ dần và nhích lên nhẹ.</summary>
    private bool _docked;
    private TimeSpan _lastRenderingTime;
    private double _lastT;
    private VisitSession? _session;
    private MonitorInfo? _monitor;
    private OverlayLayout _layout;
    private PixelRect _rect;

    private AdjustSession? _adjust;
    private NativeMethods.POINT _dragStartCursor;
    private PixelRect _dragStartRect;

    public DesktopOverlayWindow()
    {
        InitializeComponent();
        Hearts.Simulation = _hearts;
        AdjustOutline.MouseLeftButtonDown += OnAdjustDragStart;
        AdjustOutline.MouseMove += OnAdjustDragMove;
        AdjustOutline.MouseLeftButtonUp += (_, _) => AdjustOutline.ReleaseMouseCapture();
    }

    /// <summary>Người dùng lưu vị trí mới: hình chữ nhật ảnh crush (pixel vật lý) trên màn hình nào.</summary>
    public event Action<PixelRect, MonitorInfo>? AdjustSaved;

    public event Action? AdjustCancelled;

    public bool IsPlaying => _session is not null;

    public bool IsAdjusting => _adjust is not null;

    public void Initialize()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        ApplyExStyle();
    }

    public async Task PlayAsync(VisitRequest request, CancellationToken ct)
    {
        if (_session is not null || _adjust is not null)
        {
            return;
        }

        var s = request.Settings;
        var decodeHeight = DecodeHeight(s, request.Still, request.Monitor);
        var session = await VisitSession.CreateAsync(request, decodeHeight);
        if (ct.IsCancellationRequested)
        {
            session.Dispose();
            return;
        }

        _session = session;
        _hearts.Clear();
        _hearts.ReducedMotion = s.ReducedMotion;
        Hearts.SetPalette(s.HeartPalette);
        CrushImage.Source = session.Still;

        Place(request.Monitor, s, request.Still, allowRandom: true);
        PrepareBubble(request.Greeting);
        ApplyPose(Pose.Hidden(_layout.ImageHeight));

        using var registration = ct.Register(() => Dispatcher.BeginInvoke(() => Finish(session)));
        try
        {
            await ShowPlacedAsync();
            _clock.Restart();
            _lastT = 0;
            StartRendering();
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

        if (_adjust is not null)
        {
            EndAdjust();
            AdjustCancelled?.Invoke();
        }
    }

    /// <summary>Hiện crush đứng yên ở vị trí hiện tại để người dùng kéo sang chỗ khác.</summary>
    public async void BeginAdjust(AppSettings settings, AnimationSetDefinition set, MonitorInfo monitor)
    {
        if (_session is { } playing)
        {
            Finish(playing);
        }

        _adjust = new AdjustSession(settings, set);
        _hearts.Clear();
        Hearts.InvalidateVisual();
        Bubble.Opacity = 0;
        // Chỉnh vị trí luôn dùng ảnh chính và vị trí góc (kéo xong sẽ chuyển sang vị trí cố định).
        CrushImage.Source = LoadStill(set.Primary, DecodeHeight(settings, set.Primary, monitor));
        Place(monitor, settings, set.Primary, allowRandom: false);
        ApplyPose(Pose.Neutral);

        const double pad = 8;
        Canvas.SetLeft(AdjustOutline, _layout.ImageLeft - pad);
        Canvas.SetTop(AdjustOutline, _layout.ImageTop - pad);
        AdjustOutline.Width = _layout.ImageWidth + pad * 2;
        AdjustOutline.Height = _layout.ImageHeight + pad;
        AdjustOutline.Visibility = Visibility.Visible;

        AdjustPanel.Visibility = Visibility.Visible;
        AdjustPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var panelLeft = _layout.ImageLeft + (_layout.ImageWidth - AdjustPanel.DesiredSize.Width) / 2;
        Canvas.SetLeft(AdjustPanel, Math.Clamp(panelLeft, 4, Math.Max(4, _layout.WindowWidth - AdjustPanel.DesiredSize.Width - 4)));
        Canvas.SetTop(AdjustPanel, Math.Max(4, _layout.ImageTop - AdjustPanel.DesiredSize.Height - 16));

        _interactive = true;
        Focusable = true;
        ApplyExStyle();
        await ShowPlacedAsync();
        Activate();
        AdjustSaveButton.Focus();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // WPF tự co giãn theo tỷ lệ màn hình mới; đặt lại đúng kích thước vật lý đã tính.
        if (_monitor is not null)
        {
            Dispatcher.BeginInvoke(() => MoveTo(_rect));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_adjust is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            OnAdjustCancel(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OnAdjustSave(this, e);
            e.Handled = true;
        }
    }

    private static int DecodeHeight(AppSettings s, StillVariant still, MonitorInfo monitor) =>
        Math.Min(still.Height, (int)Math.Ceiling(s.HeightDip * monitor.Scale * 1.1));

    private static ImageSource LoadStill(StillVariant still, int decodeHeight)
    {
        try
        {
            return FrameSequence.LoadBitmap(still.Path, decodeHeight);
        }
        catch (Exception ex)
        {
            Log.Warn($"Không đọc được ảnh đứng, dùng ảnh nhúng sẵn: {ex.Message}");
            var bmp = new BitmapImage(new Uri(EmbeddedStill));
            bmp.Freeze();
            return bmp;
        }
    }

    private void Place(MonitorInfo monitor, AppSettings s, StillVariant still, bool allowRandom)
    {
        _monitor = monitor;
        if (allowRandom && s.Placement == PlacementMode.Random)
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

        Root.Width = _layout.WindowWidth;
        Root.Height = _layout.WindowHeight;
        Hearts.Width = _layout.WindowWidth;
        Hearts.Height = _layout.WindowHeight;
        CrushImage.Width = _layout.ImageWidth;
        CrushImage.Height = _layout.ImageHeight;
        Canvas.SetLeft(CrushImage, _layout.ImageLeft);
        Canvas.SetTop(CrushImage, _layout.ImageTop);
        MoveTo(_rect);
    }

    private async Task ShowPlacedAsync()
    {
        MoveTo(_rect);
        if (!IsVisible)
        {
            Show();
        }

        ApplyExStyle();
        MoveTo(_rect);

        // Cho WPF xử lý xong việc đổi tỷ lệ (nếu vừa chuyển sang màn hình khác) rồi đặt lại lần cuối.
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        MoveTo(_rect);
    }

    private void MoveTo(PixelRect r)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            r.X,
            r.Y,
            r.Width,
            r.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    private void ApplyExStyle()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = NativeMethods.GetExStyle(_hwnd);
        style |= NativeMethods.WS_EX_TOOLWINDOW;
        style &= ~NativeMethods.WS_EX_APPWINDOW;
        style = _interactive
            ? style & ~(NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE)
            : style | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetExStyle(_hwnd, style);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE && !_interactive)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    private void StartRendering()
    {
        if (!_rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
    }

    private void StopRendering()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Rendering có thể gọi nhiều lần trong cùng một khung hình.
        if (e is RenderingEventArgs re)
        {
            if (re.RenderingTime == _lastRenderingTime)
            {
                return;
            }

            _lastRenderingTime = re.RenderingTime;
        }

        if (_session is not { } session)
        {
            StopRendering();
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

    private void UpdateFrame(VisitSession session, double t, double dt)
    {
        var perf = session.Performance;
        if (t >= perf.Duration)
        {
            session.Completion.TrySetResult();
            return;
        }

        var step = perf.StepAt(t);
        var local = t - step.Start;
        var clip = Choreographer.ClipFor(session.Set, step.Kind);
        var sequence = clip is null ? null : session.Sequences.GetValueOrDefault(clip.Kind);

        if (sequence is not null)
        {
            var index = clip!.Loop ? (int)(t * session.Set.Fps) : (int)(local * session.Set.Fps);
            if (sequence.Get(index) is { } frame && !ReferenceEquals(CrushImage.Source, frame))
            {
                CrushImage.Source = frame;
            }
        }
        else if (!ReferenceEquals(CrushImage.Source, session.Still))
        {
            CrushImage.Source = session.Still;
        }

        ApplyPose(ComputePose(step, local, hasClip: sequence is not null, session.Settings.ReducedMotion));

        if (!session.Released && perf.KissRelease is { } release && t >= release)
        {
            session.Released = true;
            EmitHearts(session, clip);
        }

        _hearts.Update(dt);
        var active = !_hearts.IsIdle;
        if (active || _heartsWereActive)
        {
            Hearts.InvalidateVisual();
        }

        _heartsWereActive = active;
        UpdateBubble(session, t);
    }

    private void EmitHearts(VisitSession session, ClipDefinition? clip)
    {
        var still = session.Request.Still;
        var point = (session.Request.UsesClips ? clip?.KissPoint : null) ?? still.KissPoint;
        var local = new Point(point.X * CrushImage.ActualWidth, point.Y * CrushImage.ActualHeight);
        var origin = CrushImage.TransformToAncestor(Root).Transform(local);

        // Tim lệch về phía bàn tay, tức là ra xa gương mặt, để không bay ngang qua mặt.
        var drift = still.HeartDrift ?? (point.X < 0.5 ? -1 : 1);
        _hearts.Burst(origin.X, origin.Y, session.Settings.HeartCount, drift);
    }

    private void PrepareBubble(string? text)
    {
        Bubble.Opacity = 0;
        BubbleText.Text = text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var innerLeft = _layout.InnerSideIsLeft;
        BubbleTail.HorizontalAlignment = innerLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        BubbleTail.Margin = innerLeft ? new Thickness(0, 0, 22, 0) : new Thickness(22, 0, 0, 0);
        BubbleTailFlip.ScaleX = innerLeft ? 1 : -1;

        Bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Bubble.DesiredSize;

        // Đặt trên đầu, lệch về phía giữa màn hình để không che mặt.
        var anchorX = _layout.ImageLeft + _layout.ImageWidth * (innerLeft ? 0.45 : 0.55);
        var left = innerLeft ? anchorX - size.Width : anchorX;
        var top = _layout.ImageTop + _layout.ImageHeight * 0.02 - size.Height;
        Canvas.SetLeft(Bubble, Math.Clamp(left, 4, Math.Max(4, _layout.WindowWidth - size.Width - 4)));
        Canvas.SetTop(Bubble, Math.Max(4, top));
        Bubble.RenderTransformOrigin = new Point(innerLeft ? 1 : 0, 1);
    }

    private void UpdateBubble(VisitSession session, double t)
    {
        if (string.IsNullOrWhiteSpace(BubbleText.Text))
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

        Bubble.Opacity = opacity;
        BubbleScale.ScaleX = scale;
        BubbleScale.ScaleY = scale;
    }

    private Pose ComputePose(PerformanceStep step, double local, bool hasClip, bool reduced)
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

            case StepKind.Wave when !hasClip && !reduced:
            {
                // Hai nhịp nghiêng đầu nhẹ như đang vẫy chào.
                var env = Ease.Envelope(local, step.Duration, 0.3);
                var phase = Math.Sin(Math.Tau * local / 1.0);
                return Pose.Neutral with
                {
                    Rotation = lean * 3.2 * phase * env,
                    TranslateY = -3 * Math.Abs(phase) * env,
                };
            }

            case StepKind.KissPrepare when !hasClip:
            {
                var k = Ease.InOut(p);
                return Pose.Neutral with
                {
                    Scale = 1 + 0.035 * k,
                    Rotation = reduced ? 0 : lean * 2.5 * k,
                    TranslateY = reduced ? 0 : -4 * k,
                };
            }

            case StepKind.Kiss when !hasClip:
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

            case StepKind.Idle when !hasClip && !reduced:
                return Pose.Neutral with { ScaleY = 1 + 0.006 * Math.Sin(Math.Tau * local / 3.2) };

            case StepKind.Goodbye:
            {
                var sinkStart = step.Duration - Choreographer.SinkDuration;
                if (local < sinkStart)
                {
                    if (hasClip || reduced)
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
                    ? Pose.Neutral with
                    {
                        TranslateY = hide * Ease.InCubic(q),
                        Opacity = q > 0.75 ? 1 - (q - 0.75) / 0.25 : 1,
                    }
                    : Pose.Neutral with
                    {
                        TranslateY = 22 * Ease.InCubic(q),
                        Scale = 1 - 0.04 * q,
                        Opacity = 1 - Ease.InOut(q),
                    };
            }

            default:
                return Pose.Neutral;
        }
    }

    private void ApplyPose(Pose pose)
    {
        BodyScale.ScaleX = pose.Scale;
        BodyScale.ScaleY = pose.Scale * pose.ScaleY;
        BodyRotate.Angle = pose.Rotation;
        BodyTranslate.Y = pose.TranslateY;
        CrushImage.Opacity = pose.Opacity;
    }

    private void Finish(VisitSession session)
    {
        if (!ReferenceEquals(_session, session))
        {
            return;
        }

        _session = null;
        StopRendering();
        _clock.Stop();
        _hearts.Clear();
        _heartsWereActive = false;
        Hearts.InvalidateVisual();
        Bubble.Opacity = 0;
        Hide();
        CrushImage.Source = null;
        session.Completion.TrySetResult();
        session.Dispose();
    }

    private void EndAdjust()
    {
        _adjust = null;
        AdjustOutline.ReleaseMouseCapture();
        AdjustOutline.Visibility = Visibility.Collapsed;
        AdjustPanel.Visibility = Visibility.Collapsed;
        _interactive = false;
        Focusable = false;
        ApplyExStyle();
        Hide();
        CrushImage.Source = null;
    }

    private void OnAdjustDragStart(object sender, MouseButtonEventArgs e)
    {
        NativeMethods.GetCursorPos(out _dragStartCursor);
        _dragStartRect = _rect;
        AdjustOutline.CaptureMouse();
        e.Handled = true;
    }

    private void OnAdjustDragMove(object sender, MouseEventArgs e)
    {
        if (!AdjustOutline.IsMouseCaptured || _monitor is null)
        {
            return;
        }

        NativeMethods.GetCursorPos(out var now);
        var moved = _dragStartRect.Offset(now.X - _dragStartCursor.X, now.Y - _dragStartCursor.Y);
        _rect = OverlayGeometry.ClampInside(moved, _monitor.WorkArea);
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            _rect.X,
            _rect.Y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    private void OnAdjustSave(object sender, RoutedEventArgs e)
    {
        if (_adjust is null || _monitor is null)
        {
            return;
        }

        var scale = _monitor.Scale;
        var imageWidth = (int)Math.Round(_layout.ImageWidth * scale);
        var imageHeight = (int)Math.Round(_layout.ImageHeight * scale);
        var imageRect = new PixelRect(
            _rect.X + (int)Math.Round(_layout.ImageLeft * scale),
            _rect.Bottom - imageHeight,
            imageWidth,
            imageHeight);
        var monitor = _monitor;
        EndAdjust();
        AdjustSaved?.Invoke(imageRect, monitor);
    }

    private void OnAdjustCancel(object sender, RoutedEventArgs e)
    {
        if (_adjust is null)
        {
            return;
        }

        EndAdjust();
        AdjustCancelled?.Invoke();
    }

    private readonly record struct Pose(double TranslateY, double Rotation, double Scale, double ScaleY, double Opacity)
    {
        public static Pose Neutral => new(0, 0, 1, 1, 1);

        public static Pose Hidden(double height) => new(height + 12, 0, 1, 1, 0);
    }

    private sealed record AdjustSession(AppSettings Settings, AnimationSetDefinition Set);

    private sealed class VisitSession : IDisposable
    {
        private VisitSession(VisitRequest request, ImageSource still, Dictionary<ClipKind, FrameSequence> sequences)
        {
            Request = request;
            Still = still;
            Sequences = sequences;
            Performance = Choreographer.Build(request.Kind, request.UsesClips ? request.Set : null);
        }

        public VisitRequest Request { get; }

        public AppSettings Settings => Request.Settings;

        public AnimationSetDefinition Set => Request.Set;

        public Performance Performance { get; }

        public ImageSource Still { get; }

        public Dictionary<ClipKind, FrameSequence> Sequences { get; }

        public bool Released { get; set; }

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<VisitSession> CreateAsync(VisitRequest request, int decodeHeight)
        {
            var set = request.Set;
            var still = LoadStill(request.Still, decodeHeight);

            // Giải mã sẵn vài khung đầu của mỗi động tác ở luồng nền để lúc phát không bị khựng.
            IEnumerable<ClipDefinition> clips = request.UsesClips ? set.Clips.Values : [];
            var sequences = clips.ToDictionary(c => c.Kind, c => new FrameSequence(c.FramePaths, decodeHeight, c.Loop));
            await Task.Run(() =>
            {
                foreach (var seq in sequences.Values)
                {
                    seq.WarmUp();
                }
            });

            return new VisitSession(request, still, sequences);
        }

        public void Dispose()
        {
            foreach (var seq in Sequences.Values)
            {
                seq.Dispose();
            }
        }
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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CrushGheChoi.Core.Cutout;
using CrushGheChoi.Cutout;
using CrushGheChoi.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Win32;

namespace CrushGheChoi.UI;

internal enum PhotoState
{
    Waiting,
    Processing,
    Ready,
    Failed,

    /// <summary>Ảnh còn nền mà chưa cài bộ tách nền.</summary>
    NeedsCutout,
}

/// <summary>Một ảnh trong cửa sổ Thêm hình: đang chờ, đang tách nền, đã xong (đang chỉnh) hoặc lỗi.</summary>
internal sealed class PhotoItem(string name, string? sourcePath, string? existingId) : INotifyPropertyChanged
{
    private string _status = "Đang chờ tới lượt";
    private BitmapSource? _thumbnail;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; } = name;

    /// <summary>Ảnh gốc, với hình mới thêm.</summary>
    public string? SourcePath { get; } = sourcePath;

    /// <summary>Mã hình trong bộ, khi mở hình đã có để chỉnh lại.</summary>
    public string? ExistingId { get; } = existingId;

    public PhotoState State { get; set; }

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            OnChanged();
        }
    }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            OnChanged();
        }
    }

    public CutoutDraft? Draft { get; set; }

    public CutoutPreview? Preview { get; set; }

    public CutoutEdits? Suggested { get; set; }

    public CutoutEdits? Edits { get; set; }

    public bool Changed { get; set; }

    public Stack<CutoutEdits> History { get; } = new();

    private void OnChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>
/// Thêm hình crush: hình PNG đã tách nền dùng luôn; ảnh thường thì app tự tách nền nếu người dùng đã tự cài bộ tách nền.
/// Sau đó người dùng chỉnh đường cắt, chỗ tim bay ra và tẩy chỗ dính nền trước khi lưu vào bộ "Crush của tôi".
/// Cũng dùng để chỉnh lại hình đã có.
/// </summary>
internal partial class AddPhotosWindow : Window
{
    private const int PreviewMaxSide = 1000;
    private const int ThumbnailHeight = 104;

    private readonly AppController _controller;
    private readonly UserSetStore _store;
    private readonly ObservableCollection<PhotoItem> _items = [];
    private readonly CutoutService _service = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Brush[] _backgrounds;
    private Task? _processing;
    private PhotoItem? _current;
    private WriteableBitmap? _bitmap;
    private double _displayScale = 1;
    private DragMode _drag;
    private Point _lastErase;
    private bool _memoryAccepted;
    private bool _syncing;
    private bool _forceClose;
    private int _backgroundIndex;

    public AddPhotosWindow(AppController controller, UserSetStore store)
    {
        InitializeComponent();
        _controller = controller;
        _store = store;
        _backgrounds = [(Brush)FindResource("CheckerBrush"), (Brush)FindResource("DarkStageBrush"), (Brush)FindResource("DesktopStageBrush")];
        PhotoList.ItemsSource = _items;
        ShowCurrent();
        UpdateFooter();
    }

    private enum DragMode
    {
        None,
        Cut,
        Heart,
        Erase,
    }

    /// <summary>Thêm ảnh vào hàng chờ tách nền.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var added = new List<PhotoItem>();
        foreach (var path in list.Where(p => PhotoIO.LooksLikePhoto(p) && File.Exists(p)))
        {
            if (_items.Any(i => string.Equals(i.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var item = new PhotoItem(Path.GetFileName(path), path, null);
            _items.Add(item);
            added.Add(item);
            _ = LoadThumbnailAsync(item, path);
        }

        if (added.Count == 0)
        {
            if (list.Count > 0)
            {
                FooterText.Text = "Không có ảnh nào dùng được trong các tệp vừa chọn (cần JPG, PNG, WEBP, HEIC…).";
            }

            return;
        }

        if (_current is null)
        {
            PhotoList.SelectedItem = added[0];
        }

        UpdateFooter();
        StartProcessing();
    }

    /// <summary>Mở một hình đã có trong bộ để chỉnh lại (cần bản nháp lưu lúc thêm).</summary>
    public async void EditExisting(UserPhoto photo)
    {
        if (_items.FirstOrDefault(i => i.ExistingId == photo.Id) is { } open)
        {
            PhotoList.SelectedItem = open;
            return;
        }

        var item = new PhotoItem("Hình đã có", null, photo.Id) { State = PhotoState.Processing, Status = "Đang mở…" };
        _items.Add(item);
        PhotoList.SelectedItem = item;
        try
        {
            var loaded = _store.LoadDraftEdits(photo.Id) ?? throw new InvalidDataException("Hình này không có bản nháp để chỉnh lại.");
            var draftPath = _store.DraftImagePath(photo.Id);
            var stillPath = _store.StillPath(photo.Id);
            var (draft, preview, thumb) = await Task.Run(() =>
            {
                var d = PhotoIO.LoadDraft(draftPath, loaded.Face);
                return (d, new CutoutPreview(d, PreviewMaxSide), PhotoIO.Thumbnail(stillPath, ThumbnailHeight));
            });

            item.Draft = draft;
            item.Preview = preview;
            item.Thumbnail = thumb;
            item.Edits = loaded.Edits;
            item.Suggested = CutoutPlanner.Suggest(draft);
            item.State = PhotoState.Ready;
            item.Status = "Đang chỉnh lại";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            item.State = PhotoState.Failed;
            item.Status = $"Không mở được: {ex.Message}";
        }

        if (_current == item)
        {
            ShowCurrent();
        }

        UpdateFooter();
    }

    /// <summary>Đóng không hỏi (khi thoát app).</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var unsaved = _items.Count(i => i.State == PhotoState.Processing || (i.State == PhotoState.Ready && (i.ExistingId is null || i.Changed)));
        if (!_forceClose && unsaved > 0
            && MessageBox.Show(this, $"Còn {unsaved} hình chưa lưu. Đóng và bỏ các hình này?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing.Cancel();
        _ = ReleaseWhenIdleAsync();
        base.OnClosed(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // Người dùng có thể tự chép tệp model vào thư mục models trong lúc cửa sổ đang mở.
        if (_items.Any(i => i.State == PhotoState.NeedsCutout) && ModelStore.SegmenterReady)
        {
            AfterModelReady();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnUndo(this, e);
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    // ----- Hàng chờ tách nền -----

    private void StartProcessing()
    {
        if (_processing is null || _processing.IsCompleted)
        {
            _processing = ProcessQueueAsync();
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (!_closing.IsCancellationRequested && _items.FirstOrDefault(i => i.State == PhotoState.Waiting) is { } item)
            {
                var path = item.SourcePath!;
                var canSegment = ModelStore.SegmenterReady;

                // Ảnh JPG/HEIC chắc chắn còn nền: sắp tách bằng model nặng thì hỏi trước nếu máy ít RAM trống.
                if (canSegment && PhotoIO.IsAlwaysOpaque(path) && !_memoryAccepted && !ConfirmMemory())
                {
                    return;
                }

                item.State = PhotoState.Processing;
                item.Status = "Đang xử lý…";
                if (_current == item)
                {
                    ShowCurrent();
                }

                UpdateFooter();
                var progress = new Progress<string>(text =>
                {
                    item.Status = text;
                    if (_current == item)
                    {
                        BusyText.Text = text;
                    }
                });

                var started = DateTime.Now;
                try
                {
                    var (draft, preview) = await Task.Run(() =>
                    {
                        var d = _service.Process(path, canSegment, progress, _closing.Token);
                        return (d, d is null ? null : new CutoutPreview(d, PreviewMaxSide));
                    });

                    if (draft is null || preview is null)
                    {
                        item.State = PhotoState.NeedsCutout;
                        item.Status = "Ảnh còn nền, cần tách nền";
                    }
                    else
                    {
                        item.Draft = draft;
                        item.Preview = preview;
                        item.Suggested = CutoutPlanner.Suggest(draft);
                        item.Edits = item.Suggested;
                        item.State = PhotoState.Ready;
                        item.Status = draft.Face is null ? "Xong. Không thấy gương mặt, nhớ đặt chỗ tim" : "Xong, kiểm tra lại nha";
                        Log.Info($"Chuẩn bị xong {item.Name} trong {(DateTime.Now - started).TotalSeconds:0.#} giây");
                    }

                    if (!_items.Contains(item))
                    {
                        continue; // bị bỏ khỏi danh sách trong lúc chờ
                    }

                    if (_current == item || (item.State == PhotoState.Ready && _current is not { State: PhotoState.Ready }))
                    {
                        PhotoList.SelectedItem = item;
                        ShowCurrent();
                    }
                }
                catch (OperationCanceledException)
                {
                    item.State = PhotoState.Waiting;
                    item.Status = "Đang chờ tới lượt";
                    break;
                }
                catch (Exception ex)
                {
                    // Ảnh lỗi (định dạng lạ, hết RAM, model hỏng…) chỉ làm hỏng ảnh đó, các ảnh khác vẫn tiếp tục.
                    item.State = PhotoState.Failed;
                    item.Status = FriendlyError(ex);
                    Log.Warn($"Không xử lý được {item.Name}: {ex}");
                    if (_current == item)
                    {
                        ShowCurrent();
                    }
                }

                UpdateFooter();
            }
        }
        finally
        {
            // Model tách nền chiếm vài GB: trả lại ngay khi hết ảnh chờ.
            _service.ReleaseSegmenter();
            UpdateFooter();
        }
    }

    private bool ConfirmMemory()
    {
        var free = CutoutService.AvailablePhysicalMemory();
        if (free >= CutoutService.RecommendedFreeBytes * 0.9)
        {
            _memoryAccepted = true;
            return true;
        }

        var answer = MessageBox.Show(this,
            $"Máy đang còn trống khoảng {free / 1073741824.0:0.#} GB RAM. Tách nền cần khoảng 6 GB, nên đóng bớt ứng dụng nặng (trình duyệt nhiều tab, phần mềm dựng phim…) cho chắc.\n\nVẫn tách nền luôn?",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
        {
            _memoryAccepted = true;
            return true;
        }

        FooterText.Text = "Đã tạm dừng. Đóng bớt ứng dụng rồi bấm vào một ảnh đang chờ để tách tiếp.";
        return false;
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        OutOfMemoryException => "Không đủ RAM. Đóng bớt ứng dụng rồi thêm lại ảnh này.",
        OnnxRuntimeException o when o.Message.Contains("alloc", StringComparison.OrdinalIgnoreCase) => "Không đủ RAM. Đóng bớt ứng dụng rồi thêm lại ảnh này.",
        OnnxRuntimeException => "Bộ tách nền bị lỗi. Thử thêm lại ảnh; nếu vẫn lỗi, xóa thư mục models trong dữ liệu app để tải lại.",
        NotSupportedException or FileFormatException or COMException or ArgumentException =>
            "Không đọc được ảnh này. Ảnh HEIC cần cài “HEIF Image Extensions” từ Microsoft Store, hoặc đổi sang JPG/PNG.",
        IOException or UnauthorizedAccessException => $"Không mở được tệp: {ex.Message}",
        _ => $"Lỗi khi tách nền: {ex.Message}",
    };

    private async Task LoadThumbnailAsync(PhotoItem item, string path)
    {
        try
        {
            item.Thumbnail = await Task.Run(() => PhotoIO.Thumbnail(path, ThumbnailHeight));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or COMException or ArgumentException or UnauthorizedAccessException)
        {
            // Không có ảnh nhỏ thì thôi; lỗi thật sẽ hiện khi tách nền.
        }
    }

    private async Task ReleaseWhenIdleAsync()
    {
        if (_processing is { } running)
        {
            try
            {
                await running;
            }
            catch (Exception ex)
            {
                Log.Warn($"Hàng chờ tách nền dừng với lỗi: {ex.Message}");
            }
        }

        _service.Dispose();
    }

    // ----- Bộ tách nền (người dùng tự tải) -----

    private void OnOpenModelPage(object sender, RoutedEventArgs e)
    {
        // Trình duyệt tải tệp; app không tự tải gì về.
        Process.Start(new ProcessStartInfo(ModelStore.Segmenter.Url.AbsoluteUri) { UseShellExecute = true });
        ModelText.Text = $"Trình duyệt đang tải {ModelStore.Segmenter.FileName}. Tải xong thì bấm “Chọn tệp đã tải…” (tệp thường nằm trong thư mục Downloads).";
    }

    private async void OnImportModel(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Chọn tệp {ModelStore.Segmenter.FileName}",
            Filter = "Model ONNX (*.onnx)|*.onnx",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ImportModelButton.IsEnabled = false;
        ModelText.Text = "Đang kiểm tra và cài tệp…";
        try
        {
            await ModelStore.ImportAsync(dialog.FileName, _closing.Token);
            ModelText.Text = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            ModelText.Text = ex is OperationCanceledException ? string.Empty : ex.Message;
        }
        finally
        {
            ImportModelButton.IsEnabled = true;
        }

        AfterModelReady();
    }

    private void AfterModelReady()
    {
        if (!ModelStore.SegmenterReady)
        {
            return;
        }

        foreach (var item in _items.Where(i => i.State == PhotoState.NeedsCutout))
        {
            item.State = PhotoState.Waiting;
            item.Status = "Đang chờ tới lượt";
        }

        ShowCurrent();
        UpdateFooter();
        StartProcessing();
    }

    // ----- Danh sách và khung chỉnh -----

    private void OnSelectPhoto(object sender, SelectionChangedEventArgs e)
    {
        _current = PhotoList.SelectedItem as PhotoItem;
        _drag = DragMode.None;
        ShowCurrent();
        if (_current is { State: PhotoState.Waiting })
        {
            StartProcessing();
        }
    }

    private void ShowCurrent()
    {
        var item = _current;
        var ready = item is { State: PhotoState.Ready, Preview: not null, Edits: not null };
        EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Stage.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        BusyPanel.Visibility = item is { State: PhotoState.Waiting or PhotoState.Processing } ? Visibility.Visible : Visibility.Collapsed;
        NeedsCutoutPanel.Visibility = item is { State: PhotoState.NeedsCutout } ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = item?.Status ?? string.Empty;
        Toolbar.IsEnabled = ready;
        RemovePhotoButton.IsEnabled = item is not null && item.State != PhotoState.Processing;
        RemovePhotoButton.Content = item?.ExistingId is null ? "Bỏ hình này" : "Thôi, không sửa hình này";
        PhotoHint.Text = item switch
        {
            null => string.Empty,
            { State: PhotoState.Failed } => item.Status,
            { State: PhotoState.Ready } => "Phần mờ dưới đường cắt sẽ bị bỏ. Bấm vào hình để đặt chỗ tim bay ra; chọn Cục tẩy để xóa chỗ còn dính nền (Ctrl+Z để hoàn tác).",
            _ => string.Empty,
        };

        if (!ready)
        {
            return;
        }

        _syncing = true;
        DriftLeft.IsChecked = item!.Edits!.Drift < 0;
        DriftRight.IsChecked = item.Edits.Drift > 0;
        _syncing = false;
        LayoutStage();
        RenderPreview();
    }

    private void OnStageHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_current is { State: PhotoState.Ready })
        {
            LayoutStage();
        }
    }

    private void LayoutStage()
    {
        if (_current is not { Preview: { } preview, Draft: { } draft })
        {
            return;
        }

        var availW = Math.Max(50, StageHost.ActualWidth - 28);
        var availH = Math.Max(50, StageHost.ActualHeight - 28);
        var s = Math.Min(2, Math.Min(availW / preview.Width, availH / preview.Height));
        double w = preview.Width * s, h = preview.Height * s;
        PreviewImage.Width = Overlay.Width = w;
        PreviewImage.Height = Overlay.Height = h;
        _displayScale = w / draft.Width;
        UpdateOverlay();
    }

    private void RenderPreview()
    {
        if (_current is not { Preview: { } preview, Edits: { } edits })
        {
            return;
        }

        var pixels = preview.Render(edits);
        if (_bitmap is null || _bitmap.PixelWidth != preview.Width || _bitmap.PixelHeight != preview.Height)
        {
            _bitmap = new WriteableBitmap(preview.Width, preview.Height, 96, 96, PixelFormats.Pbgra32, null);
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, preview.Width, preview.Height), pixels, preview.Width * 4, 0);
        PreviewImage.Source = _bitmap;
        UpdateOverlay();
    }

    private void UpdateOverlay()
    {
        if (_current is not { Edits: { } edits })
        {
            return;
        }

        var s = _displayScale;
        var cutY = Math.Min(Overlay.Height - 1, edits.BottomCut * s);
        CutLine.X1 = 0;
        CutLine.X2 = Overlay.Width;
        CutLine.Y1 = CutLine.Y2 = cutY;
        CutHandle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(CutHandle, Math.Max(0, (Overlay.Width - CutHandle.DesiredSize.Width) / 2));
        Canvas.SetTop(CutHandle, cutY > CutHandle.DesiredSize.Height + 6 ? cutY - CutHandle.DesiredSize.Height - 4 : cutY + 4);

        double kx = edits.KissAt.X * s, ky = edits.KissAt.Y * s;
        Canvas.SetLeft(HeartMarker, kx - 15);
        Canvas.SetTop(HeartMarker, ky - 15);
        DriftArrow.Text = edits.Drift < 0 ? "←" : "→";
        Canvas.SetLeft(DriftArrow, edits.Drift < 0 ? kx - 40 : kx + 18);
        Canvas.SetTop(DriftArrow, ky - 16);
    }

    private void SetEdits(CutoutEdits edits, bool render)
    {
        _current!.Edits = edits;
        _current.Changed = true;
        if (render)
        {
            RenderPreview();
        }
        else
        {
            UpdateOverlay();
        }
    }

    private void PushHistory() => _current!.History.Push(_current.Edits!);

    private void OnOverlayDown(object sender, MouseButtonEventArgs e)
    {
        if (_current is not { State: PhotoState.Ready, Edits: { } edits })
        {
            return;
        }

        var p = e.GetPosition(Overlay);
        Overlay.CaptureMouse();
        PushHistory();
        if (NearCutLine(p, edits))
        {
            _drag = DragMode.Cut;
            MoveCut(p);
        }
        else if (ModeErase.IsChecked == true)
        {
            _drag = DragMode.Erase;
            _lastErase = p;
            AddErase(p);
        }
        else
        {
            _drag = DragMode.Heart;
            MoveHeart(p);
        }

        e.Handled = true;
    }

    private void OnOverlayMove(object sender, MouseEventArgs e)
    {
        if (_current is not { State: PhotoState.Ready, Edits: { } edits })
        {
            return;
        }

        var p = e.GetPosition(Overlay);
        switch (_drag)
        {
            case DragMode.Cut:
                MoveCut(p);
                break;
            case DragMode.Heart:
                MoveHeart(p);
                break;
            case DragMode.Erase when (p - _lastErase).Length >= Math.Max(2, BrushSlider.Value * 0.3):
                _lastErase = p;
                AddErase(p);
                break;
        }

        var erasing = ModeErase.IsChecked == true && (_drag == DragMode.Erase || (_drag == DragMode.None && !NearCutLine(p, edits)));
        Overlay.Cursor = _drag == DragMode.Cut || (_drag == DragMode.None && NearCutLine(p, edits)) ? Cursors.SizeNS
            : erasing ? Cursors.None
            : Cursors.Hand;
        BrushCursor.Visibility = erasing ? Visibility.Visible : Visibility.Collapsed;
        if (erasing)
        {
            var r = BrushSlider.Value;
            BrushCursor.Width = BrushCursor.Height = 2 * r;
            Canvas.SetLeft(BrushCursor, p.X - r);
            Canvas.SetTop(BrushCursor, p.Y - r);
        }
    }

    private void OnOverlayUp(object sender, MouseButtonEventArgs e) => Overlay.ReleaseMouseCapture();

    private void OnOverlayLostCapture(object sender, MouseEventArgs e) => _drag = DragMode.None;

    private void OnOverlayLeave(object sender, MouseEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            BrushCursor.Visibility = Visibility.Collapsed;
        }
    }

    private bool NearCutLine(Point p, CutoutEdits edits) => Math.Abs(p.Y - Math.Min(Overlay.Height - 1, edits.BottomCut * _displayScale)) <= 9;

    private void MoveCut(Point p)
    {
        var draft = _current!.Draft!;
        var cut = (int)Math.Round(p.Y / _displayScale);
        if (p.Y >= Overlay.Height - 8)
        {
            cut = draft.Height; // kéo sát đáy: giữ cả người
        }

        cut = Math.Clamp(cut, CutoutRenderer.MinCutHeight, draft.Height);
        var edits = _current.Edits! with { BottomCut = cut };
        if (edits.KissAt.Y > cut - 8)
        {
            edits = edits with { KissAt = edits.KissAt with { Y = Math.Max(0, cut - 8) } };
        }

        SetEdits(edits, render: true);
    }

    private void MoveHeart(Point p)
    {
        var draft = _current!.Draft!;
        var edits = _current.Edits!;
        var kiss = CutoutPlanner.Clamp(new Core.Cutout.PointF((float)(p.X / _displayScale), (float)(p.Y / _displayScale)), draft, edits.BottomCut);
        SetEdits(edits with { KissAt = kiss }, render: false);
    }

    private void AddErase(Point p)
    {
        var stroke = new EraseStroke((float)(p.X / _displayScale), (float)(p.Y / _displayScale), (float)(BrushSlider.Value / _displayScale));
        SetEdits(_current!.Edits!.WithStroke(stroke), render: true);
    }

    private void OnToolChanged(object sender, RoutedEventArgs e)
    {
        if (BrushCursor is not null)
        {
            BrushCursor.Visibility = Visibility.Collapsed;
        }
    }

    private void OnBrushChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Kéo cỡ cục tẩy thì chuyển sang cục tẩy; bỏ qua lần gán giá trị lúc dựng cửa sổ.
        if (IsLoaded && !_syncing)
        {
            ModeErase.IsChecked = true;
        }
    }

    private void OnDriftChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _current is not { State: PhotoState.Ready, Edits: { } edits })
        {
            return;
        }

        PushHistory();
        SetEdits(edits with { Drift = DriftLeft.IsChecked == true ? -1 : 1 }, render: false);
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_current is { State: PhotoState.Ready } item && item.History.Count > 0)
        {
            item.Edits = item.History.Pop();
            ShowCurrent();
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_current is { State: PhotoState.Ready, Suggested: { } suggested })
        {
            PushHistory();
            SetEdits(suggested, render: true);
            ShowCurrent();
        }
    }

    private void OnCycleBackground(object sender, RoutedEventArgs e)
    {
        _backgroundIndex = (_backgroundIndex + 1) % _backgrounds.Length;
        StageHost.Background = _backgrounds[_backgroundIndex];
    }

    private void OnRemovePhoto(object sender, RoutedEventArgs e)
    {
        if (_current is not { } item || item.State == PhotoState.Processing)
        {
            return;
        }

        var index = _items.IndexOf(item);
        _items.Remove(item);
        PhotoList.SelectedItem = _items.Count == 0 ? null : _items[Math.Min(index, _items.Count - 1)];
        ShowCurrent();
        UpdateFooter();
    }

    private void OnPickPhotos(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn ảnh crush",
            Filter = PhotoIO.OpenFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddFiles(files);
        }
    }

    // ----- Lưu -----

    private void UpdateFooter()
    {
        var ready = _items.Count(i => i.State == PhotoState.Ready);
        var pending = _items.Count(i => i.State is PhotoState.Waiting or PhotoState.Processing);
        var needsCutout = _items.Count(i => i.State == PhotoState.NeedsCutout);
        SaveButton.IsEnabled = ready > 0;
        SaveButton.Content = ready > 1 ? $"Lưu {ready} hình" : "Lưu hình";
        FooterText.Text = pending > 0 ? $"Đang xử lý, còn {pending} ảnh. Hình nào xong thì chỉnh và lưu trước được."
            : needsCutout > 0 ? $"{needsCutout} ảnh còn nền: bấm vào ảnh đó để xem cách xử lý."
            : ready > 0 ? "Kiểm tra từng hình xong thì bấm Lưu."
            : string.Empty;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var ready = _items.Where(i => i.State == PhotoState.Ready && i.Draft is not null && i.Edits is not null).ToList();
        if (ready.Count == 0)
        {
            return;
        }

        SaveButton.IsEnabled = false;
        FooterText.Text = "Đang lưu…";
        var saved = 0;
        try
        {
            foreach (var item in ready)
            {
                var draft = item.Draft!;
                var edits = item.Edits!;
                var existing = item.ExistingId;
                await Task.Run(() =>
                {
                    var r = CutoutRenderer.Render(draft, edits);
                    var id = existing;
                    if (id is null)
                    {
                        id = _store.Add(p => PhotoIO.SavePng(p, r.Width, r.Height, r.Bgra), r.KissPoint, r.Drift, DateTime.Now).Id;
                    }
                    else
                    {
                        _store.Update(id, p => PhotoIO.SavePng(p, r.Width, r.Height, r.Bgra), r.KissPoint, r.Drift);
                    }

                    _store.SaveDraft(id, p => PhotoIO.SaveDraft(p, draft), edits, draft.Face);
                });

                _items.Remove(item);
                saved++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Không lưu được hình", ex);
            MessageBox.Show(this, $"Không lưu được hình: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (saved > 0)
        {
            Log.Info($"Đã lưu {saved} hình vào bộ của người dùng");
            _controller.OnUserPhotosChanged(showNewest: ready.Any(i => i.ExistingId is null));
        }

        if (_items.Count == 0)
        {
            _forceClose = true;
            Close();
            return;
        }

        PhotoList.SelectedItem = _items.FirstOrDefault(i => i.State == PhotoState.Ready) ?? _items[0];
        ShowCurrent();
        UpdateFooter();
        if (saved > 0)
        {
            FooterText.Text = $"Đã lưu {saved} hình. " + FooterText.Text;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

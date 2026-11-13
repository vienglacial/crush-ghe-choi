using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CrushGheChoi.Core.Cutout;
using CrushGheChoi.Cutout;
using CrushGheChoi.Services;
using Microsoft.ML.OnnxRuntime;

namespace CrushGheChoi.UI;

public enum PhotoState
{
    Waiting,
    Processing,
    Ready,
    Failed,

    /// <summary>Ảnh còn nền mà chưa cài bộ tách nền.</summary>
    NeedsCutout,
}

/// <summary>Một ảnh trong cửa sổ Thêm hình: đang chờ, đang xử lý, đã xong (đang chỉnh) hoặc lỗi.</summary>
public sealed class PhotoItem(string name, string? sourcePath, string? existingId) : INotifyPropertyChanged
{
    private string _status = "Đang chờ tới lượt";
    private Bitmap? _thumbnail;

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

    public Bitmap? Thumbnail
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
/// </summary>
public partial class AddPhotosWindow : Window
{
    private const int PreviewMaxSide = 1000;
    private const int ThumbnailHeight = 104;

    private static readonly IBrush[] Backgrounds =
    [
        new SolidColorBrush(Color.Parse("#F6ECF1")),
        new SolidColorBrush(Color.Parse("#1E1A22")),
        new SolidColorBrush(Color.Parse("#3A6EA5")),
    ];

    private readonly AppController? _controller;
    private readonly UserSetStore? _store;
    private readonly ObservableCollection<PhotoItem> _items = [];
    private readonly CutoutService _service = new();
    private readonly CancellationTokenSource _closing = new();
    private Task? _processing;
    private PhotoItem? _current;
    private WriteableBitmap? _bitmap;
    private double _displayScale = 1;
    private DragMode _drag;
    private Point _lastErase;
    private bool _syncing;
    private bool _forceClose;
    private int _backgroundIndex;

    public AddPhotosWindow()
    {
        InitializeComponent();
    }

    internal AddPhotosWindow(AppController controller, UserSetStore store)
        : this()
    {
        _controller = controller;
        _store = store;
        PhotoList.ItemsSource = _items;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        BrushSlider.ValueChanged += (_, _) =>
        {
            // Kéo cỡ cục tẩy thì chuyển sang cục tẩy.
            if (IsLoaded && !_syncing)
            {
                ModeErase.IsChecked = true;
            }
        };
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

    /// <summary>Thêm ảnh vào hàng chờ xử lý.</summary>
    internal void AddFiles(IEnumerable<string> paths)
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
                FooterText.Text = "Không có ảnh nào dùng được trong các tệp vừa chọn (cần PNG, JPG hoặc WEBP).";
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
    internal async void EditExisting(UserPhoto photo)
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
            var store = _store!;
            var loaded = store.LoadDraftEdits(photo.Id) ?? throw new InvalidDataException("Hình này không có bản nháp để chỉnh lại.");
            var (draft, preview, thumb) = await Task.Run(() =>
            {
                var d = PhotoIO.LoadDraft(store.DraftImagePath(photo.Id), loaded.Face);
                return (d, new CutoutPreview(d, PreviewMaxSide), PhotoIO.Thumbnail(store.StillPath(photo.Id), ThumbnailHeight));
            });

            item.Draft = draft;
            item.Preview = preview;
            item.Thumbnail = thumb;
            item.Edits = loaded.Edits;
            item.Suggested = CutoutPlanner.Suggest(draft);
            item.State = PhotoState.Ready;
            item.Status = "Đang chỉnh lại";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
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
    internal void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        var unsaved = _items.Count(i => i.State == PhotoState.Processing || (i.State == PhotoState.Ready && (i.ExistingId is null || i.Changed)));
        if (!_forceClose && unsaved > 0)
        {
            // Hỏi trước rồi mới đóng thật.
            e.Cancel = true;
            base.OnClosing(e);
            if (await Dialogs.ConfirmAsync(this, $"Còn {unsaved} hình chưa lưu. Đóng và bỏ các hình này?", "Đóng", "Ở lại"))
            {
                ForceClose();
            }

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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Z && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            OnUndo(this, e);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    // ----- Hàng chờ xử lý -----

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

    private static string FriendlyError(Exception ex) => ex switch
    {
        OutOfMemoryException => "Không đủ RAM. Đóng bớt ứng dụng rồi thêm lại ảnh này.",
        OnnxRuntimeException o when o.Message.Contains("alloc", StringComparison.OrdinalIgnoreCase) => "Không đủ RAM. Đóng bớt ứng dụng rồi thêm lại ảnh này.",
        OnnxRuntimeException => "Bộ tách nền bị lỗi. Thử thêm lại ảnh này.",
        NotSupportedException or ArgumentException => "Không đọc được ảnh này. Hãy đổi sang PNG hoặc JPG (ảnh HEIC chưa đọc được).",
        IOException or UnauthorizedAccessException => $"Không mở được tệp: {ex.Message}",
        _ => $"Lỗi khi xử lý: {ex.Message}",
    };

    private static async Task LoadThumbnailAsync(PhotoItem item, string path)
    {
        try
        {
            item.Thumbnail = await Task.Run(() => PhotoIO.Thumbnail(path, ThumbnailHeight));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or UnauthorizedAccessException)
        {
            // Không có ảnh nhỏ thì thôi; lỗi thật sẽ hiện khi xử lý.
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
                Log.Warn($"Hàng chờ xử lý dừng với lỗi: {ex.Message}");
            }
        }

        _service.Dispose();
    }

    // ----- Bộ tách nền (người dùng tự tải) -----

    private void OnOpenModelPage(object? sender, RoutedEventArgs e)
    {
        // Trình duyệt tải tệp; app không tự tải gì về.
        Shell.OpenUrl(ModelStore.Segmenter.Url);
        ModelText.Text = $"Trình duyệt đang tải {ModelStore.Segmenter.FileName}. Tải xong thì bấm “Chọn tệp đã tải…” (tệp thường nằm trong thư mục Downloads).";
    }

    private async void OnImportModel(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Chọn tệp {ModelStore.Segmenter.FileName}",
            FileTypeFilter = [new FilePickerFileType("Model ONNX") { Patterns = ["*.onnx"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        ImportModelButton.IsEnabled = false;
        ModelText.Text = "Đang kiểm tra và cài tệp…";
        try
        {
            await ModelStore.ImportAsync(path, _closing.Token);
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

        if (ModelStore.SegmenterReady)
        {
            foreach (var item in _items.Where(i => i.State == PhotoState.NeedsCutout))
            {
                item.State = PhotoState.Waiting;
                item.Status = "Đang chờ tới lượt";
            }

            ShowCurrent();
            UpdateFooter();
            StartProcessing();
        }
    }

    // ----- Danh sách và khung chỉnh -----

    private void OnSelectPhoto(object? sender, SelectionChangedEventArgs e)
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
        EmptyPanel.IsVisible = _items.Count == 0;
        Stage.IsVisible = ready;
        BusyPanel.IsVisible = item is { State: PhotoState.Waiting or PhotoState.Processing };
        NeedsCutoutPanel.IsVisible = item is { State: PhotoState.NeedsCutout };
        BusyText.Text = item?.Status ?? string.Empty;
        Toolbar.IsEnabled = ready;
        RemovePhotoButton.IsEnabled = item is not null && item.State != PhotoState.Processing;
        RemovePhotoButton.Content = item?.ExistingId is null ? "Bỏ hình này" : "Thôi, không sửa hình này";
        PhotoHint.Text = item switch
        {
            null => string.Empty,
            { State: PhotoState.Failed } => item.Status,
            { State: PhotoState.Ready } => "Phần mờ dưới đường cắt sẽ bị bỏ. Bấm vào hình để đặt chỗ tim bay ra; chọn Cục tẩy để xóa chỗ còn dính nền.",
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

    private void OnStageHostSizeChanged(object? sender, SizeChangedEventArgs e)
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

        var availW = Math.Max(50, StageHost.Bounds.Width - 28);
        var availH = Math.Max(50, StageHost.Bounds.Height - 28);
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
        if (_bitmap is null || _bitmap.PixelSize.Width != preview.Width || _bitmap.PixelSize.Height != preview.Height)
        {
            _bitmap = PhotoIO.ToBitmap(pixels, preview.Width, preview.Height);
            PreviewImage.Source = _bitmap;
        }
        else
        {
            PhotoIO.CopyInto(_bitmap, pixels);
            PreviewImage.InvalidateVisual();
        }

        UpdateOverlay();
    }

    private void UpdateOverlay()
    {
        if (_current is not { Edits: { } edits } || double.IsNaN(Overlay.Width))
        {
            return;
        }

        var s = _displayScale;
        var cutY = Math.Min(Overlay.Height - 1, edits.BottomCut * s);
        CutLine.StartPoint = new Point(0, cutY);
        CutLine.EndPoint = new Point(Overlay.Width, cutY);
        CutHandle.Measure(Size.Infinity);
        Canvas.SetLeft(CutHandle, Math.Max(0, (Overlay.Width - CutHandle.DesiredSize.Width) / 2));
        Canvas.SetTop(CutHandle, cutY > CutHandle.DesiredSize.Height + 6 ? cutY - CutHandle.DesiredSize.Height - 4 : cutY + 4);

        double kx = edits.KissAt.X * s, ky = edits.KissAt.Y * s;
        Canvas.SetLeft(HeartMarker, kx - 15);
        Canvas.SetTop(HeartMarker, ky - 15);
        DriftArrowText.Text = edits.Drift < 0 ? "←" : "→";
        Canvas.SetLeft(DriftArrow, edits.Drift < 0 ? kx - 46 : kx + 20);
        Canvas.SetTop(DriftArrow, ky - 11);
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

    private void OnOverlayDown(object? sender, PointerPressedEventArgs e)
    {
        if (_current is not { State: PhotoState.Ready, Edits: { } edits } || !e.GetCurrentPoint(Overlay).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var p = e.GetPosition(Overlay);
        e.Pointer.Capture(Overlay);
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

    private void OnOverlayMove(object? sender, PointerEventArgs e)
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
            case DragMode.Erase when Distance(p, _lastErase) >= Math.Max(2, BrushSlider.Value * 0.3):
                _lastErase = p;
                AddErase(p);
                break;
        }

        var erasing = ModeErase.IsChecked == true && (_drag == DragMode.Erase || (_drag == DragMode.None && !NearCutLine(p, edits)));
        Overlay.Cursor = _drag == DragMode.Cut || (_drag == DragMode.None && NearCutLine(p, edits)) ? new Cursor(StandardCursorType.SizeNorthSouth)
            : erasing ? new Cursor(StandardCursorType.Cross)
            : new Cursor(StandardCursorType.Hand);
        BrushCursor.IsVisible = erasing;
        if (erasing)
        {
            var r = BrushSlider.Value;
            BrushCursor.Width = BrushCursor.Height = 2 * r;
            Canvas.SetLeft(BrushCursor, p.X - r);
            Canvas.SetTop(BrushCursor, p.Y - r);
        }
    }

    private void OnOverlayUp(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        _drag = DragMode.None;
    }

    private void OnOverlayLostCapture(object? sender, PointerCaptureLostEventArgs e) => _drag = DragMode.None;

    private void OnOverlayLeave(object? sender, PointerEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            BrushCursor.IsVisible = false;
        }
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

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
        var kiss = CutoutPlanner.Clamp(new PointF((float)(p.X / _displayScale), (float)(p.Y / _displayScale)), draft, edits.BottomCut);
        SetEdits(edits with { KissAt = kiss }, render: false);
    }

    private void AddErase(Point p)
    {
        var stroke = new EraseStroke((float)(p.X / _displayScale), (float)(p.Y / _displayScale), (float)(BrushSlider.Value / _displayScale));
        SetEdits(_current!.Edits!.WithStroke(stroke), render: true);
    }

    private void OnDriftChanged(object? sender, RoutedEventArgs e)
    {
        // Mỗi lần đổi lựa chọn sự kiện này tới hai lần (nút bỏ chọn và nút được chọn): chỉ xử lý nút được chọn.
        if (_syncing || sender is not RadioButton { IsChecked: true } || _current is not { State: PhotoState.Ready, Edits: { } edits })
        {
            return;
        }

        var drift = DriftLeft.IsChecked == true ? -1 : 1;
        if (drift != edits.Drift)
        {
            PushHistory();
            SetEdits(edits with { Drift = drift }, render: false);
        }
    }

    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (_current is { State: PhotoState.Ready } item && item.History.Count > 0)
        {
            item.Edits = item.History.Pop();
            ShowCurrent();
        }
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        if (_current is { State: PhotoState.Ready, Suggested: { } suggested })
        {
            PushHistory();
            SetEdits(suggested, render: true);
            ShowCurrent();
        }
    }

    private void OnCycleBackground(object? sender, RoutedEventArgs e)
    {
        _backgroundIndex = (_backgroundIndex + 1) % Backgrounds.Length;
        StageHost.Background = Backgrounds[_backgroundIndex];
    }

    private void OnRemovePhoto(object? sender, RoutedEventArgs e)
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

    private async void OnPickPhotos(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Chọn hình crush",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Ảnh") { Patterns = PhotoIO.Patterns }],
        });
        AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { } files)
        {
            AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
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

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        var ready = _items.Where(i => i.State == PhotoState.Ready && i.Draft is not null && i.Edits is not null).ToList();
        if (ready.Count == 0 || _store is null || _controller is null)
        {
            return;
        }

        var store = _store;
        SaveButton.IsEnabled = false;
        FooterText.Text = "Đang lưu…";
        var saved = 0;
        var addedNew = false;
        string? error = null;
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
                        id = store.Add(p => PhotoIO.SavePng(p, r.Width, r.Height, r.Bgra), r.KissPoint, r.Drift, DateTime.Now).Id;
                    }
                    else
                    {
                        store.Update(id, p => PhotoIO.SavePng(p, r.Width, r.Height, r.Bgra), r.KissPoint, r.Drift);
                    }

                    store.SaveDraft(id, p => PhotoIO.SaveDraft(p, draft), edits, draft.Face);
                });

                addedNew |= existing is null;
                _items.Remove(item);
                saved++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Không lưu được hình", ex);
            error = $"Không lưu được hình: {ex.Message}";
        }

        if (saved > 0)
        {
            Log.Info($"Đã lưu {saved} hình vào bộ của người dùng");
            _controller.OnUserPhotosChanged(showNewest: addedNew);
        }

        if (_items.Count == 0 && error is null)
        {
            ForceClose();
            return;
        }

        PhotoList.SelectedItem = _items.FirstOrDefault(i => i.State == PhotoState.Ready) ?? _items.FirstOrDefault();
        ShowCurrent();
        UpdateFooter();
        if (error is not null)
        {
            FooterText.Text = error;
        }
        else if (saved > 0)
        {
            FooterText.Text = $"Đã lưu {saved} hình. " + FooterText.Text;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}

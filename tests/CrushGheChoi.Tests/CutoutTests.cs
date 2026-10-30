using System.Buffers.Binary;
using CrushGheChoi.Core.Animation;
using CrushGheChoi.Core.Cutout;

namespace CrushGheChoi.Tests;

public class ImageMathTests
{
    private static Plane Random(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var p = new Plane(w, h);
        for (var i = 0; i < p.Data.Length; i++)
        {
            p.Data[i] = (float)rng.NextDouble();
        }

        return p;
    }

    private static Plane Filled(int w, int h, Func<int, int, float> f)
    {
        var p = new Plane(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                p[x, y] = f(x, y);
            }
        }

        return p;
    }

    [Fact]
    public void Box_blur_matches_naive_window_with_edge_clamp()
    {
        var src = Random(23, 17, 1);
        const int r = 3;
        var fast = ImageMath.BoxBlur(src, r);
        for (var y = 0; y < src.Height; y++)
        {
            for (var x = 0; x < src.Width; x++)
            {
                double sum = 0;
                for (var dy = -r; dy <= r; dy++)
                {
                    for (var dx = -r; dx <= r; dx++)
                    {
                        sum += src[Math.Clamp(x + dx, 0, src.Width - 1), Math.Clamp(y + dy, 0, src.Height - 1)];
                    }
                }

                Assert.Equal(sum / ((2 * r + 1) * (2 * r + 1)), fast[x, y], 4);
            }
        }
    }

    [Fact]
    public void Resample_keeps_constant_and_averages_when_shrinking()
    {
        var constant = Filled(40, 30, (_, _) => 0.7f);
        foreach (var (w, h) in new[] { (10, 7), (90, 61), (40, 30) })
        {
            Assert.All(ImageMath.Resample(constant, w, h).Data, v => Assert.Equal(0.7f, v, 4));
        }

        var checker = Filled(64, 64, (x, y) => (x + y) % 2);
        Assert.All(ImageMath.Resample(checker, 8, 8).Data, v => Assert.InRange(v, 0.4f, 0.6f));
    }

    [Fact]
    public void Guided_filter_leaves_a_flat_mask_flat()
    {
        var guide = Random(30, 30, 2);
        var flat = Filled(30, 30, (_, _) => 1f);
        Assert.All(ImageMath.GuidedFilter(guide, flat, 4, 1e-3f).Data, v => Assert.Equal(1f, v, 3));
    }

    [Fact]
    public void Main_blob_keeps_the_seeded_part_only()
    {
        // Hai khối rời: người bên trái, vật bên phải.
        var alpha = Filled(200, 120, (x, y) => (x is > 10 and < 90 && y is > 10 and < 110) || (x is > 140 and < 190 && y is > 40 and < 80) ? 1 : 0);
        var keep = ImageMath.MainBlob(alpha, 0.5f, new PointF(50, 60));
        Assert.True(keep[60 * 200 + 50]);
        Assert.False(keep[60 * 200 + 165]);

        // Không có điểm mồi thì lấy khối dày đặc nhất (khối to).
        var auto = ImageMath.MainBlob(alpha, 0.5f, null);
        Assert.True(auto[60 * 200 + 50]);
        Assert.False(auto[60 * 200 + 165]);
    }

    [Fact]
    public void Pinholes_inside_are_filled_but_the_outline_is_kept()
    {
        var alpha = Filled(300, 300, (x, y) => x is >= 50 and < 250 && y is >= 50 and < 250 ? 1 : 0);
        alpha[150, 150] = 0;
        alpha[151, 150] = 0;
        alpha[150, 151] = 0;
        ImageMath.FillPinholes(alpha);
        Assert.Equal(1f, alpha[150, 150]);
        Assert.Equal(1f, alpha[151, 150]);
        Assert.Equal(0f, alpha[45, 150]);
        Assert.Equal(0f, alpha[150, 260]);
    }

    [Fact]
    public void Decontaminate_takes_edge_color_from_the_person()
    {
        // Người màu đỏ bên trái; cột mép bán trong suốt còn dính màu xanh của nền.
        var alpha = Filled(40, 10, (x, _) => x < 20 ? 1 : x == 20 ? 0.5f : 0);
        var rgb = new RgbImage(Filled(40, 10, (x, _) => x < 20 ? 1 : 0), Filled(40, 10, (x, _) => x < 20 ? 0 : 1), Filled(40, 10, (_, _) => 0));
        var clean = ImageMath.Decontaminate(rgb, alpha);
        Assert.Equal(1f, clean.R[20, 5], 3);
        Assert.Equal(0f, clean.G[20, 5], 3);
        Assert.Equal(1f, clean.R[5, 5]); // phần đặc giữ nguyên
    }

    [Fact]
    public void Erase_stroke_clears_the_center_softly()
    {
        var alpha = Filled(50, 50, (_, _) => 1);
        ImageMath.ApplyErase(alpha, [new EraseStroke(25, 25, 10)]);
        Assert.Equal(0f, alpha[25, 25]);
        Assert.InRange(alpha[33, 25], 0.01f, 0.99f);
        Assert.Equal(1f, alpha[40, 25]);
    }

    [Fact]
    public void Touched_edges_and_fade()
    {
        var alpha = Filled(100, 50, (x, y) => x < 60 && y > 10 ? 1 : 0);
        var (left, right, top) = ImageMath.TouchedEdges(alpha);
        Assert.True(left);
        Assert.False(right);
        Assert.False(top);

        ImageMath.FadeCutEdges(alpha, left, right, top);
        Assert.Equal(0f, alpha[0, 30]);
        Assert.True(alpha[3, 30] < alpha[7, 30]);
        Assert.Equal(1f, alpha[30, 30]);
    }
}

public class ModelIoTests
{
    [Fact]
    public void Segmentation_mask_is_stretched_to_full_range()
    {
        var logits = new float[Segmentation.InputSize * Segmentation.InputSize];
        for (var i = 0; i < logits.Length; i++)
        {
            logits[i] = i % 3 - 1; // -1, 0, 1
        }

        var mask = Segmentation.ToMask(logits);
        Assert.Equal(0f, mask.Data[0], 4);
        Assert.Equal(0.5f, mask.Data[1], 2);
        Assert.Equal(1f, mask.Data[2], 4);
    }

    [Fact]
    public void Segmentation_input_is_imagenet_normalized()
    {
        var plane = new Plane(4, 4);
        Array.Fill(plane.Data, 1f);
        var tensor = Segmentation.PrepareInput(new RgbImage(plane, plane.Clone(), plane.Clone()));
        Assert.Equal(3 * 1024 * 1024, tensor.Length);
        Assert.Equal((1 - 0.485f) / 0.229f, tensor[0], 3);
        Assert.Equal((1 - 0.406f) / 0.225f, tensor[2 * 1024 * 1024 + 5], 3);
    }

    [Fact]
    public void Face_decoder_reads_box_and_landmarks_and_drops_duplicates()
    {
        // Một ô ở bước 8, hàng 10 cột 20 (chỉ số 10*80+20), và một ô trùng chỗ có điểm thấp hơn.
        var outputs = new Dictionary<string, float[]>();
        foreach (var stride in FaceDetection.Strides)
        {
            var n = (640 / stride) * (640 / stride);
            outputs[$"cls_{stride}"] = new float[n];
            outputs[$"obj_{stride}"] = new float[n];
            outputs[$"bbox_{stride}"] = new float[n * 4];
            outputs[$"kps_{stride}"] = new float[n * 10];
        }

        void Put(int stride, int index, float score, float[] box)
        {
            outputs[$"cls_{stride}"][index] = score;
            outputs[$"obj_{stride}"][index] = 1;
            box.CopyTo(outputs[$"bbox_{stride}"], index * 4);
            for (var k = 0; k < 5; k++)
            {
                outputs[$"kps_{stride}"][index * 10 + 2 * k] = 0.5f;
                outputs[$"kps_{stride}"][index * 10 + 2 * k + 1] = 0.5f + k * 0.1f;
            }
        }

        Put(8, 10 * 80 + 20, 0.9f, [0.5f, 0.5f, MathF.Log(4), MathF.Log(4)]);
        Put(8, 10 * 80 + 21, 0.81f, [-0.4f, 0.5f, MathF.Log(4), MathF.Log(4)]);

        var faces = FaceDetection.Decode(name => outputs[name], scale: 0.5f);

        var face = Assert.Single(faces);
        Assert.Equal(MathF.Sqrt(0.9f), face.Score, 3); // điểm = căn(cls × obj)
        // tâm (20.5, 10.5)*8 = (164, 84), rộng 4*8 = 32, chia tỷ lệ 0.5
        Assert.Equal(new RectF(296, 136, 64, 64), face.Box);
        Assert.Equal(new PointF(328, 168), face.Landmarks[0]);
        Assert.Equal((168 + 0.3f * 16 + 168 + 0.4f * 16) / 2, face.Mouth.Y, 2);
    }
}

public class CutoutPlanTests
{
    private static CutoutDraft Draft(int w, int h, PixelBox person, DetectedFace? face)
    {
        var alpha = new Plane(w, h);
        for (var y = person.Y; y < person.Bottom; y++)
        {
            for (var x = person.X; x < person.Right; x++)
            {
                alpha[x, y] = 1;
            }
        }

        var gray = new Plane(w, h);
        Array.Fill(gray.Data, 0.5f);
        return new CutoutDraft(new RgbImage(gray, gray.Clone(), gray.Clone()), alpha, face);
    }

    private static DetectedFace Face(float x, float y, float size)
    {
        var mouth = y + size * 0.75f;
        return new DetectedFace(0.9f, new RectF(x, y, size, size),
        [
            new(x + size * 0.3f, y + size * 0.4f), new(x + size * 0.7f, y + size * 0.4f), new(x + size * 0.5f, y + size * 0.55f),
            new(x + size * 0.35f, mouth), new(x + size * 0.65f, mouth),
        ]);
    }

    [Fact]
    public void Suggests_a_bust_cut_and_a_heart_beside_the_face()
    {
        // Mặt lệch phải trong người → tim bay sang trái.
        var draft = Draft(800, 1600, new PixelBox(100, 100, 600, 1500), Face(450, 200, 200));
        var edits = CutoutPlanner.Suggest(draft);

        Assert.Equal(400 + (int)(1.2 * 200), edits.BottomCut);
        Assert.Equal(-1, edits.Drift);
        Assert.Equal(450 - 50, edits.KissAt.X, 1);
        Assert.Equal(200 + 150 - 20, edits.KissAt.Y, 1);
    }

    [Fact]
    public void Without_a_face_keeps_the_whole_person()
    {
        var draft = Draft(400, 600, new PixelBox(50, 40, 300, 560), null);
        var edits = CutoutPlanner.Suggest(draft);
        Assert.Equal(600, edits.BottomCut);
        Assert.InRange(edits.KissAt.Y, 40, 200);
    }

    [Fact]
    public void Cut_never_goes_past_the_image()
    {
        var draft = Draft(400, 500, new PixelBox(0, 0, 400, 500), Face(100, 250, 200));
        Assert.Equal(500, CutoutPlanner.Suggest(draft).BottomCut);
    }

    [Fact]
    public void Render_crops_to_the_person_and_maps_the_heart_point()
    {
        var draft = Draft(400, 600, new PixelBox(100, 50, 200, 500), null);
        var edits = new CutoutEdits(400, new PointF(200, 100), -1, []);
        var cut = CutoutRenderer.Render(draft, edits, maxHeight: 960);

        // khung người 100..300 × 50..400, chừa 6 px
        Assert.Equal(212, cut.Width);
        Assert.Equal(400 - 44, cut.Height);
        Assert.Equal(Math.Round((200 - 94) / 212.0, 3), cut.KissPoint.X);
        Assert.Equal(Math.Round((100 - 44) / 356.0, 3), cut.KissPoint.Y);
        Assert.Equal(-1, cut.Drift);
        Assert.Equal(0, cut.Bgra[3]); // góc trên trái trong suốt, màu cũng xóa
        Assert.Equal(0, cut.Bgra[0]);
        Assert.Equal(255, cut.Bgra[(100 * cut.Width + 100) * 4 + 3]);
    }

    [Fact]
    public void Render_shrinks_tall_results()
    {
        var draft = Draft(300, 1200, new PixelBox(20, 20, 260, 1180), null);
        var cut = CutoutRenderer.Render(draft, new CutoutEdits(1200, new PointF(150, 100), 1, []), maxHeight: 300);
        Assert.Equal(300, cut.Height);
        Assert.Equal((int)Math.Round(272 * 300 / 1186.0), cut.Width); // khung 272×1186 (đã chừa lề) thu về cao 300
    }

    [Fact]
    public void Transparent_png_is_used_as_is_and_trimmed()
    {
        // Hình đã tách nền: người ở giữa, xung quanh trong suốt.
        var draft = Draft(400, 600, new PixelBox(100, 150, 200, 300), null);
        Assert.True(CutoutPipeline.IsCutOut(draft.Alpha));

        var face = new DetectedFace(0.9f, new RectF(150, 170, 80, 80), [new(1, 1), new(2, 2), new(3, 3), new(4, 4), new(5, 5)]);
        var trimmed = CutoutPipeline.FromCutOut(draft.Rgb, draft.Alpha, face);

        // khung người 100..300 × 150..450, chừa 3% (12 và 18 px)
        Assert.Equal((224, 336), (trimmed.Width, trimmed.Height));
        Assert.Equal(1f, trimmed.Alpha[12 + 50, 18 + 50]);
        Assert.Equal(new RectF(150 - 88, 170 - 132, 80, 80), trimmed.Face!.Box);
    }

    [Fact]
    public void Opaque_photo_is_not_treated_as_cut_out()
    {
        var opaque = Draft(100, 100, new PixelBox(0, 0, 100, 100), null);
        Assert.False(CutoutPipeline.IsCutOut(opaque.Alpha));
    }

    [Fact]
    public void Preview_dims_what_is_cut_off()
    {
        var draft = Draft(100, 200, new PixelBox(0, 0, 100, 200), null);
        var preview = new CutoutPreview(draft, maxSide: 100);
        Assert.Equal((50, 100), (preview.Width, preview.Height));
        var bgra = preview.Render(new CutoutEdits(100, new PointF(50, 50), 1, []), cutOffOpacity: 0.25f);
        Assert.Equal(255, bgra[(10 * 50 + 25) * 4 + 3]);
        Assert.Equal(64, bgra[(80 * 50 + 25) * 4 + 3]);
    }
}

public sealed class UserSetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crush-user-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static Action<string> Png(int w, int h) => path =>
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), w);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), h);
        File.WriteAllBytes(path, bytes);
    };

    [Fact]
    public void Added_photos_form_a_loadable_set()
    {
        var store = new UserSetStore(_dir);
        Assert.Empty(store.Load());

        var now = new DateTime(2026, 10, 29, 20, 0, 0);
        var a = store.Add(Png(300, 400), new NormalizedPoint(0.2, 0.3), -1, now);
        var b = store.Add(Png(500, 400), new NormalizedPoint(0.7, 0.4), 1, now);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal([a.Id, b.Id], store.Load().Select(p => p.Id));

        var set = AnimationSetLoader.Load(_dir);
        Assert.Equal(UserSetStore.SetName, set.Name);
        Assert.Equal(2, set.Stills.Count);
        Assert.Equal(new NormalizedPoint(0.7, 0.4), set.Stills[1].KissPoint);
        Assert.Equal(1, set.Stills[1].HeartDrift);
        Assert.Equal(500, set.Stills[1].Width);
    }

    [Fact]
    public void Removing_deletes_files_and_the_last_one_removes_the_manifest()
    {
        var store = new UserSetStore(_dir);
        var now = DateTime.Now;
        var a = store.Add(Png(100, 100), new NormalizedPoint(0.5, 0.5), 1, now);
        var b = store.Add(Png(100, 100), new NormalizedPoint(0.5, 0.5), 1, now);
        store.SaveDraft(a.Id, Png(120, 120), new CutoutEdits(90, new PointF(10, 20), -1, [new EraseStroke(1, 2, 3)]), null);

        store.Remove(a.Id);
        Assert.False(File.Exists(store.StillPath(a.Id)));
        Assert.False(store.HasDraft(a.Id));
        Assert.Equal([b.Id], store.Load().Select(p => p.Id));

        store.Remove(b.Id);
        Assert.False(File.Exists(store.ManifestPath));
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Draft_edits_round_trip()
    {
        var store = new UserSetStore(_dir);
        var photo = store.Add(Png(100, 100), new NormalizedPoint(0.5, 0.5), 1, DateTime.Now);
        var face = new DetectedFace(0.8f, new RectF(1, 2, 30, 40), [new(1, 1), new(2, 2), new(3, 3), new(4, 4), new(5, 5)]);
        var edits = new CutoutEdits(77, new PointF(12.5f, 30), -1, [new EraseStroke(5, 6, 7)]);

        store.SaveDraft(photo.Id, Png(100, 100), edits, face);
        var loaded = store.LoadDraftEdits(photo.Id);

        Assert.NotNull(loaded);
        Assert.Equal(77, loaded.Value.Edits.BottomCut);
        Assert.Equal(new PointF(12.5f, 30), loaded.Value.Edits.KissAt);
        Assert.Equal(-1, loaded.Value.Edits.Drift);
        Assert.Equal(new EraseStroke(5, 6, 7), Assert.Single(loaded.Value.Edits.Erase));
        Assert.Equal(face.Box, loaded.Value.Face!.Box);
        Assert.Equal(new PointF(4, 4), loaded.Value.Face.Landmarks[3]);
    }

    [Fact]
    public void Ids_are_unique_for_the_same_moment()
    {
        var now = new DateTime(2026, 1, 2, 3, 4, 5, 6);
        var first = UserSetStore.NewId(now, []);
        Assert.Equal("20260102-030405-006", first);
        Assert.Equal("20260102-030405-006-2", UserSetStore.NewId(now, [first]));
    }
}

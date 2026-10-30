using System.Buffers.Binary;
using CrushGheChoi.Core.Animation;
using CrushGheChoi.Core.Effects;

namespace CrushGheChoi.Tests;

public sealed class AnimationSetLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crush-set-" + Guid.NewGuid().ToString("N"));

    public AnimationSetLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Chỉ cần phần đầu PNG (chữ ký + IHDR) vì loader không giải mã ảnh.</summary>
    private static void WritePng(string path, int w, int h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), w);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), h);
        File.WriteAllBytes(path, bytes);
    }

    private void Manifest(string json) => File.WriteAllText(Path.Combine(_dir, "manifest.json"), json);

    [Fact]
    public void Missing_manifest_throws()
    {
        Assert.Throws<AnimationSetException>(() => AnimationSetLoader.Load(_dir));
    }

    [Fact]
    public void Still_only_set()
    {
        WritePng(Path.Combine(_dir, "still.png"), 400, 600);
        Manifest("""{ "name": "Thử", "kissPoint": { "x": 0.3, "y": 0.25 } }""");

        var set = AnimationSetLoader.Load(_dir);

        Assert.True(set.IsStillOnly);
        Assert.Equal((400, 600), (set.FrameWidth, set.FrameHeight));
        Assert.Equal(new NormalizedPoint(0.3, 0.25), set.KissPoint);
        Assert.Equal(24, set.Fps);
    }

    [Fact]
    public void Missing_still_throws()
    {
        Manifest("""{ "still": "nope.png" }""");
        Assert.Throws<AnimationSetException>(() => AnimationSetLoader.Load(_dir));
    }

    [Fact]
    public void Still_outside_folder_is_rejected()
    {
        WritePng(Path.Combine(Path.GetTempPath(), "outside-still.png"), 10, 10);
        Manifest("""{ "still": "../outside-still.png" }""");
        Assert.Throws<AnimationSetException>(() => AnimationSetLoader.Load(_dir));
    }

    [Fact]
    public void Loads_clips_in_natural_order_with_kiss_event()
    {
        WritePng(Path.Combine(_dir, "still.png"), 64, 96);
        foreach (var i in new[] { 1, 2, 10, 3 })
        {
            WritePng(Path.Combine(_dir, "kiss", $"frame{i}.png"), 64, 96);
        }

        Manifest("""
        {
          "fps": 24,
          "clips": {
            "kiss": { "frames": "kiss", "events": { "kiss_release": 99 } },
            "kiss_prepare": { "frames": "missing" },
            "dance": { "frames": "kiss" }
          }
        }
        """);

        var set = AnimationSetLoader.Load(_dir);
        var kiss = set.Clip(ClipKind.Kiss)!;

        Assert.Equal(["frame1.png", "frame2.png", "frame3.png", "frame10.png"], kiss.FramePaths.Select(Path.GetFileName));
        Assert.Equal(3, kiss.KissReleaseFrame); // mốc vượt quá số khung được kẹp lại
        Assert.Null(set.Clip(ClipKind.KissPrepare));
        Assert.Equal(2, set.Warnings.Count);
    }

    [Fact]
    public void Clip_with_different_frame_size_is_dropped()
    {
        WritePng(Path.Combine(_dir, "still.png"), 64, 96);
        WritePng(Path.Combine(_dir, "wave", "001.png"), 64, 96);
        WritePng(Path.Combine(_dir, "wave", "002.png"), 80, 96);
        Manifest("""{ "clips": { "wave": { "frames": "wave" } } }""");

        var set = AnimationSetLoader.Load(_dir);

        Assert.True(set.IsStillOnly);
        Assert.Contains(set.Warnings, w => w.Contains("kích thước"));
    }

    [Fact]
    public void Loads_extra_stills_with_their_own_kiss_points()
    {
        WritePng(Path.Combine(_dir, "still.png"), 64, 96);
        WritePng(Path.Combine(_dir, "stills", "01.png"), 120, 90);
        Manifest("""
        {
          "still": "still.png",
          "stills": [
            { "file": "still.png", "kissPoint": { "x": 0.2, "y": 0.3 }, "drift": -1 },
            { "file": "stills/01.png", "kissPoint": { "x": 0.7, "y": 0.4 }, "drift": 1 },
            { "file": "stills/missing.png" },
            { "file": "../escape.png" }
          ]
        }
        """);

        var set = AnimationSetLoader.Load(_dir);

        Assert.Equal(2, set.Stills.Count);
        Assert.Equal(new NormalizedPoint(0.2, 0.3), set.Primary.KissPoint);
        Assert.Equal(-1, set.Primary.HeartDrift);
        Assert.Equal((120, 90), (set.Stills[1].Width, set.Stills[1].Height));
        Assert.Equal(new NormalizedPoint(0.7, 0.4), set.Stills[1].KissPoint);
        Assert.Single(set.Warnings);
    }

    [Fact]
    public void Set_without_stills_list_has_one_still()
    {
        WritePng(Path.Combine(_dir, "still.png"), 64, 96);
        Manifest("{}");
        var set = AnimationSetLoader.Load(_dir);
        Assert.Single(set.Stills);
        Assert.Equal(set.StillPath, set.Primary.Path);
    }

    [Fact]
    public void Broken_manifest_throws_readable_error()
    {
        Manifest("{ not json");
        var ex = Assert.Throws<AnimationSetException>(() => AnimationSetLoader.Load(_dir));
        Assert.Contains("manifest.json", ex.Message);
    }
}

public class ChoreographerTests
{
    [Fact]
    public void Greet_then_kiss_follows_plan_timeline()
    {
        var p = Choreographer.Build(PerformanceKind.GreetThenKiss);

        Assert.Equal(9.0, p.Duration, precision: 6);
        Assert.Equal(
            [StepKind.Rise, StepKind.Wave, StepKind.KissPrepare, StepKind.Kiss, StepKind.Idle, StepKind.Goodbye],
            p.Steps.Select(s => s.Kind));
        Assert.Equal(3.8, p.Steps[3].Start, precision: 6);
        Assert.InRange(p.KissRelease!.Value, 3.8, 4.6);
        Assert.InRange(p.BubbleStart, 0.8, 1.0);
        Assert.Equal(StepKind.Idle, p.StepAt(5.0).Kind);
        Assert.Equal(StepKind.Goodbye, p.StepAt(100).Kind);
    }

    [Fact]
    public void Greet_has_no_hearts()
    {
        var p = Choreographer.Build(PerformanceKind.Greet);
        Assert.Null(p.KissRelease);
        Assert.InRange(p.Duration, 6, 8);
    }

    [Fact]
    public void Kiss_clip_sets_duration_and_release_frame()
    {
        var frames = Enumerable.Range(0, 36).Select(i => $"f{i}.png").ToList();
        var set = new AnimationSetDefinition(
            "t", "1", "", "", 10, 10, 24, new NormalizedPoint(0.5, 0.3),
            new Dictionary<ClipKind, ClipDefinition>
            {
                [ClipKind.Kiss] = new(ClipKind.Kiss, frames, false, 12, null),
            },
            []);

        var p = Choreographer.Build(PerformanceKind.Kiss, set);
        var kiss = p.Steps.Single(s => s.Kind == StepKind.Kiss);

        Assert.Equal(1.5, kiss.Duration, precision: 6);
        Assert.Equal(kiss.Start + 0.5, p.KissRelease!.Value, precision: 6);
    }
}

public class HeartSimulationTests
{
    [Fact]
    public void Burst_spawns_hearts_over_time_then_cleans_up()
    {
        var sim = new HeartSimulation(new Random(1));
        sim.Burst(100, 100, 4, drift: -1);

        sim.Update(0.01);
        Assert.Single(sim.Active);

        for (var i = 0; i < 60; i++)
        {
            sim.Update(1 / 60.0);
        }

        Assert.Equal(4, sim.Active.Count);
        Assert.All(sim.Active, h => Assert.True(h.Y < 100, "tim phải bay lên"));

        for (var i = 0; i < 300; i++)
        {
            sim.Update(1 / 60.0);
        }

        Assert.True(sim.IsIdle);
    }

    [Fact]
    public void Active_hearts_are_capped()
    {
        var sim = new HeartSimulation(new Random(2));
        for (var i = 0; i < 10; i++)
        {
            sim.Burst(0, 0, 8, 1);
        }

        for (var i = 0; i < 120; i++)
        {
            sim.Update(1 / 60.0);
            Assert.True(sim.Active.Count <= HeartSimulation.MaxActive);
        }
    }

    [Fact]
    public void Reduced_motion_uses_fewer_calmer_hearts()
    {
        var sim = new HeartSimulation(new Random(3)) { ReducedMotion = true };
        sim.Burst(0, 0, 6, 1);
        for (var i = 0; i < 60; i++)
        {
            sim.Update(1 / 60.0);
        }

        Assert.Equal(2, sim.Active.Count);
        Assert.All(sim.Active, h => Assert.Equal(0, h.WobbleAmplitude));
    }

    [Fact]
    public void Heart_pops_then_fades()
    {
        var h = new HeartParticle { Life = 2.5, Pop = true };
        h.Age = 0.15;
        Assert.True(h.Scale > 1.0);
        h.Age = 2.49;
        Assert.True(h.Opacity < 0.05);
        Assert.True(h.Scale < 0.8);
    }
}

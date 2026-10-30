using CrushGheChoi.Core.Greetings;
using CrushGheChoi.Core.Settings;

namespace CrushGheChoi.Tests;

public sealed class SettingsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crush-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var result = new SettingsService(Path.Combine(_dir, "settings.json")).Load(Now);
        Assert.Equal(SettingsLoadStatus.CreatedDefault, result.Status);
        Assert.Equal(45, result.Settings.MinIntervalMinutes);
        Assert.Equal(10, result.Settings.Greetings.Count);
        Assert.False(result.Settings.SoundEnabled);
        Assert.Equal(PlacementMode.Random, result.Settings.Placement);
        Assert.DoesNotContain(result.Settings.Greetings, g => g.Contains("cố lên", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var service = new SettingsService(Path.Combine(_dir, "settings.json"));
        var s = new AppSettings
        {
            HeightDip = 380,
            Corner = ScreenCorner.BottomLeft,
            QuietStart = new TimeOnly(23, 0),
            PausedUntil = Now.AddHours(1),
            Greetings = ["Chào nha 💗"],
            HeartPalette = HeartPalette.Pastel,
        };
        service.Save(s);

        var json = File.ReadAllText(service.FilePath);
        Assert.Contains("\"corner\": \"BottomLeft\"", json);
        Assert.Contains("Chào nha", json); // tiếng Việt giữ nguyên để đọc được; emoji được escape nhưng vẫn đọc lại đúng

        var loaded = service.Load(Now).Settings;
        Assert.Equal(380, loaded.HeightDip);
        Assert.Equal(ScreenCorner.BottomLeft, loaded.Corner);
        Assert.Equal(new TimeOnly(23, 0), loaded.QuietStart);
        Assert.Equal(Now.AddHours(1), loaded.PausedUntil);
        Assert.Equal(["Chào nha 💗"], loaded.Greetings);
        Assert.Equal(HeartPalette.Pastel, loaded.HeartPalette);
    }

    [Fact]
    public void Retired_default_greeting_is_replaced_when_loading_old_settings()
    {
        var service = new SettingsService(Path.Combine(_dir, "settings.json"));
        service.Save(new AppSettings { Greetings = ["Hé lô", "Cố lên nha, sắp xong rồi đó!"] });

        var loaded = service.Load(Now).Settings;

        Assert.Equal(["Hé lô", "Tui ghé ngang thôi, làm tiếp đi nè."], loaded.Greetings);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_replaced_by_defaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ \"heightDip\": 300, oops");

        var result = new SettingsService(path).Load(Now);

        Assert.Equal(SettingsLoadStatus.RecoveredFromCorrupt, result.Status);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(AppSettings.DefaultHeightDip, result.Settings.HeightDip);
    }

    [Fact]
    public void Normalize_clamps_out_of_range_values()
    {
        var s = new AppSettings
        {
            HeightDip = 5000,
            MinIntervalMinutes = 5,
            MaxIntervalMinutes = 1,
            HeartCount = 99,
            EdgeOffsetXDip = double.NaN,
            Corner = (ScreenCorner)42,
            Greetings = ["  ", "", "  Xin chào  ", new string('a', 500)],
            PausedUntil = Now.AddMinutes(-1),
            LastAutoVisit = Now.AddDays(1),
        };

        var fixes = s.Normalize(Now);

        Assert.NotEmpty(fixes);
        Assert.Equal(AppSettings.MaxHeightDip, s.HeightDip);
        Assert.Equal(5, s.MinIntervalMinutes);
        Assert.Equal(5, s.MaxIntervalMinutes);
        Assert.Equal(AppSettings.MaxHearts, s.HeartCount);
        Assert.Equal(0, s.EdgeOffsetXDip);
        Assert.Equal(ScreenCorner.BottomRight, s.Corner);
        Assert.Equal(2, s.Greetings.Count);
        Assert.Equal("Xin chào", s.Greetings[0]);
        Assert.Equal(AppSettings.MaxGreetingLength, s.Greetings[1].Length);
        Assert.Null(s.PausedUntil);
        Assert.Null(s.LastAutoVisit);
    }

    [Fact]
    public void Full_screen_allow_list_is_cleaned()
    {
        var s = new AppSettings { FullScreenAllowedApps = [" Photoshop.exe ", "photoshop", "", "Adobe Premiere Pro"] };
        s.Normalize(Now);
        Assert.Equal(["Photoshop", "Adobe Premiere Pro"], s.FullScreenAllowedApps);
        Assert.Contains("Adobe Premiere Pro", new AppSettings().FullScreenAllowedApps);
    }

    [Fact]
    public void Empty_greetings_fall_back_to_defaults()
    {
        var s = new AppSettings { Greetings = [] };
        s.Normalize(Now);
        Assert.Equal(AppSettings.DefaultGreetings, s.Greetings);
    }

    [Fact]
    public void Export_then_import()
    {
        var path = Path.Combine(_dir, "backup.json");
        SettingsService.Export(new AppSettings { HeightDip = 250, MaxIntervalMinutes = 120 }, path);

        var imported = SettingsService.Import(path, Now);
        Assert.Equal(250, imported.HeightDip);
        Assert.Equal(120, imported.MaxIntervalMinutes);
    }

    [Fact]
    public void Import_rejects_non_settings_file()
    {
        var path = Path.Combine(_dir, "note.json");
        File.WriteAllText(path, "đây không phải JSON");
        Assert.Throws<InvalidDataException>(() => SettingsService.Import(path, Now));
    }
}

public class GreetingPickerTests
{
    [Fact]
    public void Never_repeats_the_previous_greeting()
    {
        var picker = new GreetingPicker(new Random(1));
        var list = new[] { "a", "b", "c", "d" };
        string? last = null;
        for (var i = 0; i < 1000; i++)
        {
            var g = picker.Pick(list);
            Assert.NotEqual(last, g);
            last = g;
        }
    }

    [Fact]
    public void Uses_every_greeting_before_repeating()
    {
        var picker = new GreetingPicker(new Random(2));
        var list = Enumerable.Range(0, 10).Select(i => $"g{i}").ToArray();
        var firstRound = Enumerable.Range(0, 10).Select(_ => picker.Pick(list)).ToHashSet();
        Assert.Equal(10, firstRound.Count);
    }

    [Fact]
    public void Shuffle_bag_adapts_when_list_size_changes()
    {
        var bag = new ShuffleBag(new Random(4));
        Assert.Equal(-1, bag.Next(0));
        for (var i = 0; i < 20; i++)
        {
            Assert.InRange(bag.Next(16), 0, 15);
        }

        for (var i = 0; i < 20; i++)
        {
            Assert.InRange(bag.Next(3), 0, 2);
        }
    }

    [Fact]
    public void Handles_single_and_empty_lists()
    {
        var picker = new GreetingPicker();
        Assert.Null(picker.Pick([]));
        Assert.Equal("x", picker.Pick(["x"]));
        Assert.Equal("x", picker.Pick(["x"]));
    }
}

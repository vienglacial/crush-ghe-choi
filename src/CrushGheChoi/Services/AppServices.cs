using System.IO;
using System.Media;
using System.Windows;
using Microsoft.Win32;

namespace CrushGheChoi.Services;

internal static class AppPaths
{
    /// <summary>
    /// Đặt biến này (chữ, số, gạch) để chạy một phiên riêng với dữ liệu riêng, ví dụ khi thử nghiệm,
    /// mà không đụng tới cài đặt và hình của phiên chính.
    /// </summary>
    public const string ProfileVariable = "CRUSHGHECHOI_PROFILE";

    public static string? Profile { get; } = ReadProfile();

    private static string Suffix => Profile is null ? string.Empty : "-" + Profile;

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrushGheChoi" + Suffix);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Bộ hình "Crush của tôi" do người dùng tự thêm.</summary>
    public static string UserSetDirectory => Path.Combine(DataDirectory, "crush");

    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrushGheChoi" + Suffix, "logs");

    /// <summary>Model tách nền tải về; dùng chung cho mọi phiên để không phải tải lại.</summary>
    public static string ModelDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrushGheChoi", "models");

    public static string BuiltInSetDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Crush", "default");

    public static string BundledModelDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Models");

    private static string? ReadProfile()
    {
        var value = Environment.GetEnvironmentVariable(ProfileVariable)?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 32 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? null
            : value;
    }
}

/// <summary>Nhật ký nhỏ trên máy để tra lỗi; tự cắt khi quá 1 MB.</summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppPaths.LogDirectory, "app.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Ghi nhật ký không được thì bỏ qua, không làm phiền người dùng.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Bật/tắt chạy cùng Windows qua khóa Run của người dùng hiện tại (không cần quyền quản trị).</summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CrushGheChoi";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled && Environment.ProcessPath is { } exe)
        {
            key.SetValue(ValueName, $"\"{exe}\" --tray");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

internal static class Chime
{
    private static SoundPlayer? _player;

    public static void Play()
    {
        try
        {
            if (_player is null)
            {
                var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/chime.wav"))?.Stream;
                if (stream is null)
                {
                    return;
                }

                _player = new SoundPlayer(stream);
                _player.Load();
            }

            _player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Log.Warn($"Không phát được tiếng chuông: {ex.Message}");
        }
    }
}

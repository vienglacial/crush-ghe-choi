using System.Diagnostics;
using System.Security;

namespace CrushGheChoi.Services;

internal static class AppPaths
{
    /// <summary>Đặt biến này (chữ, số, gạch) để chạy một phiên riêng với dữ liệu riêng, ví dụ khi thử nghiệm.</summary>
    public const string ProfileVariable = "CRUSHGHECHOI_PROFILE";

    public static string? Profile { get; } = ReadProfile();

    private static string Suffix => Profile is null ? string.Empty : "-" + Profile;

    /// <summary>Trên macOS là ~/Library/Application Support/CrushGheChoi.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrushGheChoi" + Suffix);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Bộ hình "Crush của tôi" do người dùng tự thêm.</summary>
    public static string UserSetDirectory => Path.Combine(DataDirectory, "crush");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Model tách nền người dùng tự cài; dùng chung cho mọi phiên.</summary>
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ghi nhật ký không được thì bỏ qua, không làm phiền người dùng.
        }
    }
}

/// <summary>Chỉ cho một phiên app chạy: giữ khóa một tệp trong thư mục dữ liệu suốt lúc app chạy.</summary>
internal sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private SingleInstanceLock(FileStream stream) => _stream = stream;

    public static SingleInstanceLock? TryAcquire()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var path = Path.Combine(AppPaths.DataDirectory, "instance.lock");
            return new SingleInstanceLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null; // phiên khác đang giữ khóa
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// Mở cùng máy trên macOS: một tệp LaunchAgent trong ~/Library/LaunchAgents trỏ tới tệp chạy của app.
/// Có hiệu lực từ lần đăng nhập sau. Chuyển app sang thư mục khác thì cần bật lại tùy chọn.
/// </summary>
internal static class StartupService
{
    private const string Label = "com.vienglacial.crushghechoi";

    public static bool Supported => OperatingSystem.IsMacOS();

    private static string AgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    public static void Apply(bool enabled)
    {
        if (!Supported)
        {
            return;
        }

        if (!enabled)
        {
            if (File.Exists(AgentPath))
            {
                File.Delete(AgentPath);
            }

            return;
        }

        var exe = Environment.ProcessPath ?? throw new IOException("Không xác định được đường dẫn của app.");
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{SecurityElement.Escape(exe)}</string>
                    <string>--tray</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>ProcessType</key>
                <string>Interactive</string>
            </dict>
            </plist>

            """;
        Directory.CreateDirectory(Path.GetDirectoryName(AgentPath)!);
        File.WriteAllText(AgentPath, plist);
    }
}

internal static class Shell
{
    /// <summary>Mở một thư mục bằng Finder (hoặc Explorer khi chạy thử trên Windows).</summary>
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { path } });
            }
            else
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Không mở được thư mục {path}: {ex.Message}");
        }
    }

    public static void OpenUrl(Uri url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { url.AbsoluteUri } });
            }
            else
            {
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Không mở được trang {url}: {ex.Message}");
        }
    }
}

using EasyMic.Audio;

namespace EasyMic.Settings;

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public ProcessingSettings Processing { get; init; } = ProcessingSettings.Default;
    public string? SourceDevice { get; init; }
    public string? SinkDevice { get; init; }
    public int LatencyMs { get; init; } = 20;

    public AppSettings Clamp() => this with
    {
        LatencyMs = Math.Clamp(LatencyMs, 5, 500),
        Processing = (Processing ?? ProcessingSettings.Default).Clamp(),
    };
}

public static class EasyMicPaths
{
    public static string ConfigDirectory
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            return Path.Combine(root, "easymic");
        }
    }

    public static string StateDirectory
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            var root = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state")
                : xdg;
            return Path.Combine(root, "easymic");
        }
    }

    public static string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

    public static string DaemonLogPath => Path.Combine(StateDirectory, "daemon.log");

    public static string NotifyLogPath => Path.Combine(StateDirectory, "notify.log");

    public static string SocketPath
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(runtime))
            {
                return Path.Combine(runtime, "easymic.sock");
            }

            return Path.Combine(Path.GetTempPath(), $"easymic-{Environment.UserName}.sock");
        }
    }

    public static string DaemonLockPath
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(runtime))
            {
                return Path.Combine(runtime, "easymic.lock");
            }

            return Path.Combine(Path.GetTempPath(), $"easymic-{Environment.UserName}.daemon.lock");
        }
    }
}

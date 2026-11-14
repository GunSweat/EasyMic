using System.Globalization;

namespace EasyMic.Audio;

/// <summary>
/// Creates a virtual microphone as a PipeWire pipe source. Audio written to the
/// FIFO by <see cref="AudioPipeline"/> is available to other applications as
/// <see cref="DisplayName"/>. Unlike a null sink + remap source, a pipe source
/// has no sink input, so screen-sharing applications cannot accidentally pick
/// up the microphone as "application audio".
/// </summary>
public sealed class VirtualMic : IAsyncDisposable
{
    // Kept for cleaning up installations created by older EasyMic versions.
    public const string SinkName = "easymic_virtual";
    public const string MonitorSource = SinkName + ".monitor";

    public const string SourceName = "easymic_virtual_mic";
    public const string DisplayName = "EasyMic-Virtual-Mic";

    private FileStream? _lock;
    private int? _sourceModule;

    public bool IsAvailable { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// FIFO read by the pipe-source module. The daemon writes processed PCM
    /// samples into it.
    /// </summary>
    public static string PipePath
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var directory = string.IsNullOrWhiteSpace(runtime)
                ? Path.GetTempPath()
                : runtime;

            return Path.Combine(directory, $"easymic-{Environment.UserName}.mic");
        }
    }

    public async Task<string?> StartAsync()
    {
        if (IsAvailable)
        {
            return null;
        }

        var lockError = AcquireLock();
        if (lockError is not null)
        {
            return lockError;
        }

        try
        {
            await CleanupStaleModulesAsync().ConfigureAwait(false);

            var fifo = PipePath;
            TryDeletePipe(fifo);

            var mkfifo = await ProcessRunner
                .RunAsync("mkfifo", new[] { "-m", "0600", fifo })
                .ConfigureAwait(false);

            if (mkfifo.ExitCode != 0)
            {
                return Fail($"Virtual microphone could not be created: {FirstLine(mkfifo.StdErr)}");
            }

            var source = await Pactl.RunAsync(
                "load-module",
                "module-pipe-source",
                $"source_name={SourceName}",
                $"file={fifo}",
                "format=s16le",
                $"rate={AudioProcessor.SampleRate}",
                "channels=1",
                $"source_properties=device.description={DisplayName}").ConfigureAwait(false);

            if (source.ExitCode != 0 ||
                !int.TryParse(source.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceModule))
            {
                TryDeletePipe(fifo);
                return Fail($"Virtual microphone could not be created: {FirstLine(source.StdErr)}");
            }

            _sourceModule = sourceModule;

            if (!await WaitForSourceAsync().ConfigureAwait(false))
            {
                await UnloadAsync(sourceModule).ConfigureAwait(false);
                _sourceModule = null;
                TryDeletePipe(fifo);
                return Fail("Virtual microphone did not become available in time.");
            }

            IsAvailable = true;
            LastError = null;
            return null;
        }
        catch (Exception exception)
        {
            await StopAsync().ConfigureAwait(false);
            return Fail($"Virtual microphone could not be created: {exception.Message}");
        }
    }

    public async Task StopAsync()
    {
        await CleanupLoopbacksAsync().ConfigureAwait(false);

        var sourceModule = _sourceModule;
        _sourceModule = null;
        if (sourceModule is not null)
        {
            await UnloadAsync(sourceModule.Value).ConfigureAwait(false);
        }

        TryDeletePipe(PipePath);

        IsAvailable = false;
        ReleaseLock();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private static async Task<bool> WaitForSourceAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var result = await Pactl.RunAsync("list", "short", "sources").ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('\t');
                    if (parts.Length > 1 && parts[1] == SourceName)
                    {
                        return true;
                    }
                }
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return false;
    }

    private string? AcquireLock()
    {
        if (_lock is not null)
        {
            return null;
        }

        var path = LockPath;

        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);

            var text = $"{Environment.ProcessId}\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();

            _lock = stream;
            return null;
        }
        catch (IOException)
        {
            return "EasyMic is already running in another instance.";
        }
        catch (UnauthorizedAccessException)
        {
            return $"Lock file could not be opened: {path}";
        }
    }

    private void ReleaseLock()
    {
        var stream = _lock;
        _lock = null;

        if (stream is null)
        {
            return;
        }

        try
        {
            stream.Dispose();
        }
        catch
        {
            // Ignore.
        }

        try
        {
            File.Delete(LockPath);
        }
        catch
        {
            // Ignore.
        }
    }

    private static string LockPath
        => Path.Combine(Path.GetTempPath(), $"easymic-{Environment.UserName}.lock");

    private static readonly string[] LegacySinkNames = { "micmon_virtual", SinkName };
    private static readonly string[] LegacySourceNames = { "micmon_virtual_mic", SourceName };

    private static async Task CleanupLoopbacksAsync()
    {
        var modules = await ListModulesAsync().ConfigureAwait(false);

        foreach (var module in modules.Where(module =>
                     module.Name == "module-loopback" &&
                     (LegacySourceNames.Any(source =>
                          HasArgument(module.Arguments, $"source={source}")) ||
                      LegacySinkNames.Any(sink =>
                          HasArgument(module.Arguments, $"source={sink}.monitor")))))
        {
            await UnloadAsync(module.Id).ConfigureAwait(false);
        }
    }

    private static async Task CleanupStaleModulesAsync()
    {
        var modules = await ListModulesAsync().ConfigureAwait(false);
        var stale = new List<(int Id, int Priority)>();

        foreach (var module in modules)
        {
            if (module.Name == "module-pipe-source" &&
                LegacySourceNames.Any(source => HasArgument(module.Arguments, $"source_name={source}")))
            {
                stale.Add((module.Id, 0));
            }
            else if (module.Name == "module-loopback" &&
                     (LegacySourceNames.Any(source =>
                          HasArgument(module.Arguments, $"source={source}")) ||
                      LegacySinkNames.Any(sink =>
                          HasArgument(module.Arguments, $"source={sink}.monitor"))))
            {
                stale.Add((module.Id, 0));
            }
            else if (module.Name == "module-remap-source" &&
                     LegacySourceNames.Any(source => HasArgument(module.Arguments, $"source_name={source}")))
            {
                stale.Add((module.Id, 1));
            }
            else if (module.Name == "module-null-sink" &&
                     LegacySinkNames.Any(sink => HasArgument(module.Arguments, $"sink_name={sink}")))
            {
                stale.Add((module.Id, 2));
            }
        }

        foreach (var (id, _) in stale.OrderBy(item => item.Priority))
        {
            await UnloadAsync(id).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<(int Id, string Name, string Arguments)>> ListModulesAsync()
    {
        try
        {
            var result = await ProcessRunner
                .RunAsync("pactl", new[] { "list", "short", "modules" })
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                return Array.Empty<(int, string, string)>();
            }

            var modules = new List<(int, string, string)>();

            foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2 ||
                    !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    continue;
                }

                modules.Add((id, parts[1], parts.Length > 2 ? parts[2] : string.Empty));
            }

            return modules;
        }
        catch
        {
            return Array.Empty<(int, string, string)>();
        }
    }

    private static async Task UnloadAsync(int moduleId)
    {
        try
        {
            await Pactl.RunAsync(
                "unload-module",
                moduleId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
        catch
        {
            // Best effort.
        }
    }

    private static bool HasArgument(string arguments, string token)
        => arguments
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(token, StringComparer.Ordinal);

    private static void TryDeletePipe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private string Fail(string message)
    {
        LastError = message;
        IsAvailable = false;
        ReleaseLock();
        return message;
    }

    private static string FirstLine(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

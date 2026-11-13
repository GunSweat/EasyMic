using System.Globalization;

namespace EasyMic.Audio;

/// <summary>
/// Creates a virtual microphone with a null sink and a remap source. Audio that
/// is written to the sink by <see cref="AudioPipeline"/> is available to other
/// applications as <see cref="DisplayName"/>.
/// </summary>
public sealed class VirtualMic : IAsyncDisposable
{
    public const string SinkName = "easymic_virtual";
    public const string SourceName = "easymic_virtual_mic";
    public const string MonitorSource = SinkName + ".monitor";
    public const string DisplayName = "EasyMic-Virtual-Mic";

    private const string SinkDisplayName = "EasyMic-Virtual";

    private FileStream? _lock;
    private int? _sinkModule;
    private int? _sourceModule;

    public bool IsAvailable { get; private set; }

    public string? LastError { get; private set; }

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

            var sink = await Pactl.RunAsync(
                "load-module",
                "module-null-sink",
                $"sink_name={SinkName}",
                $"sink_properties=device.description={SinkDisplayName}").ConfigureAwait(false);

            if (sink.ExitCode != 0 ||
                !int.TryParse(sink.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sinkModule))
            {
                return Fail($"Virtuelles Mikrofon konnte nicht erstellt werden: {FirstLine(sink.StdErr)}");
            }

            _sinkModule = sinkModule;

            var source = await Pactl.RunAsync(
                "load-module",
                "module-remap-source",
                $"master={MonitorSource}",
                $"source_name={SourceName}",
                $"source_properties=device.description={DisplayName}").ConfigureAwait(false);

            if (source.ExitCode != 0 ||
                !int.TryParse(source.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceModule))
            {
                await UnloadAsync(sinkModule).ConfigureAwait(false);
                _sinkModule = null;
                return Fail($"Virtuelles Mikrofon konnte nicht erstellt werden: {FirstLine(source.StdErr)}");
            }

            _sourceModule = sourceModule;
            IsAvailable = true;
            LastError = null;
            return null;
        }
        catch (Exception exception)
        {
            await StopAsync().ConfigureAwait(false);
            return Fail($"Virtuelles Mikrofon konnte nicht erstellt werden: {exception.Message}");
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

        var sinkModule = _sinkModule;
        _sinkModule = null;
        if (sinkModule is not null)
        {
            await UnloadAsync(sinkModule.Value).ConfigureAwait(false);
        }

        IsAvailable = false;
        ReleaseLock();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
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
            return "EasyMic läuft bereits in einer anderen Instanz.";
        }
        catch (UnauthorizedAccessException)
        {
            return $"Sperrdatei konnte nicht geöffnet werden: {path}";
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
                     LegacySinkNames.Any(sink => HasArgument(module.Arguments, $"source={sink}.monitor"))))
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
            if (module.Name == "module-loopback" &&
                LegacySinkNames.Any(sink => HasArgument(module.Arguments, $"source={sink}.monitor")))
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

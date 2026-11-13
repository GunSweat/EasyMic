using System.Globalization;
using System.Text.Json;

namespace EasyMic.Audio;

/// <summary>
/// Routes the processed microphone to an output device using PulseAudio/
/// PipeWire's module-loopback. The loopback stream is muted for a moment after
/// creation and then faded in; disabling fades out before the module is
/// removed. This hides the short burst that a freshly created loopback can
/// produce. No audio data runs through this process, so latency stays low.
/// </summary>
public sealed class LoopbackMonitor : IAsyncDisposable
{
    private const string MonitorApplication = "EasyMic-Monitor";
    private const int FindAttempts = 8;
    private const int FindDelayMs = 15;
    private const int SettleMilliseconds = 80;
    private const int FadeSteps = 14;
    private const int FadeStepMilliseconds = 22;

    private readonly string _sourceName;
    private readonly string? _sinkName;
    private readonly int _latencyMs;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private int? _moduleId;
    private int? _sinkInputIndex;

    public LoopbackMonitor(string sourceName, string? sinkName, int latencyMs)
    {
        _sourceName = sourceName;
        _sinkName = sinkName;
        _latencyMs = latencyMs;
    }

    public bool IsEnabled => _moduleId is not null;

    public string? LastError { get; private set; }

    public async Task<string?> EnableAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsEnabled)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(_sinkName))
            {
                return "Kein Ausgabegerät gefunden – Monitoring ist nicht verfügbar.";
            }

            LastError = null;

            var result = await Pactl.RunAsync(
                "load-module",
                "module-loopback",
                $"source={_sourceName}",
                $"sink={_sinkName}",
                $"latency_msec={_latencyMs.ToString(CultureInfo.InvariantCulture)}",
                "source_dont_move=true",
                "sink_dont_move=true",
                "source_output_properties=application.name=EasyMic",
                $"sink_input_properties=application.name={MonitorApplication}").ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                LastError = $"Monitoring konnte nicht aktiviert werden: {FirstLine(result.StdErr)}";
                return LastError;
            }

            if (!int.TryParse(
                    result.StdOut.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var moduleId))
            {
                LastError = $"Unerwartete Antwort von pactl: {result.StdOut}";
                return LastError;
            }

            _moduleId = moduleId;

            var index = await FindSinkInputAsync().ConfigureAwait(false);
            if (index is null)
            {
                await UnloadAsync(moduleId).ConfigureAwait(false);
                _moduleId = null;
                LastError = "Der Monitoring-Stream konnte nicht gefunden werden.";
                return LastError;
            }

            _sinkInputIndex = index;
            await SetVolumeAsync(index.Value, 0).ConfigureAwait(false);

            // Let the loopback fill its buffers while it is still silent, then
            // bring the level up smoothly.
            await Task.Delay(SettleMilliseconds).ConfigureAwait(false);
            await RampAsync(index.Value, 0, 100).ConfigureAwait(false);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> DisableAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var moduleId = _moduleId;
            var index = _sinkInputIndex;
            _moduleId = null;
            _sinkInputIndex = null;

            if (moduleId is null)
            {
                return null;
            }

            if (index is not null)
            {
                await RampAsync(index.Value, 100, 0).ConfigureAwait(false);
            }

            var result = await UnloadAsync(moduleId.Value).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                LastError = $"Monitoring konnte nicht deaktiviert werden: {FirstLine(result.StdErr)}";
                return LastError;
            }

            LastError = null;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisableAsync().ConfigureAwait(false);
    }

    private static Task<ProcessResult> UnloadAsync(int moduleId)
        => Pactl.RunAsync(
            "unload-module",
            moduleId.ToString(CultureInfo.InvariantCulture));

    private static async Task RampAsync(int index, int from, int to)
    {
        for (var step = 1; step <= FadeSteps; step++)
        {
            var t = step / (float)FadeSteps;
            var eased = t * t * (3f - 2f * t);
            var percent = (int)MathF.Round(from + (to - from) * eased);
            await SetVolumeAsync(index, percent).ConfigureAwait(false);
            await Task.Delay(FadeStepMilliseconds).ConfigureAwait(false);
        }
    }

    private static async Task SetVolumeAsync(int index, int percent)
    {
        await Pactl.RunAsync(
            "set-sink-input-volume",
            index.ToString(CultureInfo.InvariantCulture),
            $"{percent.ToString(CultureInfo.InvariantCulture)}%").ConfigureAwait(false);
    }

    private static async Task<int?> FindSinkInputAsync()
    {
        for (var attempt = 0; attempt < FindAttempts; attempt++)
        {
            var index = await QuerySinkInputIndexAsync().ConfigureAwait(false);
            if (index is not null)
            {
                return index;
            }

            await Task.Delay(FindDelayMs).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<int?> QuerySinkInputIndexAsync()
    {
        var result = await Pactl.RunAsync("-f", "json", "list", "sink-inputs").ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            int? found = null;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("index", out var indexElement) ||
                    !indexElement.TryGetInt32(out var index))
                {
                    continue;
                }

                if (!element.TryGetProperty("properties", out var properties) ||
                    !properties.TryGetProperty("application.name", out var application) ||
                    !string.Equals(application.GetString(), MonitorApplication, StringComparison.Ordinal))
                {
                    continue;
                }

                if (found is null || index > found)
                {
                    found = index;
                }
            }

            return found;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FirstLine(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

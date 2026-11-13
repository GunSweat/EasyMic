using System.Globalization;

namespace EasyMic.Audio;

/// <summary>
/// Routes the microphone to an output device using PulseAudio/PipeWire's
/// module-loopback. No audio data runs through this process, so latency stays low.
/// </summary>
public sealed class LoopbackMonitor : IAsyncDisposable
{
    private readonly string _sourceName;
    private readonly string? _sinkName;
    private readonly int _latencyMs;

    private int? _moduleId;

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
            "sink_input_properties=application.name=EasyMic").ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            LastError = $"Monitoring konnte nicht aktiviert werden: {FirstLine(result.StdErr)}";
            return LastError;
        }

        if (!int.TryParse(result.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var moduleId))
        {
            LastError = $"Unerwartete Antwort von pactl: {result.StdOut}";
            return LastError;
        }

        _moduleId = moduleId;
        return null;
    }

    public async Task<string?> DisableAsync()
    {
        var moduleId = _moduleId;
        _moduleId = null;

        if (moduleId is null)
        {
            return null;
        }

        var result = await Pactl.RunAsync(
            "unload-module",
            moduleId.Value.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            LastError = $"Monitoring konnte nicht deaktiviert werden: {FirstLine(result.StdErr)}";
            return LastError;
        }

        LastError = null;
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisableAsync().ConfigureAwait(false);
    }

    private static string FirstLine(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

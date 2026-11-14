using System.Globalization;
using EasyMic.Audio;

namespace EasyMic;

public sealed record TuningParameter(
    string Id,
    string Label,
    float Minimum,
    float Maximum,
    float FineStep,
    float CoarseStep,
    Func<ProcessingSettings, float> Get,
    Func<ProcessingSettings, float, ProcessingSettings> Apply,
    Func<float, string> Format,
    string Group,
    IReadOnlyList<string> Aliases)
{
    public float Read(ProcessingSettings settings) => Get(settings);

    public ProcessingSettings Write(ProcessingSettings settings, float value)
        => Apply(settings, Math.Clamp(value, Minimum, Maximum));

    public float Clamp(float value) => Math.Clamp(value, Minimum, Maximum);
}

public sealed record TuningToggle(
    string Id,
    string Label,
    Func<ProcessingSettings, bool> Get,
    Func<ProcessingSettings, bool, ProcessingSettings> Apply,
    IReadOnlyList<string> Aliases)
{
    public bool Read(ProcessingSettings settings) => Get(settings);

    public ProcessingSettings Write(ProcessingSettings settings, bool value)
        => Apply(settings, value);
}

public static class ParameterCatalog
{
    private static string Percent(float value) => $"{MathF.Round(value * 100f):0} %";

    private static string SignedDb(float value) => $"{(value >= 0 ? "+" : string.Empty)}{value.ToString("0.0", CultureInfo.InvariantCulture)} dB";

    private static string PlainDb(float value) => $"{value.ToString("0", CultureInfo.InvariantCulture)} dB";

    private static string SignedOctaves(float value)
        => $"{(value >= 0 ? "+" : string.Empty)}{value.ToString("0.00", CultureInfo.InvariantCulture)} oct.";

    private static string Milliseconds(float value) => $"{MathF.Round(value):0} ms";

    public static IReadOnlyList<TuningParameter> Main { get; } = new[]
    {
        new TuningParameter(
            "pitch", "PITCH", -1f, 1f, 0.05f, 0.25f,
            settings => settings.PitchOctaves,
            (settings, value) => settings with { PitchOctaves = value },
            SignedOctaves, "Effects",
            new[] { "pitch" }),

        new TuningParameter(
            "volume", "VOLUME", 0f, 2f, 0.02f, 0.10f,
            settings => settings.Volume,
            (settings, value) => settings with { Volume = value },
            Percent, "Effects",
            new[] { "volume" }),

        new TuningParameter(
            "tone", "TONE", -1f, 1f, 0.04f, 0.20f,
            settings => settings.Tone,
            (settings, value) => settings with { Tone = value },
            value => SignedDb(value * 12f), "Effects",
            new[] { "tone" }),

        new TuningParameter(
            "gate", "GATE", 0f, 1f, 0.02f, 0.10f,
            settings => settings.GateStrength,
            (settings, value) => settings with { GateStrength = value },
            Percent, "Noise",
            new[] { "gate" }),

        new TuningParameter(
            "threshold", "THRESHOLD", -80f, -10f, 1f, 5f,
            settings => settings.GateThresholdDb,
            (settings, value) => settings with { GateThresholdDb = value },
            PlainDb, "Noise",
            new[] { "threshold" }),

        new TuningParameter(
            "denoise", "DENOISE", 0f, 1f, 0.02f, 0.10f,
            settings => settings.DenoiseStrength,
            (settings, value) => settings with { DenoiseStrength = value },
            Percent, "Noise",
            new[] { "denoise", "noise-reduction" }),

        new TuningParameter(
            "reduction", "REDUCTION", 6f, 40f, 1f, 4f,
            settings => settings.DenoiseReductionDb,
            (settings, value) => settings with { DenoiseReductionDb = value },
            PlainDb, "Noise",
            new[] { "reduction" }),

        new TuningParameter(
            "mix", "MIX", 0f, 1f, 0.02f, 0.10f,
            settings => settings.NoiseMix,
            (settings, value) => settings with { NoiseMix = value },
            Percent, "Noise",
            new[] { "mix", "noise-mix" }),
    };

    public static IReadOnlyList<TuningParameter> Details { get; } = new[]
    {
        new TuningParameter(
            "gate-attack", "Attack", 1f, 200f, 1f, 10f,
            settings => settings.GateAttackMs,
            (settings, value) => settings with { GateAttackMs = value },
            Milliseconds, "Gate Details",
            new[] { "gate-attack", "attack" }),

        new TuningParameter(
            "gate-release", "Release", 20f, 2000f, 10f, 100f,
            settings => settings.GateReleaseMs,
            (settings, value) => settings with { GateReleaseMs = value },
            Milliseconds, "Gate Details",
            new[] { "gate-release", "release" }),

        new TuningParameter(
            "gate-hold", "Hold", 0f, 1000f, 10f, 100f,
            settings => settings.GateHoldMs,
            (settings, value) => settings with { GateHoldMs = value },
            Milliseconds, "Gate Details",
            new[] { "gate-hold", "hold" }),

        new TuningParameter(
            "gate-damping", "Damping", 0f, 60f, 1f, 5f,
            settings => settings.GateMaxReductionDb,
            (settings, value) => settings with { GateMaxReductionDb = value },
            PlainDb, "Gate Details",
            new[] { "gate-damping", "damping" }),

        new TuningParameter(
            "denoise-floor", "Noise Floor", -80f, -20f, 1f, 5f,
            settings => settings.DenoiseFloorDb,
            (settings, value) => settings with { DenoiseFloorDb = value },
            PlainDb, "Denoise Details",
            new[] { "denoise-floor", "noise-floor", "floor" }),

        new TuningParameter(
            "denoise-smoothing", "Smoothing", 0f, 0.95f, 0.01f, 0.05f,
            settings => settings.DenoiseSmoothing,
            (settings, value) => settings with { DenoiseSmoothing = value },
            value => $"{MathF.Round(value * 100f):0} %", "Denoise Details",
            new[] { "denoise-smoothing", "smoothing" }),
    };

    public static IReadOnlyList<TuningToggle> Toggles { get; } = new[]
    {
        new TuningToggle(
            "gate-on", "Noise-Gate",
            settings => settings.GateEnabled,
            (settings, value) => settings with { GateEnabled = value },
            new[] { "gate-on", "gate-enabled" }),

        new TuningToggle(
            "denoise-on", "Denoise",
            settings => settings.DenoiseEnabled,
            (settings, value) => settings with { DenoiseEnabled = value },
            new[] { "denoise-on", "denoise-enabled" }),

        new TuningToggle(
            "limiter-on", "Limiter",
            settings => settings.LimiterEnabled,
            (settings, value) => settings with { LimiterEnabled = value },
            new[] { "limiter-on", "limiter-enabled", "limiter" }),
    };

    public static IReadOnlyList<TuningParameter> All { get; }
        = Main.Concat(Details).ToArray();

    public static TuningParameter? Find(string id)
        => All.FirstOrDefault(parameter =>
            parameter.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            parameter.Aliases.Any(alias => alias.Equals(id, StringComparison.OrdinalIgnoreCase)));

    public static TuningToggle? FindToggle(string id)
        => Toggles.FirstOrDefault(toggle =>
            toggle.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            toggle.Aliases.Any(alias => alias.Equals(id, StringComparison.OrdinalIgnoreCase)));
}

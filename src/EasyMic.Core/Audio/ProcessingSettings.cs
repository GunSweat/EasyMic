namespace EasyMic.Audio;

/// <summary>
/// Immutable snapshot of all processing controls. The daemon replaces the whole
/// record atomically, so the audio thread always sees a consistent set of values.
/// </summary>
public sealed record ProcessingSettings
{
    public float PitchOctaves { get; init; }
    public float Volume { get; init; } = 1f;
    public float Tone { get; init; }

    public float GateStrength { get; init; } = 0.3f;
    public float GateThresholdDb { get; init; } = -45f;
    public float DenoiseStrength { get; init; } = 0.5f;
    public float DenoiseReductionDb { get; init; } = 24f;
    public float NoiseMix { get; init; } = 1f;

    public float GateAttackMs { get; init; } = 10f;
    public float GateReleaseMs { get; init; } = 150f;
    public float GateHoldMs { get; init; } = 100f;
    public float GateMaxReductionDb { get; init; } = 30f;

    public float DenoiseFloorDb { get; init; } = -55f;
    public float DenoiseSmoothing { get; init; } = 0.7f;

    public bool GateEnabled { get; init; } = true;
    public bool DenoiseEnabled { get; init; } = true;
    public bool LimiterEnabled { get; init; } = true;
    public float LimiterCeilingDb { get; init; } = -1f;

    public static ProcessingSettings Default { get; } = new();

    public ProcessingSettings Clamp() => this with
    {
        PitchOctaves = Math.Clamp(PitchOctaves, -1f, 1f),
        Volume = Math.Clamp(Volume, 0f, 2f),
        Tone = Math.Clamp(Tone, -1f, 1f),
        GateStrength = Math.Clamp(GateStrength, 0f, 1f),
        GateThresholdDb = Math.Clamp(GateThresholdDb, -80f, -10f),
        DenoiseStrength = Math.Clamp(DenoiseStrength, 0f, 1f),
        DenoiseReductionDb = Math.Clamp(DenoiseReductionDb, 6f, 40f),
        NoiseMix = Math.Clamp(NoiseMix, 0f, 1f),
        GateAttackMs = Math.Clamp(GateAttackMs, 1f, 200f),
        GateReleaseMs = Math.Clamp(GateReleaseMs, 20f, 2000f),
        GateHoldMs = Math.Clamp(GateHoldMs, 0f, 1000f),
        GateMaxReductionDb = Math.Clamp(GateMaxReductionDb, 0f, 60f),
        DenoiseFloorDb = Math.Clamp(DenoiseFloorDb, -80f, -20f),
        DenoiseSmoothing = Math.Clamp(DenoiseSmoothing, 0f, 0.95f),
        LimiterCeilingDb = Math.Clamp(LimiterCeilingDb, -12f, 0f),
    };
}

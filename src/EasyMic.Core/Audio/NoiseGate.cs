namespace EasyMic.Audio;

/// <summary>
/// Sample-based noise gate with attack, release and hold. The gate attenuation
/// is scaled by <see cref="ProcessingSettings.GateStrength"/> so that 0 % is a
/// transparent bypass and 100 % uses the full configured reduction.
/// </summary>
public sealed class NoiseGate
{
    private const float SampleRate = 48_000f;

    private float _envelope;
    private float _gain = 1f;
    private float _holdRemaining;
    private bool _open;

    public bool IsOpen => _open;

    public void Process(Span<float> samples, ProcessingSettings settings)
    {
        if (samples.Length == 0)
        {
            return;
        }

        var thresholdDb = Math.Clamp(settings.GateThresholdDb, -80f, -10f);
        var strength = Math.Clamp(settings.GateStrength, 0f, 1f);
        var reductionDb = Math.Clamp(settings.GateMaxReductionDb, 0f, 60f) * strength;
        var floorGain = MathF.Pow(10f, -reductionDb / 20f);

        if (!settings.GateEnabled || strength <= 0.0001f)
        {
            if (_gain < 0.9999f)
            {
                _gain = 1f;
            }

            _open = true;
            _holdRemaining = 0f;
            return;
        }

        // Fast attack, slow release envelope on the absolute input level.
        var attack = 1f - MathF.Exp(-1f / (0.002f * SampleRate));
        var release = 1f - MathF.Exp(-1f / (0.080f * SampleRate));

        var openThreshold = MathF.Pow(10f, (thresholdDb + 2f) / 20f);
        var closeThreshold = MathF.Pow(10f, (thresholdDb - 2f) / 20f);
        var holdSeconds = Math.Clamp(settings.GateHoldMs, 0f, 1000f) / 1000f;

        var gainAttack = 1f - MathF.Exp(-1f / (Math.Max(settings.GateAttackMs, 0.5f) / 1000f * SampleRate));
        var gainRelease = 1f - MathF.Exp(-1f / (Math.Max(settings.GateReleaseMs, 5f) / 1000f * SampleRate));

        for (var i = 0; i < samples.Length; i++)
        {
            var absolute = MathF.Abs(samples[i]);
            var coefficient = absolute > _envelope ? attack : release;
            _envelope += (absolute - _envelope) * coefficient;

            if (_envelope >= openThreshold)
            {
                if (!_open)
                {
                    _open = true;
                }

                _holdRemaining = holdSeconds;
            }
            else if (_envelope < closeThreshold)
            {
                if (_holdRemaining > 0f)
                {
                    _holdRemaining -= 1f / SampleRate;
                }
                else
                {
                    _open = false;
                }
            }

            var target = _open ? 1f : floorGain;
            var smoothing = target > _gain ? gainAttack : gainRelease;
            _gain += (target - _gain) * smoothing;
            samples[i] *= _gain;
        }
    }

    public void Reset()
    {
        _envelope = 0f;
        _gain = 1f;
        _holdRemaining = 0f;
        _open = true;
    }
}

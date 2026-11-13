namespace EasyMic.Audio;

/// <summary>
/// Fast peak limiter with a short attack and a smooth release. It keeps the
/// output below the configured ceiling and hard-clips any remaining overshoot.
/// </summary>
public sealed class OutputLimiter
{
    private const float SampleRate = 48_000f;

    private float _envelope;
    private float _gain = 1f;

    public void Process(Span<float> samples, ProcessingSettings settings)
    {
        if (samples.Length == 0)
        {
            return;
        }

        if (!settings.LimiterEnabled)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = Math.Clamp(samples[i], -1f, 1f);
            }

            _gain = 1f;
            _envelope = 0f;
            return;
        }

        var ceiling = MathF.Pow(10f, Math.Clamp(settings.LimiterCeilingDb, -12f, 0f) / 20f);
        var attack = 1f - MathF.Exp(-1f / (0.0015f * SampleRate));
        var release = 1f - MathF.Exp(-1f / (0.120f * SampleRate));

        for (var i = 0; i < samples.Length; i++)
        {
            var absolute = MathF.Abs(samples[i]);
            var coefficient = absolute > _envelope ? attack : release;
            _envelope += (absolute - _envelope) * coefficient;

            var target = _envelope > ceiling && _envelope > 1e-6f
                ? ceiling / _envelope
                : 1f;

            var smoothing = target < _gain ? attack : release;
            _gain += (target - _gain) * smoothing;

            samples[i] = Math.Clamp(samples[i] * _gain, -ceiling, ceiling);
        }
    }

    public void Reset()
    {
        _envelope = 0f;
        _gain = 1f;
    }
}

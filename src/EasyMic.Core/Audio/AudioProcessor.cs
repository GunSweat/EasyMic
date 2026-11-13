using SoundTouch;

namespace EasyMic.Audio;

/// <summary>
/// Applies the complete processing chain to 48 kHz mono 16-bit PCM:
/// spectral denoise, dry/wet mix, gate, tilt EQ, volume, pitch (SoundTouch),
/// then the output limiter. All effects therefore apply both to the virtual
/// microphone and to the monitoring loopback.
/// </summary>
public sealed class AudioProcessor
{
    public const int SampleRate = 48_000;

    private const float VolumeSmoothingSeconds = 0.015f;
    private const float ToneSmoothingSeconds = 0.015f;
    private const float ToneCutoffHz = 700f;
    private const float ToneMaxDb = 12f;

    private readonly SoundTouchProcessor _pitch;
    private readonly NoiseGate _gate = new();
    private readonly SpectralDenoise _denoise = new();
    private readonly OutputLimiter _limiter = new();

    private float[] _work = new float[2048];
    private float[] _dry = new float[2048];
    private float[] _pitchOutput = new float[16384];

    private float _lowState;
    private float _volume = 1f;
    private float _tone;
    private float _pitchOctaves;

    public AudioProcessor()
    {
        _pitch = new SoundTouchProcessor
        {
            SampleRate = SampleRate,
            Channels = 1,
        };

        // The defaults add roughly 100 ms of latency. These values cut that down
        // to about 30 ms, which is much more pleasant for live monitoring.
        _pitch.SetSetting(SettingId.UseQuickSeek, 1);
        _pitch.SetSetting(SettingId.SequenceDurationMs, 20);
        _pitch.SetSetting(SettingId.SeekWindowDurationMs, 10);
        _pitch.SetSetting(SettingId.OverlapDurationMs, 5);
    }

    public bool GateOpen => _gate.IsOpen;

    public short[] Process(ReadOnlySpan<short> samples, ProcessingSettings settings)
    {
        if (samples.Length == 0)
        {
            return Array.Empty<short>();
        }

        EnsureCapacity(samples.Length);

        var work = _work.AsSpan(0, samples.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            work[i] = samples[i] / 32768f;
        }

        // Denoise before the gate: the noise estimator must see the ungated
        // input, otherwise a closed gate makes it learn a noise floor that is
        // far too low and the noise returns as soon as speech opens the gate.
        work.CopyTo(_dry);
        _denoise.Process(work, settings);

        var mix = Math.Clamp(settings.NoiseMix, 0f, 1f);
        if (mix < 0.9999f)
        {
            var dry = _dry.AsSpan(0, samples.Length);
            for (var i = 0; i < work.Length; i++)
            {
                work[i] = dry[i] * (1f - mix) + work[i] * mix;
            }
        }

        _gate.Process(work, settings);

        ApplyToneAndVolume(work, settings);

        return RunPitch(work, settings);
    }

    private void ApplyToneAndVolume(Span<float> samples, ProcessingSettings settings)
    {
        var targetVolume = Math.Clamp(settings.Volume, 0f, 2f);
        var targetTone = Math.Clamp(settings.Tone, -1f, 1f);

        var volumeStep = 1f - MathF.Exp(-1f / (VolumeSmoothingSeconds * SampleRate));
        var toneStep = 1f - MathF.Exp(-1f / (ToneSmoothingSeconds * SampleRate));
        var lowAlpha = MathF.Exp(-2f * MathF.PI * ToneCutoffHz / SampleRate);

        for (var i = 0; i < samples.Length; i++)
        {
            _volume += (targetVolume - _volume) * volumeStep;
            _tone += (targetTone - _tone) * toneStep;

            var sample = samples[i];
            _lowState = lowAlpha * _lowState + (1f - lowAlpha) * sample;
            var high = sample - _lowState;

            // A tilt of +1 boosts highs by 12 dB and cuts lows by 12 dB;
            // -1 does the opposite. At 0 both paths have unity gain.
            var lowGain = MathF.Pow(10f, -_tone * ToneMaxDb / 20f);
            var highGain = MathF.Pow(10f, _tone * ToneMaxDb / 20f);
            samples[i] = Math.Clamp((_lowState * lowGain + high * highGain) * _volume, -1f, 1f);
        }
    }

    private short[] RunPitch(ReadOnlySpan<float> samples, ProcessingSettings settings)
    {
        var targetPitch = Math.Clamp(settings.PitchOctaves, -1f, 1f);

        // Smoothing the pitch avoids hard jumps when the slider is dragged quickly.
        _pitchOctaves += (targetPitch - _pitchOctaves) * 0.25f;
        if (MathF.Abs(_pitchOctaves - (float)_pitch.PitchOctaves) > 0.0005f)
        {
            _pitch.PitchOctaves = _pitchOctaves;
        }

        _pitch.PutSamples(samples, samples.Length);

        var count = _pitch.ReceiveSamples(_pitchOutput, _pitchOutput.Length);
        if (count <= 0)
        {
            return Array.Empty<short>();
        }

        _limiter.Process(_pitchOutput.AsSpan(0, count), settings);

        var result = new short[count];
        for (var i = 0; i < count; i++)
        {
            var value = MathF.Round(_pitchOutput[i] * 32767f);
            result[i] = (short)Math.Clamp((int)value, short.MinValue, short.MaxValue);
        }

        return result;
    }

    private void EnsureCapacity(int sampleCount)
    {
        if (_work.Length < sampleCount)
        {
            Array.Resize(ref _work, Math.Max(sampleCount, _work.Length * 2));
        }

        if (_dry.Length < sampleCount)
        {
            Array.Resize(ref _dry, Math.Max(sampleCount, _dry.Length * 2));
        }
    }

    public void Reset()
    {
        _pitch.Clear();
        _gate.Reset();
        _denoise.Reset();
        _limiter.Reset();
        _lowState = 0f;
        _volume = 1f;
        _tone = 0f;
        _pitchOctaves = 0f;
    }
}

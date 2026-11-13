namespace EasyMic.Audio;

/// <summary>
/// Streaming spectral denoiser using a Hann-windowed STFT (1024 samples, hop
/// 256) with a self-made radix-2 FFT. The noise floor is tracked per bin with
/// minimum statistics over roughly two seconds, then subtracted from the
/// smoothed magnitude spectrum. Overlap-add reconstruction adds about 21 ms of
/// latency, which is acceptable for live use.
/// </summary>
public sealed class SpectralDenoise
{
    private const int FrameSize = 1024;
    private const int HopSize = 256;
    private const int BinCount = FrameSize / 2 + 1;

    // Minimum statistics: the observed power is split into 43 ms blocks and
    // the minimum over the last two seconds is used as the noise estimate.
    private const int BlockHops = 8;
    private const int BlockCount = 48;
    private const float PowerSmoothing = 0.85f;
    private const float MinimumBias = 3.0f;

    // The first hops only contain a partially filled analysis window, which
    // would otherwise be mistaken for an extremely low noise floor.
    private const int WarmupHops = FrameSize / HopSize;

    private readonly float[] _window = new float[FrameSize];
    private readonly float[] _history = new float[FrameSize];
    private readonly float[] _online = new float[FrameSize];
    private readonly float[] _ola = new float[FrameSize];
    private readonly float[] _power = new float[BinCount];
    private readonly float[] _noise = new float[BinCount];
    private readonly float[] _gain = new float[BinCount];
    private readonly float[] _target = new float[BinCount];
    private readonly float[] _blockMin = new float[BinCount];
    private readonly float[] _ringMin = new float[BinCount];
    private readonly float[] _blockMinima = new float[BinCount * BlockCount];
    private readonly float[] _real = new float[FrameSize];
    private readonly float[] _imag = new float[FrameSize];

    private readonly float[] _pending = new float[FrameSize * 2];
    private readonly float[] _ready = new float[FrameSize * 2];

    private readonly float _windowPower;
    private int _pendingCount;
    private int _readyCount;
    private int _blockHop;
    private int _blockIndex;
    private int _blocksFilled;
    private int _warmupHops = WarmupHops;
    private bool _firstHop = true;

    public SpectralDenoise()
    {
        for (var i = 0; i < FrameSize; i++)
        {
            _window[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (FrameSize - 1));
            _windowPower += _window[i] * _window[i];
        }

        Array.Fill(_gain, 1f);
        Array.Fill(_blockMin, float.MaxValue);
        Array.Fill(_ringMin, float.MaxValue);
        Array.Fill(_blockMinima, float.MaxValue);
    }

    public void Process(Span<float> samples, ProcessingSettings settings)
    {
        if (samples.Length == 0)
        {
            return;
        }

        Append(samples);

        while (_pendingCount >= HopSize)
        {
            ProcessHop(settings);
            Array.Copy(_pending, HopSize, _pending, 0, _pendingCount - HopSize);
            _pendingCount -= HopSize;
        }

        var available = Math.Min(samples.Length, _readyCount);
        _ready.AsSpan(0, available).CopyTo(samples);
        if (available < samples.Length)
        {
            samples[available..].Clear();
        }

        if (available > 0)
        {
            Array.Copy(_ready, available, _ready, 0, _readyCount - available);
            _readyCount -= available;
        }
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        if (_pendingCount + samples.Length > _pending.Length)
        {
            // Should not happen with our 960-sample blocks, but stay safe.
            samples = samples[..Math.Max(0, _pending.Length - _pendingCount)];
        }

        samples.CopyTo(_pending.AsSpan(_pendingCount));
        _pendingCount += samples.Length;
    }

    private void ProcessHop(ProcessingSettings settings)
    {
        Array.Copy(_history, HopSize, _history, 0, FrameSize - HopSize);
        _pending.AsSpan(0, HopSize).CopyTo(_history.AsSpan(FrameSize - HopSize));

        for (var i = 0; i < FrameSize; i++)
        {
            _real[i] = _history[i] * _window[i];
            _imag[i] = 0f;
        }

        Fft(_real, _imag, inverse: false);

        var enabled = settings.DenoiseEnabled && settings.DenoiseStrength > 0.0001f;
        var strength = Math.Clamp(settings.DenoiseStrength, 0f, 1f);
        var reductionDb = Math.Clamp(settings.DenoiseReductionDb, 6f, 40f);
        var floorGain = MathF.Pow(10f, -reductionDb / 20f);
        var smoothing = Math.Clamp(settings.DenoiseSmoothing, 0f, 0.95f);

        // The slider is the assumed minimum noise floor. Convert it from
        // time-domain RMS to the power that a broadband signal produces in
        // one FFT bin. The adaptive minimum-statistics estimate may rise well
        // above this value; the floor only keeps digital silence from being
        // over-subtracted.
        var minimumNoise = MathF.Pow(10f, Math.Clamp(settings.DenoiseFloorDb, -80f, -20f) / 10f)
            * _windowPower;

        var over = 1f + 3f * strength;
        var warmingUp = _warmupHops > 0;

        for (var bin = 0; bin < BinCount; bin++)
        {
            var real = _real[bin];
            var imaginary = _imag[bin];
            var power = real * real + imaginary * imaginary;

            if (warmingUp)
            {
                _gain[bin] = 1f;
                continue;
            }

            _power[bin] = _firstHop
                ? power
                : PowerSmoothing * _power[bin] + (1f - PowerSmoothing) * power;

            if (_power[bin] < _blockMin[bin])
            {
                _blockMin[bin] = _power[bin];
            }

            var minimum = MathF.Min(_power[bin], _blockMin[bin]);
            if (minimum > _ringMin[bin])
            {
                minimum = _ringMin[bin];
            }

            var noise = MathF.Max(minimum * MinimumBias, minimumNoise);

            if (_firstHop)
            {
                _noise[bin] = noise;
            }
            else if (noise < _noise[bin])
            {
                // Adapt downwards quickly, upwards slowly. This keeps a short
                // speech syllable from being learned as background noise.
                _noise[bin] = 0.5f * _noise[bin] + 0.5f * noise;
            }
            else
            {
                _noise[bin] = 0.9f * _noise[bin] + 0.1f * noise;
            }

            var ratio = _noise[bin] / (_power[bin] + 1e-12f);
            _target[bin] = MathF.Sqrt(MathF.Max(0f, 1f - over * ratio));
        }

        if (warmingUp)
        {
            _warmupHops--;
        }
        else
        {
            _firstHop = false;
        }

        // Smooth the gain over neighbouring bins to avoid isolated holes that
        // would otherwise be heard as "musical" chirping.
        for (var bin = 0; bin < BinCount; bin++)
        {
            var left = bin > 0 ? _target[bin - 1] : _target[0];
            var right = bin + 1 < BinCount ? _target[bin + 1] : _target[BinCount - 1];
            var smoothedTarget = 0.25f * left + 0.5f * _target[bin] + 0.25f * right;

            var gain = enabled
                ? smoothing * _gain[bin] + (1f - smoothing) * smoothedTarget
                : 1f;

            _gain[bin] = Math.Clamp(gain, enabled ? floorGain : 1f, 1f);
        }

        for (var bin = 0; bin < BinCount; bin++)
        {
            var gain = _gain[bin];
            _real[bin] *= gain;
            _imag[bin] *= gain;

            if (bin > 0 && bin < FrameSize / 2)
            {
                var mirror = FrameSize - bin;
                _real[mirror] = _real[bin];
                _imag[mirror] = -_imag[bin];
            }
        }

        Fft(_real, _imag, inverse: true);

        for (var i = 0; i < FrameSize; i++)
        {
            _ola[i] += _real[i] * _window[i];
        }

        // A Hann window with 75 % overlap sums to 1.5 in the squared domain.
        for (var i = 0; i < HopSize; i++)
        {
            var value = _ola[i] / 1.5f;
            if (_readyCount < _ready.Length)
            {
                _ready[_readyCount++] = value;
            }
        }

        Array.Copy(_ola, HopSize, _ola, 0, FrameSize - HopSize);
        Array.Clear(_ola, FrameSize - HopSize, HopSize);

        if (!warmingUp)
        {
            AdvanceMinimumStatistics();
        }
    }

    private void AdvanceMinimumStatistics()
    {
        _blockHop++;

        if (_blockHop < BlockHops)
        {
            return;
        }

        _blockHop = 0;

        Array.Copy(
            _blockMin,
            0,
            _blockMinima,
            _blockIndex * BinCount,
            BinCount);

        _blockIndex = (_blockIndex + 1) % BlockCount;

        if (_blocksFilled < BlockCount)
        {
            _blocksFilled++;
        }

        for (var bin = 0; bin < BinCount; bin++)
        {
            var minimum = float.MaxValue;
            for (var block = 0; block < _blocksFilled; block++)
            {
                var candidate = _blockMinima[block * BinCount + bin];
                if (candidate < minimum)
                {
                    minimum = candidate;
                }
            }

            _ringMin[bin] = minimum;
            _blockMin[bin] = _power[bin];
        }
    }

    public void Reset()
    {
        _pendingCount = 0;
        _readyCount = 0;
        _blockHop = 0;
        _blockIndex = 0;
        _blocksFilled = 0;
        _warmupHops = WarmupHops;
        _firstHop = true;
        Array.Clear(_history);
        Array.Clear(_ola);
        Array.Clear(_power);
        Array.Clear(_noise);
        Array.Clear(_target);
        Array.Clear(_real);
        Array.Clear(_imag);
        Array.Fill(_gain, 1f);
        Array.Fill(_blockMin, float.MaxValue);
        Array.Fill(_ringMin, float.MaxValue);
        Array.Fill(_blockMinima, float.MaxValue);
    }

    private static void Fft(float[] real, float[] imaginary, bool inverse)
    {
        var n = real.Length;

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;

            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2 * MathF.PI / length * (inverse ? 1f : -1f);
            var stepReal = MathF.Cos(angle);
            var stepImaginary = MathF.Sin(angle);
            var half = length >> 1;

            for (var start = 0; start < n; start += length)
            {
                var wReal = 1f;
                var wImaginary = 0f;

                for (var k = 0; k < half; k++)
                {
                    var evenIndex = start + k;
                    var oddIndex = evenIndex + half;

                    var oddReal = real[oddIndex] * wReal - imaginary[oddIndex] * wImaginary;
                    var oddImaginary = real[oddIndex] * wImaginary + imaginary[oddIndex] * wReal;

                    real[oddIndex] = real[evenIndex] - oddReal;
                    imaginary[oddIndex] = imaginary[evenIndex] - oddImaginary;
                    real[evenIndex] += oddReal;
                    imaginary[evenIndex] += oddImaginary;

                    var nextReal = wReal * stepReal - wImaginary * stepImaginary;
                    wImaginary = wReal * stepImaginary + wImaginary * stepReal;
                    wReal = nextReal;
                }
            }
        }

        if (inverse)
        {
            var scale = 1f / n;
            for (var i = 0; i < n; i++)
            {
                real[i] *= scale;
                imaginary[i] *= scale;
            }
        }
    }
}

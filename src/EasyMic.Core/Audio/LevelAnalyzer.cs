namespace EasyMic.Audio;

public readonly record struct MeterSnapshot(
    double PeakDb,
    double RmsDb,
    double HoldDb,
    double[] History);

/// <summary>
/// Turns raw PCM chunks into smoothed peak/RMS values in dBFS, plus a short history
/// for the little scrolling graph in the UI.
/// </summary>
public sealed class LevelAnalyzer
{
    public const double FloorDb = -60.0;

    private const double HistoryIntervalSeconds = 0.04;
    private const double HoldSeconds = 1.2;
    private const double HoldFallDbPerSecond = 24.0;

    private readonly object _gate = new();
    private readonly double[] _history = new double[600];

    private int _historyHead;
    private int _historyCount;

    private double _peakDb = FloorDb;
    private double _rmsDb = FloorDb;
    private double _holdDb = FloorDb;
    private double _holdAge;
    private double _historyClock;

    public void Process(ReadOnlySpan<short> samples, double seconds)
    {
        if (samples.Length == 0)
        {
            return;
        }

        double sumOfSquares = 0;
        var peak = 0;

        foreach (var sample in samples)
        {
            var absolute = sample == short.MinValue ? 32768 : Math.Abs((int)sample);
            if (absolute > peak)
            {
                peak = absolute;
            }

            sumOfSquares += (double)sample * sample;
        }

        var peakDb = ToDb(peak / 32768.0);
        var rmsDb = ToDb(Math.Sqrt(sumOfSquares / samples.Length) / 32768.0);

        lock (_gate)
        {
            _peakDb = Smooth(_peakDb, peakDb, seconds, attackSeconds: 0.008, releaseSeconds: 0.22);
            _rmsDb = Smooth(_rmsDb, rmsDb, seconds, attackSeconds: 0.06, releaseSeconds: 0.30);

            if (_peakDb >= _holdDb)
            {
                _holdDb = _peakDb;
                _holdAge = 0;
            }
            else
            {
                _holdAge += seconds;
                if (_holdAge > HoldSeconds)
                {
                    _holdDb = Math.Max(FloorDb, _holdDb - HoldFallDbPerSecond * seconds);
                }
            }

            _historyClock += seconds;
            if (_historyClock >= HistoryIntervalSeconds)
            {
                _historyClock %= HistoryIntervalSeconds;

                _history[_historyHead] = _peakDb;
                _historyHead = (_historyHead + 1) % _history.Length;
                if (_historyCount < _history.Length)
                {
                    _historyCount++;
                }
            }
        }
    }

    public MeterSnapshot Snapshot()
    {
        lock (_gate)
        {
            var history = new double[_historyCount];
            var start = (_historyHead - _historyCount + _history.Length) % _history.Length;

            for (var i = 0; i < _historyCount; i++)
            {
                history[i] = _history[(start + i) % _history.Length];
            }

            return new MeterSnapshot(_peakDb, _rmsDb, _holdDb, history);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _peakDb = FloorDb;
            _rmsDb = FloorDb;
            _holdDb = FloorDb;
            _holdAge = 0;
            _historyClock = 0;
            _historyHead = 0;
            _historyCount = 0;
            Array.Fill(_history, FloorDb);
        }
    }

    public void ResetHold()
    {
        lock (_gate)
        {
            _holdDb = _peakDb;
            _holdAge = 0;
        }
    }

    private static double ToDb(double amplitude)
    {
        if (amplitude <= 0)
        {
            return FloorDb;
        }

        return Math.Max(FloorDb, 20 * Math.Log10(Math.Min(1.0, amplitude)));
    }

    private static double Smooth(
        double current,
        double target,
        double seconds,
        double attackSeconds,
        double releaseSeconds)
    {
        var timeConstant = target > current ? attackSeconds : releaseSeconds;
        var alpha = 1 - Math.Exp(-Math.Max(seconds, 0.0001) / timeConstant);
        return current + (target - current) * alpha;
    }
}

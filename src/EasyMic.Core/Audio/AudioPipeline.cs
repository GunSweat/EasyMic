using System.Diagnostics;

namespace EasyMic.Audio;

/// <summary>
/// Feeds processed audio into the virtual microphone sink through pacat.
/// A small bounded queue decouples capture and playback: if the writer falls
/// behind, old chunks are dropped so latency does not grow without bound.
/// </summary>
public sealed class AudioPipeline : IAsyncDisposable
{
    private const int MaxQueuedChunks = 8; // ~160 ms

    private readonly Func<ProcessingSettings> _settings;
    private readonly AudioProcessor _processor = new();
    private readonly Queue<short[]> _queue = new();
    private readonly object _gate = new();
    private readonly byte[] _writeBuffer = new byte[16 * 1024];

    private Process? _process;
    private Stream? _stdin;
    private Task? _writer;
    private Task<string>? _stderr;
    private CancellationTokenSource? _cancellation;
    private bool _stopping;

    public AudioPipeline(Func<ProcessingSettings> settings)
    {
        _settings = settings;
    }

    public event Action<string>? Failed;

    public void Start(string sinkName)
    {
        if (_process is not null)
        {
            return;
        }

        var startInfo = new ProcessStartInfo("pacat")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--playback");
        startInfo.ArgumentList.Add($"--device={sinkName}");
        startInfo.ArgumentList.Add("--format=s16le");
        startInfo.ArgumentList.Add($"--rate={AudioProcessor.SampleRate}");
        startInfo.ArgumentList.Add("--channels=1");
        startInfo.ArgumentList.Add("--latency-msec=40");
        startInfo.ArgumentList.Add("--process-time-msec=20");
        startInfo.ArgumentList.Add("--client-name=EasyMic");
        startInfo.ArgumentList.Add("--stream-name=EasyMic Virtuelles Mikrofon");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("pacat konnte nicht gestartet werden.");

        _stopping = false;
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stderr = process.StandardError.ReadToEndAsync();
        _cancellation = new CancellationTokenSource();
        _writer = Task.Run(() => WriterLoopAsync(_cancellation.Token));

        _ = WatchProcessAsync(process);
    }

    public void Enqueue(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        var copy = samples.ToArray();

        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            while (_queue.Count >= MaxQueuedChunks)
            {
                _queue.Dequeue();
            }

            _queue.Enqueue(copy);
            Monitor.Pulse(_gate);
        }
    }

    public async Task StopAsync()
    {
        var process = _process;
        var writer = _writer;
        var cancellation = _cancellation;

        if (process is null)
        {
            return;
        }

        lock (_gate)
        {
            _stopping = true;
            _queue.Clear();
            Monitor.PulseAll(_gate);
        }

        try
        {
            cancellation?.Cancel();
        }
        catch
        {
            // Shutdown path.
        }

        try
        {
            _stdin?.Close();
        }
        catch
        {
            // Shutdown path.
        }

        if (writer is not null)
        {
            try
            {
                await writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Timed out or faulted; the process is killed below.
            }
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone.
        }

        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // Shutdown path.
        }

        process.Dispose();
        cancellation?.Dispose();

        _process = null;
        _stdin = null;
        _writer = null;
        _stderr = null;
        _cancellation = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        var stdin = _stdin;

        if (stdin is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                short[]? chunk = null;

                lock (_gate)
                {
                    while (_queue.Count == 0 && !_stopping && !cancellationToken.IsCancellationRequested)
                    {
                        Monitor.Wait(_gate, 100);
                    }

                    if (_stopping || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (_queue.Count > 0)
                    {
                        chunk = _queue.Dequeue();
                    }
                }

                if (chunk is null)
                {
                    continue;
                }

                var processed = _processor.Process(chunk, _settings());
                if (processed.Length == 0)
                {
                    continue;
                }

                var byteCount = processed.Length * sizeof(short);
                Buffer.BlockCopy(processed, 0, _writeBuffer, 0, byteCount);
                await stdin
                    .WriteAsync(_writeBuffer.AsMemory(0, byteCount), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            if (!_stopping)
            {
                Failed?.Invoke($"Audioausgabe-Fehler: {exception.Message}");
            }
        }
    }

    private async Task WatchProcessAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (_stopping)
        {
            return;
        }

        var detail = string.Empty;

        try
        {
            detail = _stderr is null ? string.Empty : (await _stderr.ConfigureAwait(false)).Trim();
        }
        catch
        {
            // Ignore.
        }

        if (detail.Length == 0)
        {
            detail = $"pacat wurde beendet (Exit-Code {process.ExitCode}).";
        }

        Failed?.Invoke($"Audioausgabe: {FirstLine(detail)}");
    }

    private static string FirstLine(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

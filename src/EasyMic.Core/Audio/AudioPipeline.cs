using System.Runtime.InteropServices;

namespace EasyMic.Audio;

/// <summary>
/// Feeds processed audio into the virtual microphone's FIFO. The pipe source
/// module reads the other end and exposes it as <see cref="VirtualMic.DisplayName"/>.
/// A small bounded queue decouples capture and playback: if the writer falls
/// behind, old chunks are dropped so latency does not grow without bound.
/// </summary>
public sealed class AudioPipeline : IAsyncDisposable
{
    private const int MaxQueuedChunks = 8; // ~160 ms

    // Keep the kernel pipe small so a paused source cannot accumulate seconds
    // of stale audio. 4096 bytes are roughly 42 ms of mono 16-bit audio.
    private const int PipeBufferBytes = 4096;
    private const int FSetPipeSize = 1031;
    private const int SigPipe = 13;
    private const nint SigIgn = 1;

    private readonly Func<ProcessingSettings> _settings;
    private readonly AudioProcessor _processor = new();
    private readonly Queue<short[]> _queue = new();
    private readonly object _gate = new();
    private readonly byte[] _writeBuffer = new byte[16 * 1024];

    private static bool _processConfigured;

    private FileStream? _pipe;
    private Task? _writer;
    private CancellationTokenSource? _cancellation;
    private bool _stopping;

    public AudioPipeline(Func<ProcessingSettings> settings)
    {
        _settings = settings;
    }

    public event Action<string>? Failed;

    public void Start(string fifoPath)
    {
        if (_pipe is not null)
        {
            return;
        }

        ConfigureProcess();

        var pipe = new FileStream(
            fifoPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 1,
            FileOptions.None);

        TryShrinkPipe(pipe);

        _stopping = false;
        _pipe = pipe;
        _cancellation = new CancellationTokenSource();
        _writer = Task.Run(() => WriterLoopAsync(pipe, _cancellation.Token));
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
        var writer = _writer;
        _writer = null;

        lock (_gate)
        {
            _stopping = true;
            _queue.Clear();
            Monitor.PulseAll(_gate);
        }

        try
        {
            _cancellation?.Cancel();
        }
        catch
        {
            // Shutdown path.
        }

        // Closing the write end unblocks a pending write when the source is
        // paused (for example while no application is capturing the mic).
        try
        {
            _pipe?.Dispose();
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
                // Timed out or faulted.
            }
        }

        _pipe?.Dispose();
        _pipe = null;

        _cancellation?.Dispose();
        _cancellation = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task WriterLoopAsync(FileStream pipe, CancellationToken cancellationToken)
    {
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

                await pipe
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
            // Normal shutdown while a write was blocked on the FIFO.
        }
        catch (Exception exception)
        {
            if (!_stopping)
            {
                Failed?.Invoke($"Audio output error: {FirstLine(exception.Message)}");
            }
        }
    }

    private static void ConfigureProcess()
    {
        if (_processConfigured)
        {
            return;
        }

        _processConfigured = true;

        try
        {
            // A FIFO write with no reader raises SIGPIPE. Ignore it so a
            // paused/removed pipe source cannot terminate the daemon.
            _ = signal(SigPipe, SigIgn);
        }
        catch
        {
            // Non-fatal; the write path reports the error instead.
        }
    }

    private static void TryShrinkPipe(FileStream pipe)
    {
        try
        {
            var handle = pipe.SafeFileHandle.DangerousGetHandle();
            _ = fcntl(handle.ToInt32(), FSetPipeSize, PipeBufferBytes);
        }
        catch
        {
            // Keep the default pipe size if this is not supported.
        }
    }

    private static string FirstLine(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl(int fd, int command, int argument);

    [DllImport("libc", EntryPoint = "signal")]
    private static extern nint signal(int signalNumber, nint handler);
}

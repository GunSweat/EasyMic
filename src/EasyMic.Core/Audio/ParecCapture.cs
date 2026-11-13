using System.Buffers.Binary;
using System.Diagnostics;

namespace EasyMic.Audio;

/// <summary>
/// Captures mono 16-bit PCM from a PulseAudio/PipeWire source using parec.
/// parec is part of pulseaudio-utils and works with PipeWire's pulse layer.
/// </summary>
public sealed class ParecCapture : IAsyncDisposable
{
    public const int SampleRate = 48_000;

    private const int ChunkSamples = 960; // 20 ms

    private readonly string _device;
    private readonly short[] _samples = new short[ChunkSamples];

    private CancellationTokenSource? _cancellation;
    private Process? _process;
    private Task? _pump;
    private Task<string>? _stderr;

    public ParecCapture(string device)
    {
        _device = device;
    }

    public event Action<short[]>? SamplesReady;
    public event Action<string>? Failed;

    public bool IsRunning => _process is { HasExited: false };

    public void Start()
    {
        if (_process is not null)
        {
            return;
        }

        var startInfo = new ProcessStartInfo("parec")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add($"--device={_device}");
        startInfo.ArgumentList.Add("--format=s16le");
        startInfo.ArgumentList.Add($"--rate={SampleRate}");
        startInfo.ArgumentList.Add("--channels=1");
        startInfo.ArgumentList.Add("--latency-msec=20");
        startInfo.ArgumentList.Add("--raw");
        startInfo.ArgumentList.Add("--client-name=EasyMic");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("parec konnte nicht gestartet werden.");

        var cancellation = new CancellationTokenSource();
        var stderr = process.StandardError.ReadToEndAsync();

        _process = process;
        _cancellation = cancellation;
        _stderr = stderr;
        _pump = Task.Run(() => PumpAsync(process, stderr, cancellation.Token));
    }

    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Start();
    }

    public async Task StopAsync()
    {
        var cancellation = _cancellation;
        var process = _process;
        var pump = _pump;

        _cancellation = null;
        _process = null;
        _pump = null;
        _stderr = null;

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process already gone.
            }
        }

        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch
            {
                // Shutdown path.
            }
        }

        if (process is not null)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                // Shutdown path.
            }

            process.Dispose();
        }

        cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task PumpAsync(Process process, Task<string> stderr, CancellationToken cancellationToken)
    {
        var bytes = new byte[ChunkSamples * 2];
        var filled = 0;

        try
        {
            var stream = process.StandardOutput.BaseStream;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream
                    .ReadAsync(bytes.AsMemory(filled, bytes.Length - filled), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                filled += read;
                if (filled < bytes.Length)
                {
                    continue;
                }

                for (var i = 0; i < ChunkSamples; i++)
                {
                    _samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2));
                }

                SamplesReady?.Invoke(_samples);
                filled = 0;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Failed?.Invoke($"Aufnahme-Fehler: {exception.Message}");
            }

            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var detail = string.Empty;

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Ignore.
        }

        try
        {
            detail = (await stderr.ConfigureAwait(false)).Trim();
        }
        catch
        {
            // Ignore.
        }

        if (detail.Length == 0)
        {
            detail = $"parec wurde beendet (Exit-Code {process.ExitCode}).";
        }

        Failed?.Invoke(detail);
    }
}

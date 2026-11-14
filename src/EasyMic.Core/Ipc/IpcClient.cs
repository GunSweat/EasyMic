using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace EasyMic.Ipc;

/// <summary>
/// Client for <see cref="IpcServer"/>. Requests are serialized through a
/// semaphore; responses are matched in order. Status snapshots received on a
/// subscribed connection are stored in <see cref="LatestStatus"/>.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Queue<TaskCompletionSource<IpcResponse>> _pending = new();
    private readonly object _pendingGate = new();

    private Socket? _socket;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cancellation;
    private Task? _readLoop;

    public StatusSnapshot? LatestStatus { get; private set; }

    public bool IsConnected => _socket is { Connected: true };

    public static async Task<bool> ProbeAsync(string socketPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(socketPath))
        {
            return false;
        }

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task ConnectAsync(string? socketPath = null, CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        var path = socketPath ?? IpcProtocol.DefaultSocketPath;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);

        var stream = new NetworkStream(socket, ownsSocket: false);
        _socket = socket;
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _cancellation = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_cancellation.Token), CancellationToken.None);
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        var writer = _writer ?? throw new InvalidOperationException("Not connected to the daemon.");
        var completion = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingGate)
        {
            _pending.Enqueue(completion);
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var line = JsonSerializer.Serialize(request, IpcProtocol.JsonOptions);
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            FailPending();
            throw;
        }
        finally
        {
            _sendGate.Release();
        }

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task.ConfigureAwait(false);
    }

    public async Task<StatusSnapshot?> SubscribeAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(IpcRequest.Subscribe, cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error ?? "Subscription failed.");
        }

        return response.Status ?? LatestStatus;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cancellation?.Cancel();
        }
        catch
        {
            // Shutdown path.
        }

        FailPending();

        try
        {
            _socket?.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // Already disconnected.
        }

        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // Shutdown path.
        }

        try
        {
            _reader?.Dispose();
        }
        catch
        {
            // Shutdown path.
        }

        try
        {
            _socket?.Dispose();
        }
        catch
        {
            // Shutdown path.
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch
            {
                // Timeout during shutdown.
            }
        }

        _cancellation?.Dispose();
        _sendGate.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reader = _reader;
            if (reader is null)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                IpcMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<IpcMessage>(line, IpcProtocol.JsonOptions);
                }
                catch
                {
                    continue;
                }

                if (message?.Status is not null)
                {
                    LatestStatus = message.Status;
                }

                if (message?.Response is not null)
                {
                    TaskCompletionSource<IpcResponse>? completion;
                    lock (_pendingGate)
                    {
                        _pending.TryDequeue(out completion);
                    }

                    completion?.TrySetResult(message.Response);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch
        {
            // Connection lost.
        }
        finally
        {
            FailPending();
        }
    }

    private void FailPending()
    {
        lock (_pendingGate)
        {
            while (_pending.TryDequeue(out var completion))
            {
                completion.TrySetException(new IOException("Connection to the daemon lost."));
            }
        }
    }
}

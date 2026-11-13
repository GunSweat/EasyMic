using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace EasyMic.Ipc;

/// <summary>
/// Line-delimited JSON server on a Unix domain socket. Requests are handled
/// sequentially per client; subscribed clients additionally receive a status
/// snapshot roughly 30 times per second.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _handler;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();

    private Socket? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptLoop;

    public IpcServer(Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler)
    {
        _handler = handler;
    }

    public string SocketPath { get; private set; } = IpcProtocol.DefaultSocketPath;

    public async Task StartAsync(string? socketPath = null, CancellationToken cancellationToken = default)
    {
        SocketPath = socketPath ?? IpcProtocol.DefaultSocketPath;

        var directory = Path.GetDirectoryName(SocketPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(SocketPath))
        {
            try
            {
                File.Delete(SocketPath);
            }
            catch
            {
                // If deletion fails, binding below reports the real problem.
            }
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(16);

        _listener = listener;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cancellation.Token), CancellationToken.None);

        await Task.CompletedTask;
    }

    public async Task BroadcastAsync(StatusSnapshot status, CancellationToken cancellationToken = default)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        var message = JsonSerializer.Serialize(IpcMessage.FromStatus(status), IpcProtocol.JsonOptions);

        foreach (var client in _clients.Values)
        {
            if (!client.Subscribed)
            {
                continue;
            }

            try
            {
                await client.WriteLineAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The read loop will remove the connection.
            }
        }
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

        try
        {
            _listener?.Close();
        }
        catch
        {
            // Shutdown path.
        }

        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _clients.Clear();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Timeout or cancellation during shutdown.
            }
        }

        try
        {
            if (File.Exists(SocketPath))
            {
                File.Delete(SocketPath);
            }
        }
        catch
        {
            // Best effort.
        }

        _cancellation?.Dispose();
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var id = Guid.NewGuid();
            var client = new ClientConnection(socket);
            _clients[id] = client;
            _ = Task.Run(() => HandleClientAsync(id, client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(Guid id, ClientConnection client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await client.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                IpcRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<IpcRequest>(line, IpcProtocol.JsonOptions);
                }
                catch
                {
                    request = null;
                }

                if (request is null)
                {
                    await client.WriteLineAsync(
                        JsonSerializer.Serialize(
                            IpcMessage.FromResponse(IpcResponse.Failure("Ungültige Anfrage.")),
                            IpcProtocol.JsonOptions),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (request.Command.Equals("subscribe", StringComparison.OrdinalIgnoreCase))
                {
                    client.Subscribed = true;
                }

                IpcResponse response;
                try
                {
                    response = await _handler(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    response = IpcResponse.Failure(exception.Message);
                }

                await client.WriteLineAsync(
                    JsonSerializer.Serialize(IpcMessage.FromResponse(response), IpcProtocol.JsonOptions),
                    cancellationToken).ConfigureAwait(false);

                if (client.Subscribed && response.Status is not null)
                {
                    await client.WriteLineAsync(
                        JsonSerializer.Serialize(IpcMessage.FromStatus(response.Status), IpcProtocol.JsonOptions),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch
        {
            // Disconnected client.
        }
        finally
        {
            _clients.TryRemove(id, out _);
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ClientConnection : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public ClientConnection(Socket socket)
        {
            _socket = socket;
            var stream = new NetworkStream(socket, ownsSocket: false);
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public bool Subscribed { get; set; }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
            => _reader.ReadLineAsync(cancellationToken).AsTask();

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // Already disconnected.
            }

            _socket.Dispose();
            _reader.Dispose();
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writeGate.Dispose();
        }
    }
}

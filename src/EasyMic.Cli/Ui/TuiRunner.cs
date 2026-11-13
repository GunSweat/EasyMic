using EasyMic.Ipc;

namespace EasyMic.Ui;

/// <summary>
/// Main interactive loop: reads terminal events, forwards changes to the
/// daemon and redraws the dashboard at roughly 30 frames per second.
/// </summary>
public sealed class TuiRunner
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(2);

    private readonly AppState _state = new();
    private readonly List<Task> _pending = new();

    private IpcClient? _client;
    private DashboardHits? _hits;
    private DateTime _lastConnectAttempt = DateTime.MinValue;
    private DateTime _lastDragSend = DateTime.MinValue;
    private int? _dragControl;
    private int _dragLeft;
    private int _dragWidth;
    private int _viewport = 18;
    private bool _quit;

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        await DaemonLauncher.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);

        var runner = new TuiRunner();
        return await runner.RunLoopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RunLoopAsync(CancellationToken cancellationToken)
    {
        using var input = new TerminalInput();
        input.Start();

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            while (!_quit && !cancellationToken.IsCancellationRequested)
            {
                foreach (var uiEvent in input.Poll())
                {
                    Handle(uiEvent, cancellationToken);
                }

                _pending.RemoveAll(task => task.IsCompleted);

                if (_client?.LatestStatus is { } status)
                {
                    _state.Apply(status);
                }

                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

                var frame = Dashboard.Build(_state);
                _viewport = frame.ViewportRows;
                _hits = Dashboard.Draw(frame, _state);

                await Task.Delay(FrameInterval, cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            input.Stop();
            Console.Error.WriteLine($"EasyMic: {exception.Message}");
            return 1;
        }
        finally
        {
            input.Stop();
            await DisposeClientAsync().ConfigureAwait(false);
        }
    }

    private void Handle(UiEvent uiEvent, CancellationToken cancellationToken)
    {
        switch (uiEvent.Kind)
        {
            case UiEventKind.WheelUp:
                _state.Scroll -= 3;
                return;

            case UiEventKind.WheelDown:
                _state.Scroll += 3;
                return;

            case UiEventKind.MouseDown:
                HandleMouseDown(uiEvent.X, uiEvent.Y);
                return;

            case UiEventKind.MouseMove:
                if (_dragControl is not null)
                {
                    ApplySliderPosition(uiEvent.X);
                }

                return;

            case UiEventKind.MouseUp:
                if (_dragControl is not null)
                {
                    ApplySliderPosition(uiEvent.X, force: true);
                    _dragControl = null;
                }

                return;
        }

        if (uiEvent.Code == UiKey.Escape)
        {
            _quit = true;
            return;
        }

        if (uiEvent.Code != UiKey.None)
        {
            HandleKey(uiEvent, cancellationToken);
            return;
        }

        var key = uiEvent.Key;

        switch (char.ToLowerInvariant(key))
        {
            case 'q':
                _quit = true;
                break;

            case 'm':
                Send(_state.ToggleMonitor());
                break;

            case ' ':
                Send(_state.ToggleMonitor());
                break;

            case 'r':
                Send(_state.Reset());
                break;
        }
    }

    private void HandleKey(UiEvent uiEvent, CancellationToken cancellationToken)
    {
        switch (uiEvent.Code)
        {
            case UiKey.Up:
                _state.Selected = (_state.Selected - 1 + AppState.Controls.Count) % AppState.Controls.Count;
                break;

            case UiKey.Down:
                _state.Selected = (_state.Selected + 1) % AppState.Controls.Count;
                break;

            case UiKey.Tab:
                _state.Selected = (_state.Selected + 1) % AppState.Controls.Count;
                break;

            case UiKey.Home:
                _state.Selected = 0;
                break;

            case UiKey.End:
                _state.Selected = AppState.Controls.Count - 1;
                break;

            case UiKey.Left:
                Send(_state.Adjust(-1, uiEvent.Shift));
                break;

            case UiKey.Right:
                Send(_state.Adjust(1, uiEvent.Shift));
                break;

            case UiKey.PageUp:
                _state.Scroll -= Math.Max(1, _viewport - 1);
                break;

            case UiKey.PageDown:
                _state.Scroll += Math.Max(1, _viewport - 1);
                break;

            case UiKey.Enter:
                Send(_state.ToggleSelected());
                break;
        }
    }

    private void HandleMouseDown(int x, int y)
    {
        if (_hits is null)
        {
            return;
        }

        _dragControl = null;

        if (_hits.MonitorBadge.Contains(x, y))
        {
            Send(_state.ToggleMonitor());
            return;
        }

        if (_hits.QuitButton.Contains(x, y))
        {
            _quit = true;
            return;
        }

        foreach (var toggle in _hits.Toggles)
        {
            if (toggle.Row == y && x >= toggle.Left && x <= toggle.Right)
            {
                _state.Selected = toggle.ControlIndex;
                Send(_state.ToggleSelected());
                return;
            }
        }

        foreach (var slider in _hits.Sliders)
        {
            if (slider.Row != y ||
                x < slider.Left ||
                x > slider.Left + slider.Width - 1)
            {
                continue;
            }

            _state.Selected = slider.ControlIndex;
            _dragControl = slider.ControlIndex;
            _dragLeft = slider.Left;
            _dragWidth = slider.Width;
            ApplySliderPosition(x, force: true);
            return;
        }

        if (_hits.RowToControl.TryGetValue(y, out var control))
        {
            _state.Selected = control;
        }
    }

    private void ApplySliderPosition(int x, bool force = false)
    {
        if (_dragControl is not { } index)
        {
            return;
        }

        var parameter = AppState.Controls[index].Parameter;
        if (parameter is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && now - _lastDragSend < TimeSpan.FromMilliseconds(45))
        {
            return;
        }

        _lastDragSend = now;

        var fraction = _dragWidth > 1
            ? (x - _dragLeft) / (float)(_dragWidth - 1)
            : 0f;

        fraction = Math.Clamp(fraction, 0f, 1f);

        var value = parameter.Minimum + fraction * (parameter.Maximum - parameter.Minimum);
        Send(_state.SetValue(index, value));
    }

    private void Send(IpcRequest? request)
    {
        var client = _client;

        if (request is null || client is null || !client.IsConnected)
        {
            return;
        }

        _pending.Add(SendCoreAsync(client, request));
    }

    private async Task SendCoreAsync(IpcClient client, IpcRequest request)
    {
        try
        {
            var response = await client.SendAsync(request).ConfigureAwait(false);

            if (!response.Ok)
            {
                _state.Notice = response.Error;
                _state.NoticeIsError = true;
                return;
            }

            _state.Notice = null;

            if (response.Status is not null)
            {
                _state.Apply(response.Status);
            }
        }
        catch (Exception exception)
        {
            _state.Notice = $"Daemon: {exception.Message}";
            _state.NoticeIsError = true;
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true })
        {
            return;
        }

        if (DateTime.UtcNow - _lastConnectAttempt < ReconnectInterval)
        {
            return;
        }

        _lastConnectAttempt = DateTime.UtcNow;
        await DisposeClientAsync().ConfigureAwait(false);

        var client = new IpcClient();

        try
        {
            await client
                .ConnectAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var status = await client.SubscribeAsync(cancellationToken).ConfigureAwait(false);
            if (status is not null)
            {
                _state.Apply(status);
            }

            _client = client;
            _state.Notice = null;
        }
        catch (Exception exception)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            _state.Notice = $"Daemon nicht erreichbar: {exception.Message}";
            _state.NoticeIsError = true;
        }
    }

    private async Task DisposeClientAsync()
    {
        var client = _client;
        _client = null;

        if (client is null)
        {
            return;
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Shutdown path.
        }
    }
}

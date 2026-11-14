using System.Diagnostics;
using System.Globalization;
using EasyMic.Audio;
using EasyMic.Ipc;
using EasyMic.Settings;

namespace EasyMic.Daemon;

/// <summary>
/// Owns the audio devices, the DSP chain, the virtual microphone and the IPC
/// server. Exactly one instance runs per user session.
/// </summary>
public sealed class DaemonHost : IAsyncDisposable
{
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(33);

    private readonly object _gate = new();
    private readonly SettingsStore _store = new();
    private readonly LevelAnalyzer _analyzer = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AppSettings _settings = new();
    private ProcessingSettings _processing = ProcessingSettings.Default;

    private AudioDevice? _source;
    private AudioDevice? _sink;

    private VirtualMic? _virtualMic;
    private AudioPipeline? _pipeline;
    private ParecCapture? _capture;
    private LoopbackMonitor? _monitor;
    private IpcServer? _server;
    private FileStream? _lock;

    private CancellationTokenSource? _cancellation;
    private Task? _statusLoop;
    private Task? _saveLoop;

    private bool _monitorEnabled;
    private string? _notice;
    private bool _noticeIsError;
    private bool _dirty;
    private DateTime _dirtyAt;
    private int _captureGeneration;
    private int _restartScheduled;
    private int _sourceRetryScheduled;

    public Task Shutdown => _shutdown.Task;

    public event Action<string>? Log;

    public async Task<string?> StartAsync(CancellationToken cancellationToken = default)
    {
        var lockError = AcquireLock();
        if (lockError is not null)
        {
            return lockError;
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _settings = await _store.LoadAsync(_cancellation.Token).ConfigureAwait(false);
        _processing = _settings.Processing;

        await ResolveDevicesAsync(updateSettings: !string.IsNullOrWhiteSpace(_settings.SourceDevice)).ConfigureAwait(false);

        _virtualMic = new VirtualMic();
        var virtualError = await _virtualMic.StartAsync().ConfigureAwait(false);
        if (virtualError is not null)
        {
            SetNotice(virtualError, isError: true);
        }

        if (_virtualMic.IsAvailable)
        {
            _pipeline = new AudioPipeline(() => Volatile.Read(ref _processing));
            _pipeline.Failed += message => SetNotice(message, isError: true);

            try
            {
                _pipeline.Start(VirtualMic.PipePath);
            }
            catch (Exception exception)
            {
                SetNotice($"Audioausgabe konnte nicht gestartet werden: {exception.Message}", isError: true);
            }
        }

        RecreateMonitor();

        await StartCaptureAsync().ConfigureAwait(false);
        await StartServerAsync().ConfigureAwait(false);

        _statusLoop = Task.Run(() => StatusLoopAsync(_cancellation.Token), CancellationToken.None);
        _saveLoop = Task.Run(() => SaveLoopAsync(_cancellation.Token), CancellationToken.None);

        return null;
    }

    public void RequestShutdown()
    {
        if (_shutdown.Task.IsCompleted)
        {
            return;
        }

        try
        {
            _cancellation?.Cancel();
        }
        catch
        {
            // Shutdown path.
        }

        _shutdown.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        RequestShutdown();

        if (_statusLoop is not null)
        {
            try
            {
                await _statusLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Shutdown timeout.
            }
        }

        if (_saveLoop is not null)
        {
            try
            {
                await _saveLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Shutdown timeout.
            }
        }

        try
        {
            await SaveAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort.
        }

        if (_monitor is not null)
        {
            await _monitor.DisposeAsync().ConfigureAwait(false);
            _monitor = null;
        }

        if (_capture is not null)
        {
            await _capture.DisposeAsync().ConfigureAwait(false);
            _capture = null;
        }

        if (_pipeline is not null)
        {
            await _pipeline.DisposeAsync().ConfigureAwait(false);
            _pipeline = null;
        }

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }

        if (_virtualMic is not null)
        {
            await _virtualMic.DisposeAsync().ConfigureAwait(false);
            _virtualMic = null;
        }

        _cancellation?.Dispose();

        try
        {
            _lock?.Dispose();
            _lock = null;
            if (File.Exists(EasyMicPaths.DaemonLockPath))
            {
                File.Delete(EasyMicPaths.DaemonLockPath);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private string? AcquireLock()
    {
        var path = EasyMicPaths.DaemonLockPath;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            var bytes = System.Text.Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n");
            stream.Write(bytes);
            stream.Flush();
            _lock = stream;
            return null;
        }
        catch (IOException)
        {
            return "Der EasyMic-Daemon läuft bereits.";
        }
        catch (UnauthorizedAccessException exception)
        {
            return $"Sperrdatei konnte nicht geöffnet werden: {exception.Message}";
        }
    }

    private async Task ResolveDevicesAsync(bool updateSettings)
    {
        try
        {
            var sources = await DeviceCatalog.GetSourcesAsync().ConfigureAwait(false);
            var sinkDevices = await DeviceCatalog.GetSinksAsync().ConfigureAwait(false);

            _source = DeviceCatalog.Resolve(sources, _settings.SourceDevice);
            if (_source is null)
            {
                var fallback = await DeviceCatalog.GetDefaultSourceAsync().ConfigureAwait(false);
                _source = DeviceCatalog.Resolve(sources, fallback)
                    ?? sources.FirstOrDefault(device => !device.IsMonitor && !device.Name.StartsWith("easymic_", StringComparison.Ordinal))
                    ?? sources.FirstOrDefault(device => !device.IsMonitor);
            }

            _sink = DeviceCatalog.Resolve(sinkDevices, _settings.SinkDevice);
            if (_sink is null)
            {
                var fallback = await DeviceCatalog.GetDefaultSinkAsync().ConfigureAwait(false);
                _sink = DeviceCatalog.Resolve(sinkDevices, fallback)
                    ?? sinkDevices.FirstOrDefault(device => !device.Name.StartsWith("easymic_", StringComparison.Ordinal))
                    ?? sinkDevices.FirstOrDefault();
            }

            if (updateSettings)
            {
                lock (_gate)
                {
                    // Only refresh devices the user pinned explicitly. A device
                    // that was never chosen stays on the system default.
                    var sourcePinned = !string.IsNullOrWhiteSpace(_settings.SourceDevice);
                    var sinkPinned = !string.IsNullOrWhiteSpace(_settings.SinkDevice);

                    _settings = (_settings with
                    {
                        SourceDevice = sourcePinned
                            ? _source?.Name ?? _settings.SourceDevice
                            : _settings.SourceDevice,
                        SinkDevice = sinkPinned
                            ? _sink?.Name ?? _settings.SinkDevice
                            : _settings.SinkDevice,
                    }).Clamp();
                }
            }
        }
        catch (Exception exception)
        {
            SetNotice($"Audio-Geräte konnten nicht ermittelt werden: {exception.Message}", isError: true);
        }
    }

    private async Task StartCaptureAsync()
    {
        if (_source is null)
        {
            SetNotice("Kein Mikrofon gefunden – suche weiter ...", isError: true);
            ScheduleSourceRetry();
            return;
        }

        var generation = Interlocked.Increment(ref _captureGeneration);

        if (_capture is not null)
        {
            await _capture.DisposeAsync().ConfigureAwait(false);
            _capture = null;
        }

        var capture = new ParecCapture(_source.Name);
        capture.SamplesReady += OnSamples;
        capture.Failed += message => OnCaptureFailed(generation, message);
        _capture = capture;

        try
        {
            capture.Start();
            Log?.Invoke($"Aufnahme gestartet: {_source.DisplayName}");
        }
        catch (Exception exception)
        {
            SetNotice($"Aufnahme konnte nicht gestartet werden: {exception.Message}", isError: true);
        }
    }

    private void OnSamples(short[] samples)
    {
        var seconds = samples.Length / (double)ParecCapture.SampleRate;
        _analyzer.Process(samples, seconds);
        _pipeline?.Enqueue(samples);
    }

    private void OnCaptureFailed(int generation, string message)
    {
        if (_cancellation?.IsCancellationRequested == true)
        {
            return;
        }

        SetNotice($"Aufnahme: {message}", isError: true);

        if (Interlocked.CompareExchange(ref _restartScheduled, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000, _cancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
                    if (generation == Volatile.Read(ref _captureGeneration) &&
                        _cancellation?.IsCancellationRequested != true)
                    {
                        await StartCaptureAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutdown.
                }
                finally
                {
                    Interlocked.Exchange(ref _restartScheduled, 0);
                }
            });
        }
    }

    private void ScheduleSourceRetry()
    {
        if (Interlocked.CompareExchange(ref _sourceRetryScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var attempt = 0;

                while (_cancellation?.IsCancellationRequested != true)
                {
                    // Poll quickly right after login, then back off so an
                    // unplugged microphone does not cause constant process churn.
                    var delay = attempt++ < 30 ? 2000 : 10000;
                    await Task.Delay(delay, _cancellation?.Token ?? CancellationToken.None)
                        .ConfigureAwait(false);

                    var oldSink = _sink?.Name;
                    await ResolveDevicesAsync(updateSettings: false).ConfigureAwait(false);

                    // If the user pinned a source, wait for exactly that device
                    // instead of silently capturing a different microphone.
                    if (!string.IsNullOrWhiteSpace(_settings.SourceDevice) &&
                        !string.Equals(_source?.Name, _settings.SourceDevice, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (_source is null)
                    {
                        continue;
                    }

                    if (!string.Equals(oldSink, _sink?.Name, StringComparison.Ordinal))
                    {
                        RecreateMonitor();
                    }

                    await StartCaptureAsync().ConfigureAwait(false);

                    if (_capture is { IsRunning: true })
                    {
                        SetNotice(null, isError: false);
                    }

                    break;
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
            finally
            {
                Interlocked.Exchange(ref _sourceRetryScheduled, 0);
            }
        });
    }

    private void RecreateMonitor()
    {
        var old = _monitor;
        _monitor = _sink is null
            ? null
            : new LoopbackMonitor(VirtualMic.SourceName, _sink.Name, _settings.LatencyMs);

        if (old is not null)
        {
            _ = old.DisposeAsync();
        }
    }

    private async Task<string?> SetMonitorAsync(bool enabled)
    {
        if (!enabled)
        {
            if (_monitor is not null)
            {
                var error = await _monitor.DisableAsync().ConfigureAwait(false);
                if (error is not null)
                {
                    SetNotice(error, isError: true);
                    return error;
                }
            }

            _monitorEnabled = false;
            SetNotice(null, isError: false);
            return null;
        }

        if (_virtualMic is not { IsAvailable: true })
        {
            const string error = "Monitoring ist nur mit verfügbarem virtuellem Mikrofon möglich.";
            SetNotice(error, isError: true);
            return error;
        }

        if (_sink is null)
        {
            const string error = "Kein Ausgabegerät gefunden – Monitoring ist nicht verfügbar.";
            SetNotice(error, isError: true);
            return error;
        }

        if (_monitor is null)
        {
            RecreateMonitor();
        }

        var monitorError = await _monitor!.EnableAsync().ConfigureAwait(false);
        _monitorEnabled = monitorError is null;
        SetNotice(monitorError, isError: monitorError is not null);
        return monitorError;
    }

    private async Task<string?> SetSourceAsync(string query)
    {
        try
        {
            var sources = await DeviceCatalog.GetSourcesAsync().ConfigureAwait(false);
            var device = DeviceCatalog.Resolve(sources, query);
            if (device is null)
            {
                return $"Keine Quelle für \"{query}\" gefunden.";
            }

            if (device.Name.Equals(VirtualMic.SourceName, StringComparison.OrdinalIgnoreCase))
            {
                return "Die virtuelle Quelle kann nicht als Eingang verwendet werden.";
            }

            _source = device;
            lock (_gate)
            {
                _settings = (_settings with { SourceDevice = device.Name }).Clamp();
            }

            MarkDirty();
            await StartCaptureAsync().ConfigureAwait(false);
            SetNotice(null, isError: false);
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    private async Task<string?> SetSinkAsync(string query)
    {
        try
        {
            var sinks = await DeviceCatalog.GetSinksAsync().ConfigureAwait(false);
            var device = DeviceCatalog.Resolve(sinks, query);
            if (device is null)
            {
                return $"Keine Ausgabe für \"{query}\" gefunden.";
            }

            if (device.Name.Equals(VirtualMic.SinkName, StringComparison.OrdinalIgnoreCase))
            {
                return "Die virtuelle Senke kann nicht als Ausgabe verwendet werden.";
            }

            var wasEnabled = _monitorEnabled;
            if (wasEnabled)
            {
                await SetMonitorAsync(false).ConfigureAwait(false);
            }

            _sink = device;
            lock (_gate)
            {
                _settings = (_settings with { SinkDevice = device.Name }).Clamp();
            }

            RecreateMonitor();
            MarkDirty();

            if (wasEnabled)
            {
                return await SetMonitorAsync(true).ConfigureAwait(false);
            }

            SetNotice(null, isError: false);
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    private async Task<string?> SetLatencyAsync(int latencyMs)
    {
        var wasEnabled = _monitorEnabled;
        if (wasEnabled)
        {
            await SetMonitorAsync(false).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _settings = (_settings with { LatencyMs = Math.Clamp(latencyMs, 5, 500) }).Clamp();
        }

        MarkDirty();
        RecreateMonitor();

        if (wasEnabled)
        {
            return await SetMonitorAsync(true).ConfigureAwait(false);
        }

        return null;
    }

    private async Task StartServerAsync()
    {
        _server = new IpcServer(HandleRequestAsync);
        try
        {
            await _server.StartAsync(cancellationToken: _cancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
            Log?.Invoke($"IPC-Socket: {_server.SocketPath}");
        }
        catch (Exception exception)
        {
            Log?.Invoke($"IPC-Server konnte nicht gestartet werden: {exception.Message}");
            SetNotice($"IPC-Server konnte nicht gestartet werden: {exception.Message}", isError: true);
        }
    }

    private async Task<IpcResponse> HandleRequestAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var command = request.Command.ToLowerInvariant();

        switch (command)
        {
            case "status":
            case "subscribe":
                return new IpcResponse
                {
                    Ok = true,
                    Status = BuildStatus(),
                    Settings = SnapshotSettings(),
                };

            case "monitor":
                return await HandleMonitorAsync(request.Value).ConfigureAwait(false);

            case "set":
                return HandleSet(request.Name, request.Value);

            case "toggle":
                return HandleToggle(request.Name);

            case "get":
                return HandleGet(request.Name);

            case "devices":
                return await HandleDevicesAsync(request.Name).ConfigureAwait(false);

            case "source":
                if (string.IsNullOrWhiteSpace(request.Value))
                {
                    return IpcResponse.Failure("source erwartet einen Gerätenamen.");
                }

                var sourceError = await SetSourceAsync(request.Value).ConfigureAwait(false);
                return sourceError is null
                    ? new IpcResponse { Ok = true, Message = $"Quelle: {_source?.DisplayName}", Status = BuildStatus(), Settings = SnapshotSettings() }
                    : IpcResponse.Failure(sourceError);

            case "sink":
                if (string.IsNullOrWhiteSpace(request.Value))
                {
                    return IpcResponse.Failure("sink erwartet einen Gerätenamen.");
                }

                var sinkError = await SetSinkAsync(request.Value).ConfigureAwait(false);
                return sinkError is null
                    ? new IpcResponse { Ok = true, Message = $"Ausgabe: {_sink?.DisplayName}", Status = BuildStatus(), Settings = SnapshotSettings() }
                    : IpcResponse.Failure(sinkError);

            case "latency":
                if (!int.TryParse(request.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var latency))
                {
                    return IpcResponse.Failure("latency erwartet eine Zahl in Millisekunden.");
                }

                var latencyError = await SetLatencyAsync(latency).ConfigureAwait(false);
                return latencyError is null
                    ? new IpcResponse { Ok = true, Message = $"Latenz: {_settings.LatencyMs} ms", Status = BuildStatus(), Settings = SnapshotSettings() }
                    : IpcResponse.Failure(latencyError);

            case "reset":
                UpdateSettings(settings => (settings with { Processing = ProcessingSettings.Default }).Clamp());
                return new IpcResponse { Ok = true, Message = "Einstellungen zurückgesetzt.", Status = BuildStatus(), Settings = SnapshotSettings() };

            case "reload":
                await ReloadAsync().ConfigureAwait(false);
                return new IpcResponse { Ok = true, Message = "Einstellungen neu geladen.", Status = BuildStatus(), Settings = SnapshotSettings() };

            case "shutdown":
            case "stop":
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    RequestShutdown();
                });
                return new IpcResponse { Ok = true, Message = "Daemon wird beendet." };

            default:
                return IpcResponse.Failure($"Unbekannter Befehl: {request.Command}");
        }
    }

    private async Task<IpcResponse> HandleMonitorAsync(string? value)
    {
        var target = _monitorEnabled;
        var normalized = value?.Trim().ToLowerInvariant();

        target = normalized switch
        {
            null or "" or "toggle" => !_monitorEnabled,
            "on" or "an" or "1" or "true" or "yes" => true,
            "off" or "aus" or "0" or "false" or "no" => false,
            _ => target,
        };

        var error = await SetMonitorAsync(target).ConfigureAwait(false);
        return error is null
            ? new IpcResponse
            {
                Ok = true,
                Message = target ? "Monitoring an." : "Monitoring aus.",
                Status = BuildStatus(),
                Settings = SnapshotSettings(),
            }
            : IpcResponse.Failure(error);
    }

    private IpcResponse HandleSet(string? name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
        {
            return IpcResponse.Failure("set erwartet Name und Wert.");
        }

        if (TryParseFloat(value, out var numeric))
        {
            var parameter = ParameterCatalog.Find(name);
            if (parameter is not null)
            {
                var applied = 0f;
                UpdateSettings(settings =>
                {
                    var next = settings with { Processing = parameter.Write(settings.Processing, numeric) };
                    applied = parameter.Read(next.Processing);
                    return next;
                });

                return new IpcResponse
                {
                    Ok = true,
                    Message = $"{parameter.Label}: {parameter.Format(applied)}",
                    Status = BuildStatus(),
                    Settings = SnapshotSettings(),
                };
            }
        }

        var toggle = ParameterCatalog.FindToggle(name);
        if (toggle is not null && TryParseBool(value, out var boolean))
        {
            UpdateSettings(settings => (settings with
            {
                Processing = toggle.Write(settings.Processing, boolean),
            }).Clamp());

            return new IpcResponse
            {
                Ok = true,
                Message = $"{toggle.Label}: {(boolean ? "an" : "aus")}",
                Status = BuildStatus(),
                Settings = SnapshotSettings(),
            };
        }

        return IpcResponse.Failure($"Unbekannter Parameter: {name}");
    }

    private IpcResponse HandleToggle(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return IpcResponse.Failure("toggle erwartet einen Namen.");
        }

        var toggle = ParameterCatalog.FindToggle(name);
        if (toggle is null)
        {
            return IpcResponse.Failure($"Unbekannter Schalter: {name}");
        }

        var state = false;
        UpdateSettings(settings =>
        {
            state = !toggle.Read(settings.Processing);
            return (settings with
            {
                Processing = toggle.Write(settings.Processing, state),
            }).Clamp();
        });

        return new IpcResponse
        {
            Ok = true,
            Message = $"{toggle.Label}: {(state ? "an" : "aus")}",
            Status = BuildStatus(),
            Settings = SnapshotSettings(),
        };
    }

    private IpcResponse HandleGet(string? name)
    {
        var settings = SnapshotSettings();

        if (string.IsNullOrWhiteSpace(name))
        {
            return new IpcResponse { Ok = true, Settings = settings, Status = BuildStatus() };
        }

        var parameter = ParameterCatalog.Find(name);
        if (parameter is not null)
        {
            var value = parameter.Read(settings.Processing);
            return new IpcResponse
            {
                Ok = true,
                Message = $"{parameter.Label}: {parameter.Format(value)}",
                Settings = settings,
                Status = BuildStatus(),
            };
        }

        var toggle = ParameterCatalog.FindToggle(name);
        if (toggle is not null)
        {
            return new IpcResponse
            {
                Ok = true,
                Message = $"{toggle.Label}: {(toggle.Read(settings.Processing) ? "an" : "aus")}",
                Settings = settings,
                Status = BuildStatus(),
            };
        }

        return IpcResponse.Failure($"Unbekannter Parameter: {name}");
    }

    private async Task<IpcResponse> HandleDevicesAsync(string? kind)
    {
        try
        {
            var normalized = kind?.Trim().ToLowerInvariant();
            var devices = new List<DeviceInfo>();

            if (normalized is not ("sink" or "sinks" or "ausgabe"))
            {
                devices.AddRange((await DeviceCatalog.GetSourcesAsync().ConfigureAwait(false))
                    .Where(device => !device.Name.Equals(VirtualMic.SourceName, StringComparison.OrdinalIgnoreCase))
                    .Select(DeviceInfo.From));
            }

            if (normalized is "sink" or "sinks" or "ausgabe" or "all" or "alle")
            {
                devices.AddRange((await DeviceCatalog.GetSinksAsync().ConfigureAwait(false))
                    .Where(device => !device.Name.Equals(VirtualMic.SinkName, StringComparison.OrdinalIgnoreCase))
                    .Select(DeviceInfo.From));
            }

            return new IpcResponse { Ok = true, Devices = devices, Status = BuildStatus() };
        }
        catch (Exception exception)
        {
            return IpcResponse.Failure(exception.Message);
        }
    }

    private async Task ReloadAsync()
    {
        var loaded = await _store.LoadAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _settings = loaded;
            _processing = loaded.Processing;
        }

        if (!string.Equals(_settings.SourceDevice, _source?.Name, StringComparison.Ordinal))
        {
            await ResolveDevicesAsync(updateSettings: true).ConfigureAwait(false);
            await StartCaptureAsync().ConfigureAwait(false);
        }
    }

    private void UpdateSettings(Func<AppSettings, AppSettings> transform)
    {
        lock (_gate)
        {
            _settings = transform(_settings).Clamp();
            _processing = _settings.Processing;
        }

        MarkDirty();
    }

    private AppSettings SnapshotSettings()
    {
        lock (_gate)
        {
            return _settings;
        }
    }

    private StatusSnapshot BuildStatus()
    {
        var meter = _analyzer.Snapshot();
        if (meter.History.Length > 160)
        {
            meter = meter with { History = meter.History[^160..] };
        }

        return new StatusSnapshot
        {
            Running = _capture is { IsRunning: true },
            MonitorEnabled = _monitorEnabled,
            VirtualMicAvailable = _virtualMic is { IsAvailable: true },
            LimiterActive = Volatile.Read(ref _processing).LimiterEnabled,
            SourceDevice = _source?.DisplayName,
            SinkDevice = _sink?.DisplayName,
            VirtualDevice = _virtualMic is { IsAvailable: true } ? VirtualMic.DisplayName : null,
            Notice = _notice,
            NoticeIsError = _noticeIsError,
            LatencyMs = _settings.LatencyMs,
            Processing = Volatile.Read(ref _processing),
            Meter = meter,
        };
    }

    private void SetNotice(string? message, bool isError)
    {
        lock (_gate)
        {
            _notice = message;
            _noticeIsError = isError;
        }
    }

    private void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
            _dirtyAt = DateTime.UtcNow;
        }
    }

    private async Task StatusLoopAsync(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(StatusInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var server = _server;
                if (server is null)
                {
                    continue;
                }

                try
                {
                    await server.BroadcastAsync(BuildStatus(), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // A broken client is handled by the server.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async Task SaveLoopAsync(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(150));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                bool due;
                AppSettings settings;

                lock (_gate)
                {
                    due = _dirty && DateTime.UtcNow - _dirtyAt >= TimeSpan.FromMilliseconds(400);
                    settings = _settings;
                    if (due)
                    {
                        _dirty = false;
                    }
                }

                if (!due)
                {
                    continue;
                }

                try
                {
                    await _store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    MarkDirty();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async Task SaveAsync()
    {
        AppSettings settings;
        lock (_gate)
        {
            settings = _settings;
            _dirty = false;
        }

        await _store.SaveAsync(settings).ConfigureAwait(false);
    }

    private static bool TryParseFloat(string value, out float result)
        => float.TryParse(
            value.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result);

    private static bool TryParseBool(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "on":
            case "an":
            case "true":
            case "1":
            case "yes":
            case "ja":
                result = true;
                return true;

            case "off":
            case "aus":
            case "false":
            case "0":
            case "no":
            case "nein":
                result = false;
                return true;

            default:
                result = false;
                return false;
        }
    }
}

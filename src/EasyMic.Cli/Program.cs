using System.Reflection;
using System.Globalization;
using System.Text.Json;
using EasyMic;
using EasyMic.Ipc;
using EasyMic.Settings;
using EasyMic.Ui;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var command = args.Length == 0 ? "tui" : args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();
var json = rest.Contains("--json", StringComparer.Ordinal);
var positional = rest.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();

try
{
    switch (command)
    {
        case "tui":
        case "ui":
            return await TuiRunner.RunAsync();

        case "status":
            return await RunClientAsync(async client =>
            {
                var response = await client.SendAsync(IpcRequest.Status);
                if (!response.Ok)
                {
                    return Fail(response.Error);
                }

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        new { status = response.Status, settings = response.Settings },
                        IpcProtocol.JsonOptions));
                    return 0;
                }

                PrintStatus(response.Status);
                return 0;
            });

        case "monitor":
            return await RunClientAsync(async client =>
            {
                var mode = positional.Length > 0 ? positional[0] : "toggle";
                var response = await client.SendAsync(new IpcRequest("monitor", "monitor", mode));
                if (!response.Ok)
                {
                    return Fail(response.Error);
                }

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(response.Status, IpcProtocol.JsonOptions));
                }
                else
                {
                    Console.WriteLine(response.Message ?? "Monitoring toggled.");
                }

                return 0;
            });

        case "set":
            if (positional.Length < 2)
            {
                return Fail("Usage: easymic set <parameter> <value>");
            }

            return await RunClientAsync(async client =>
            {
                var name = positional[0];
                var value = NormalizeValue(name, positional[1]);
                if (value is null)
                {
                    return Fail($"Invalid value for {name}: {positional[1]}");
                }

                var response = await client.SendAsync(new IpcRequest("set", name, value));
                if (!response.Ok)
                {
                    return Fail(response.Error);
                }

                Console.WriteLine(response.Message ?? "Set.");
                return 0;
            });

        case "get":
            return await RunClientAsync(async client =>
            {
                var name = positional.Length > 0 ? positional[0] : null;
                var response = await client.SendAsync(new IpcRequest("get", name));
                if (!response.Ok)
                {
                    return Fail(response.Error);
                }

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        new { message = response.Message, settings = response.Settings },
                        IpcProtocol.JsonOptions));
                    return 0;
                }

                if (!string.IsNullOrWhiteSpace(response.Message))
                {
                    Console.WriteLine(response.Message);
                    return 0;
                }

                PrintSettings(response.Settings);
                return 0;
            });

        case "devices":
            return await RunClientAsync(async client =>
            {
                var kind = positional.Length > 0 ? positional[0] : null;

                if (kind is not null)
                {
                    return await PrintDevicesAsync(client, kind, json);
                }

                Console.WriteLine("Sources:");
                await PrintDevicesAsync(client, "source", json);
                Console.WriteLine();
                Console.WriteLine("Sinks:");
                return await PrintDevicesAsync(client, "sink", json);
            });

        case "source":
            if (positional.Length == 0)
            {
                return Fail("Usage: easymic source <name>");
            }

            return await RunClientAsync(async client =>
            {
                var response = await client.SendAsync(new IpcRequest("source", "source", string.Join(' ', positional)));
                if (!response.Ok)
                {
                    return Fail(response.Error);
                }

                Console.WriteLine(response.Message ?? "Source set.");
                return 0;
            });

        case "sink":
            if (positional.Length == 0)
            {
                return Fail("Usage: easymic sink <name>");
            }

            return await RunClientAsync(async client =>
            {
                var response = await client.SendAsync(new IpcRequest("sink", "sink", string.Join(' ', positional)));
                if (!response.Ok)
                {
                    return Fail(response.Error);
                }

                Console.WriteLine(response.Message ?? "Sink set.");
                return 0;
            });

        case "latency":
            if (positional.Length == 0)
            {
                return Fail("Usage: easymic latency <milliseconds>");
            }

            return await RunClientAsync(async client =>
            {
                var response = await client.SendAsync(new IpcRequest("latency", "latency", positional[0]));
                if (!response.Ok)
                {
                    return Fail(response.Error);
                }

                Console.WriteLine(response.Message ?? "Latency set.");
                return 0;
            });

        case "reset":
            return await RunClientAsync(async client =>
            {
                var response = await client.SendAsync(new IpcRequest("reset"));
                if (!response.Ok)
                {
                    return Fail(response.Error);
                }

                Console.WriteLine(response.Message ?? "Settings reset.");
                return 0;
            });

        case "daemon":
            return await RunDaemonAsync(positional.Length > 0 ? positional[0] : "status");

        case "notify":
            return await Notifier.ShowAsync();

        case "version":
        case "--version":
            var version = System.Reflection.Assembly.GetEntryAssembly()?
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "1.0.0";
            var build = version.IndexOf('+');
            if (build >= 0)
            {
                version = version[..build];
            }

            Console.WriteLine($"easymic {version}");
            return 0;

        case "help":
        case "--help":
        case "-h":
            PrintHelp();
            return 0;

        default:
            return Fail($"Unknown command: {command}. Run \"easymic help\" for a list of commands.");
    }
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception exception)
{
    return Fail(exception.Message);
}

static int Fail(string? message)
{
    Console.Error.WriteLine($"EasyMic: {message}");
    return 1;
}

static async Task<int> RunClientAsync(Func<IpcClient, Task<int>> action, bool ensureDaemon = true)
{
    if (ensureDaemon)
    {
        await DaemonLauncher.EnsureRunningAsync();
    }

    await using var client = new IpcClient();
    await client.ConnectAsync();
    return await action(client);
}

static async Task<int> RunDaemonAsync(string action)
{
    switch (action.ToLowerInvariant())
    {
        case "start":
            await DaemonLauncher.EnsureRunningAsync();
            Console.WriteLine("EasyMic daemon is running.");
            return 0;

        case "stop":
            await DaemonLauncher.StopAsync();
            Console.WriteLine("EasyMic daemon stopped.");
            return 0;

        case "restart":
            await DaemonLauncher.StopAsync();
            await DaemonLauncher.EnsureRunningAsync();
            Console.WriteLine("EasyMic daemon restarted.");
            return 0;

        case "status":
            Console.WriteLine($"EasyMic daemon: {await DaemonLauncher.GetStateAsync()}");
            return 0;

        default:
            return Fail("Usage: easymic daemon <start|stop|restart|status>");
    }
}

static async Task<int> PrintDevicesAsync(IpcClient client, string kind, bool json)
{
    var response = await client.SendAsync(new IpcRequest("devices", kind));
    if (!response.Ok)
    {
        return Fail(response.Error);
    }

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(response.Devices, IpcProtocol.JsonOptions));
        return 0;
    }

    if (response.Devices is null || response.Devices.Count == 0)
    {
        Console.WriteLine("  (no devices found)");
        return 0;
    }

    foreach (var device in response.Devices)
    {
        Console.WriteLine($"  {device.DisplayName}");
        if (!string.Equals(device.DisplayName, device.Name, StringComparison.Ordinal))
        {
            Console.WriteLine($"    {device.Name}");
        }
    }

    return 0;
}

static void PrintStatus(StatusSnapshot? status)
{
    if (status is null)
    {
        Console.WriteLine("No status available.");
        return;
    }

    Console.WriteLine("EasyMic");
    Console.WriteLine($"  Capture:     {(status.Running ? "running" : "stopped")}");
    Console.WriteLine($"  Input:       {status.SourceDevice ?? "–"}");
    Console.WriteLine($"  Output:      {status.SinkDevice ?? "–"}");
    Console.WriteLine($"  Virtual:     {(status.VirtualMicAvailable ? status.VirtualDevice : "unavailable")}");
    Console.WriteLine($"  Monitoring:  {(status.MonitorEnabled ? "on" : "off")}");
    Console.WriteLine($"  Latency:     {status.LatencyMs} ms");
    Console.WriteLine($"  Level:       {status.Meter.PeakDb.ToString("0.0", CultureInfo.InvariantCulture)} dBFS");

    if (!string.IsNullOrWhiteSpace(status.Notice))
    {
        Console.WriteLine($"  Notice:      {status.Notice}");
    }

    PrintSettings(new AppSettings { Processing = status.Processing }, indent: "  ");
}

static void PrintSettings(AppSettings? settings, string indent = "  ")
{
    if (settings is null)
    {
        return;
    }

    var processing = settings.Processing;
    Console.WriteLine();
    Console.WriteLine($"{indent}Effects");
    foreach (var parameter in ParameterCatalog.Main.Where(parameter => parameter.Group == "Effects"))
    {
        Console.WriteLine($"{indent}{parameter.Label,-12} {parameter.Format(parameter.Read(processing))}");
    }

    Console.WriteLine($"{indent}Noise");
    foreach (var parameter in ParameterCatalog.Main.Where(parameter => parameter.Group == "Noise"))
    {
        Console.WriteLine($"{indent}{parameter.Label,-12} {parameter.Format(parameter.Read(processing))}");
    }

    Console.WriteLine($"{indent}Details");
    foreach (var parameter in ParameterCatalog.Details)
    {
        Console.WriteLine($"{indent}{parameter.Label,-12} {parameter.Format(parameter.Read(processing))}");
    }

    Console.WriteLine($"{indent}Switches");
    foreach (var toggle in ParameterCatalog.Toggles)
    {
        Console.WriteLine($"{indent}{toggle.Label,-12} {(toggle.Read(processing) ? "on" : "off")}");
    }
}

static string? NormalizeValue(string name, string raw)
{
    var percent = raw.EndsWith('%');
    var text = percent ? raw[..^1] : raw;

    if (!float.TryParse(
            text.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value))
    {
        return raw;
    }

    var parameter = ParameterCatalog.Find(name);
    if (parameter is null)
    {
        return raw;
    }

    if (percent)
    {
        value /= 100f;
    }
    else if (parameter.Id == "tone" && MathF.Abs(value) > 1f)
    {
        value /= 12f;
    }
    else if (parameter.Id == "volume" && value > 2f)
    {
        value /= 100f;
    }
    else if (parameter.Id is "gate" or "denoise" or "mix" && value > 1f)
    {
        value /= 100f;
    }
    else if (parameter.Id == "denoise-smoothing" && value > 1f)
    {
        value /= 100f;
    }

    return value.ToString("R", CultureInfo.InvariantCulture);
}

static void PrintHelp()
{
    Console.WriteLine("easymic – EasyMic control and terminal UI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  easymic                         Start the UI");
    Console.WriteLine("  easymic status [--json]         Show status");
    Console.WriteLine("  easymic monitor [on|off]        Toggle monitoring");
    Console.WriteLine("  easymic set <name> <value>      Set a control");
    Console.WriteLine("  easymic get [name] [--json]     Show values");
    Console.WriteLine("  easymic devices [source|sink]   List devices");
    Console.WriteLine("  easymic source <name>           Change input");
    Console.WriteLine("  easymic sink <name>             Change output");
    Console.WriteLine("  easymic latency <ms>            Set monitoring latency");
    Console.WriteLine("  easymic reset                   Reset controls");
    Console.WriteLine("  easymic daemon <start|stop|restart|status>");
    Console.WriteLine("  easymic notify                  Show login notification");
    Console.WriteLine();
    Console.WriteLine("Controls:");
    Console.WriteLine("  pitch, volume, tone, gate, threshold, denoise, reduction, mix");
    Console.WriteLine("  gate-attack, gate-release, gate-hold, gate-damping");
    Console.WriteLine("  denoise-floor, denoise-smoothing, gate-on, denoise-on, limiter-on");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  easymic set volume 120");
    Console.WriteLine("  easymic set gate 45%");
    Console.WriteLine("  easymic set tone -6");
    Console.WriteLine("  easymic set limiter-on off");
}

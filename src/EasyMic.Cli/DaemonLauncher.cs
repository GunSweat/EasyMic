using System.Diagnostics;
using System.Text;
using EasyMic.Ipc;
using EasyMic.Settings;

namespace EasyMic;

/// <summary>
/// Starts and stops the daemon on demand. The primary path is the systemd user
/// service; a detached process is used as a fallback for non-systemd setups.
/// </summary>
public static class DaemonLauncher
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(6);

    public static async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        var socket = IpcProtocol.DefaultSocketPath;

        if (await IpcClient.ProbeAsync(socket, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await TrySystemctlAsync("start", cancellationToken).ConfigureAwait(false);

        if (await WaitForSocketAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var daemon = LocateDaemon();
        if (daemon is null)
        {
            throw new InvalidOperationException(
                "easymicd was not found. Run install.sh first.");
        }

        StartDetached(daemon);

        if (await WaitForSocketAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        throw new InvalidOperationException(
            "The EasyMic daemon could not be started. Details are in "
            + EasyMicPaths.DaemonLogPath + ".");
    }

    public static async Task<int> StopAsync(CancellationToken cancellationToken = default)
    {
        var socket = IpcProtocol.DefaultSocketPath;

        if (await IpcClient.ProbeAsync(socket, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await using var client = new IpcClient();
                await client.ConnectAsync(socket, cancellationToken).ConfigureAwait(false);
                await client.SendAsync(new IpcRequest("shutdown"), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Fall through to systemctl.
            }

            for (var i = 0; i < 60; i++)
            {
                if (!await IpcClient.ProbeAsync(socket, cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        await TrySystemctlAsync("stop", cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public static async Task<string> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (await IpcClient.ProbeAsync(IpcProtocol.DefaultSocketPath, cancellationToken).ConfigureAwait(false))
        {
            return "running";
        }

        var result = await RunAsync(
            "systemctl",
            new[] { "--user", "is-active", "easymicd.service" },
            cancellationToken).ConfigureAwait(false);

        var state = result.StdOut.Trim();
        return state.Length == 0 || state == "unknown" ? "stopped" : state;
    }

    public static async Task<bool> WaitForSocketAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + StartTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await IpcClient.ProbeAsync(IpcProtocol.DefaultSocketPath, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task TrySystemctlAsync(string verb, CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(
                "systemctl",
                new[] { "--user", verb, "easymicd.service" },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // systemd is optional; the detached fallback follows.
        }
    }

    private static void StartDetached(string daemon)
    {
        var log = EasyMicPaths.DaemonLogPath;
        var directory = Path.GetDirectoryName(log);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var quotedDaemon = daemon.Replace("'", "'\\''");
        var quotedLog = log.Replace("'", "'\\''");

        var startInfo = new ProcessStartInfo("setsid")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"exec '{quotedDaemon}' </dev/null >>'{quotedLog}' 2>&1");

        try
        {
            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"easymicd could not be started: {exception.Message}");
        }
    }

    private static string? LocateDaemon()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "easymicd"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "bin",
                "easymicd"),
        };

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.AddRange(path
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, "easymicd")));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{fileName} could not be started.");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore.
            }

            throw;
        }

        return (
            process.ExitCode,
            (await stdout.ConfigureAwait(false)).Trim(),
            (await stderr.ConfigureAwait(false)).Trim());
    }
}

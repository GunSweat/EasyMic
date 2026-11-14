using System.Diagnostics;

namespace EasyMic;

/// <summary>
/// Shows the login notification and opens the TUI when "Open EasyMic" is
/// clicked. Used by the XDG autostart entry.
/// </summary>
public static class Notifier
{
    public static async Task<int> ShowAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await DaemonLauncher.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The notification is still useful even if the daemon is down.
        }

        var startInfo = new ProcessStartInfo("notify-send")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--app-name=EasyMic");
        startInfo.ArgumentList.Add("--icon=audio-input-microphone");
        startInfo.ArgumentList.Add("--urgency=normal");
        startInfo.ArgumentList.Add("--expire-time=0");
        startInfo.ArgumentList.Add("--action=open=Open EasyMic");
        startInfo.ArgumentList.Add("--wait");
        startInfo.ArgumentList.Add("Open EasyMic");
        startInfo.ArgumentList.Add("EasyMic is ready – click to open the interface.");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch
        {
            Console.Error.WriteLine("notify-send is not available.");
            return 1;
        }

        if (process is null)
        {
            return 1;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        process.Dispose();

        var action = stdout.Trim();
        if (!action.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return OpenTerminal() ? 0 : 1;
    }

    private static bool OpenTerminal()
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(self))
        {
            self = "easymic";
        }

        var terminal = Environment.GetEnvironmentVariable("TERMINAL");
        var candidates = new List<(string File, string[] Args)>();

        if (!string.IsNullOrWhiteSpace(terminal))
        {
            var parts = terminal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                candidates.Add((parts[0], parts.Skip(1).ToArray()));
            }
        }

        candidates.Add(("konsole", new[] { "-e", self }));
        candidates.Add(("alacritty", new[] { "-e", self }));
        candidates.Add(("kitty", new[] { self }));
        candidates.Add(("foot", new[] { self }));
        candidates.Add(("x-terminal-emulator", new[] { "-e", self }));
        candidates.Add(("gnome-terminal", new[] { "--", self }));
        candidates.Add(("xfce4-terminal", new[] { "-x", self }));

        foreach (var (file, args) in candidates)
        {
            try
            {
                var startInfo = new ProcessStartInfo(file)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                foreach (var argument in args)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                var process = Process.Start(startInfo);
                if (process is not null)
                {
                    process.Dispose();
                    return true;
                }
            }
            catch
            {
                // Try the next terminal.
            }
        }

        return false;
    }
}

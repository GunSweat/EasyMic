using System.Diagnostics;

namespace EasyMic.Audio;

internal readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
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

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"{fileName} could not be started.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new ProcessResult(
            process.ExitCode,
            (await stdout.ConfigureAwait(false)).Trim(),
            (await stderr.ConfigureAwait(false)).Trim());
    }
}

internal static class Pactl
{
    public static Task<ProcessResult> RunAsync(params string[] args)
        => ProcessRunner.RunAsync("pactl", args);
}

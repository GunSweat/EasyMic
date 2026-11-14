using System.Runtime.InteropServices;
using EasyMic.Daemon;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length > 0)
{
    switch (args[0])
    {
        case "-h":
        case "--help":
            Console.WriteLine("easymicd – EasyMic audio daemon");
            Console.WriteLine();
            Console.WriteLine("Starts audio processing, the virtual microphone and the IPC socket.");
            Console.WriteLine("The daemon is normally started via easymicd.service.");
            Console.WriteLine();
            Console.WriteLine("  --version   Print version");
            return 0;

        case "--version":
            Console.WriteLine("easymicd 1.0.0");
            return 0;
    }
}

await using var host = new DaemonHost();
host.Log += message => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");

using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    host.RequestShutdown();
});

using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    host.RequestShutdown();
});

try
{
    var error = await host.StartAsync();
    if (error is not null)
    {
        Console.Error.WriteLine(error);
        return error.Contains("already running", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    await host.Shutdown;
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"EasyMic daemon: {exception}");
    return 1;
}

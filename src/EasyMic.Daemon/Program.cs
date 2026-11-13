using System.Runtime.InteropServices;
using EasyMic.Daemon;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length > 0)
{
    switch (args[0])
    {
        case "-h":
        case "--help":
            Console.WriteLine("easymicd – EasyMic-Audiodaemon");
            Console.WriteLine();
            Console.WriteLine("Startet die Audioverarbeitung, das virtuelle Mikrofon und den IPC-Socket.");
            Console.WriteLine("Der Daemon wird normalerweise über easymicd.service gestartet.");
            Console.WriteLine();
            Console.WriteLine("  --version   Version ausgeben");
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
        return error.Contains("läuft bereits", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
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
    Console.Error.WriteLine($"EasyMic-Daemon: {exception}");
    return 1;
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EasyMic.Ui;

public enum UiEventKind
{
    Key,
    MouseDown,
    MouseMove,
    MouseUp,
    WheelUp,
    WheelDown,
}

public enum UiKey
{
    None,
    Up,
    Down,
    Left,
    Right,
    PageUp,
    PageDown,
    Home,
    End,
    Escape,
    Enter,
    Tab,
    Backspace,
    Delete,
}

public readonly record struct UiEvent(
    UiEventKind Kind,
    char Key,
    UiKey Code,
    bool Shift,
    int X,
    int Y);

/// <summary>
/// Reads keyboard and mouse input in raw terminal mode. Input is parsed from
/// bytes so arrow keys, modifier keys and SGR mouse reports all arrive reliably.
/// </summary>
public sealed class TerminalInput : IDisposable
{
    private static readonly TimeSpan EscapeTimeout = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan SequenceTimeout = TimeSpan.FromMilliseconds(200);

    private readonly ConcurrentQueue<UiEvent> _events = new();
    private readonly List<byte> _buffer = new();
    private readonly object _bufferGate = new();

    private Thread? _reader;
    private volatile bool _stopping;
    private string? _savedState;
    private DateTime _escapeAt;

    public bool IsActive { get; private set; }

    public bool PointerMode { get; private set; }

    public void Start()
    {
        if (IsActive)
        {
            return;
        }

        try
        {
            _savedState = RunStty("-g").Trim();
        }
        catch
        {
            _savedState = null;
        }

        try
        {
            RunStty("-icanon -echo min 1 time 0");
        }
        catch
        {
            // Without stty the UI still works, but keys arrive line-buffered.
        }

        _stopping = false;
        IsActive = true;

        Write("\x1b[?1049h\x1b[?25l\x1b[2J\x1b[H");
        EnablePointerMode();

        _reader = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "easymic-input",
        };
        _reader.Start();
    }

    public void Stop()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        _stopping = true;

        try
        {
            DisablePointerMode();
            Write("\x1b[?25h\x1b[?1049l");
        }
        catch
        {
            // Terminal already gone.
        }

        // Restore the tty before closing our stdin handle; stty needs fd 0.
        if (_savedState is not null && _savedState.Length > 0)
        {
            try
            {
                RunStty(_savedState);
            }
            catch
            {
                // Ignore.
            }
        }

        if (_reader is not null && _reader.IsAlive)
        {
            try
            {
                _reader.Join(500);
            }
            catch
            {
                // Ignore.
            }
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Enables pointer reporting through the OSC 1337 pointer protocol used by
    /// SSH terminals as well as SGR mouse reporting for local terminals.
    /// </summary>
    public void EnablePointerMode()
    {
        if (PointerMode)
        {
            return;
        }

        PointerMode = true;
        Write("\x1b[?1002h\x1b[?1006h\x1b]1337;SetButtons=on\x07");
    }

    public void DisablePointerMode()
    {
        if (!PointerMode)
        {
            return;
        }

        PointerMode = false;
        Write("\x1b]1337;SetButtons=off\x07\x1b[?1006l\x1b[?1002l");
    }

    public IReadOnlyList<UiEvent> Poll()
    {
        var result = new List<UiEvent>();

        lock (_bufferGate)
        {
            ParseLocked(result);
        }

        while (_events.TryDequeue(out var queued))
        {
            result.Add(queued);
        }

        return result;
    }

    public static void Write(string text)
    {
        Console.Write(text);
        Console.Out.Flush();
    }

    private void ReadLoop()
    {
        var buffer = new byte[512];
        var fds = new[] { new PollFd { Fd = StandardInput, Events = PollIn } };

        try
        {
            while (!_stopping)
            {
                fds[0].Revents = 0;
                var ready = poll(fds, 1, 100);

                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() == Eintr)
                    {
                        continue;
                    }

                    break;
                }

                if (ready == 0 || (fds[0].Revents & PollIn) == 0)
                {
                    if ((fds[0].Revents & (PollErr | PollHup | PollNval)) != 0)
                    {
                        break;
                    }

                    continue;
                }

                var read = NativeRead(StandardInput, buffer, buffer.Length);

                if (read < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is Eintr or Eagain)
                    {
                        continue;
                    }

                    break;
                }

                if (read == 0)
                {
                    break;
                }

                lock (_bufferGate)
                {
                    for (var i = 0; i < read; i++)
                    {
                        _buffer.Add(buffer[i]);
                    }
                }
            }
        }
        catch
        {
            // Stream closed during shutdown.
        }
    }

    private const int StandardInput = 0;
    private const int Eintr = 4;
    private const int Eagain = 11;
    private const short PollIn = 0x0001;
    private const short PollErr = 0x0008;
    private const short PollHup = 0x0010;
    private const short PollNval = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int poll([In, Out] PollFd[] fds, nint count, int timeout);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint NativeRead(int fd, byte[] buffer, nint count);

    private void ParseLocked(List<UiEvent> events)
    {
        while (_buffer.Count > 0)
        {
            var first = _buffer[0];

            if (first == 0x1b)
            {
                if (_buffer.Count == 1)
                {
                    _escapeAt = _escapeAt == default ? DateTime.UtcNow : _escapeAt;
                    if (DateTime.UtcNow - _escapeAt >= EscapeTimeout)
                    {
                        _buffer.Clear();
                        _escapeAt = default;
                        events.Add(new UiEvent(UiEventKind.Key, '\0', UiKey.Escape, false, 0, 0));
                    }

                    return;
                }

                if (_buffer[1] == (byte)'[')
                {
                    if (!TryParseCsi(events))
                    {
                        return;
                    }

                    continue;
                }

                if (_buffer[1] == (byte)']')
                {
                    if (!TryParseOsc(events))
                    {
                        return;
                    }

                    continue;
                }

                if (_buffer[1] == (byte)'O')
                {
                    _buffer.RemoveRange(0, Math.Min(3, _buffer.Count));
                    continue;
                }

                _buffer.RemoveAt(0);
                _escapeAt = default;
                events.Add(new UiEvent(UiEventKind.Key, '\0', UiKey.Escape, false, 0, 0));
                continue;
            }

            _buffer.RemoveAt(0);
            _escapeAt = default;
            events.Add(MapByte(first));
        }
    }

    private bool TryParseOsc(List<UiEvent> events)
    {
        var end = -1;
        var terminatorLength = 1;

        for (var i = 2; i < _buffer.Count; i++)
        {
            if (_buffer[i] == 0x07)
            {
                end = i;
                terminatorLength = 1;
                break;
            }

            if (_buffer[i] == 0x1b && i + 1 < _buffer.Count && _buffer[i + 1] == (byte)'\\')
            {
                end = i;
                terminatorLength = 2;
                break;
            }
        }

        if (end < 0)
        {
            if (_buffer.Count > 128)
            {
                _buffer.Clear();
            }

            return false;
        }

        var payload = Encoding.ASCII.GetString(_buffer.ToArray(), 2, end - 2);
        _buffer.RemoveRange(0, end + terminatorLength);
        _escapeAt = default;

        var parts = payload.Split(';');
        if (parts.Length < 3 || parts[0] != "1337")
        {
            return true;
        }

        var kind = parts[1] switch
        {
            "PointerDown" => UiEventKind.MouseDown,
            "PointerUp" => UiEventKind.MouseUp,
            "PointerMove" => UiEventKind.MouseMove,
            "WheelUp" => UiEventKind.WheelUp,
            "WheelDown" => UiEventKind.WheelDown,
            _ => UiEventKind.MouseMove,
        };

        if (parts.Length < 4)
        {
            return true;
        }

        var x = ParseCoordinate(parts[2]);
        var y = ParseCoordinate(parts[3]);

        events.Add(new UiEvent(kind, '\0', UiKey.None, false, x, y));
        return true;
    }

    private static int ParseCoordinate(string value)
        => int.TryParse(value, out var parsed) ? parsed : 0;

    private bool TryParseCsi(List<UiEvent> events)
    {
        var finalIndex = -1;

        for (var i = 2; i < _buffer.Count; i++)
        {
            var value = _buffer[i];
            if (value is >= 0x40 and <= 0x7e)
            {
                finalIndex = i;
                break;
            }
        }

        if (finalIndex < 0)
        {
            if (_buffer.Count > 64)
            {
                _buffer.Clear();
            }

            return false;
        }

        var parameters = Encoding.ASCII.GetString(_buffer.ToArray(), 2, finalIndex - 2);
        var final = (char)_buffer[finalIndex];
        _buffer.RemoveRange(0, finalIndex + 1);
        _escapeAt = default;

        if (final is 'M' or 'm' && parameters.StartsWith('<'))
        {
            ParseMouse(parameters, final, events);
            return true;
        }

        switch (final)
        {
            case 'A':
                events.Add(Arrow(UiKey.Up, parameters));
                break;
            case 'B':
                events.Add(Arrow(UiKey.Down, parameters));
                break;
            case 'C':
                events.Add(Arrow(UiKey.Right, parameters));
                break;
            case 'D':
                events.Add(Arrow(UiKey.Left, parameters));
                break;
            case 'H':
                events.Add(Arrow(UiKey.Home, parameters));
                break;
            case 'F':
                events.Add(Arrow(UiKey.End, parameters));
                break;
            case '~':
                events.Add(Tilde(parameters));
                break;
            case 'Z':
                events.Add(new UiEvent(UiEventKind.Key, '\t', UiKey.Tab, true, 0, 0));
                break;
        }

        return true;
    }

    private static UiEvent Arrow(UiKey key, string parameters)
    {
        var shift = HasShift(parameters);
        return new UiEvent(UiEventKind.Key, '\0', key, shift, 0, 0);
    }

    private static UiEvent Tilde(string parameters)
    {
        var parts = parameters.Split(';');
        var code = int.TryParse(parts[0], out var number) ? number : 0;
        var shift = parts.Length > 1 && HasShift(parts[1]);

        var key = code switch
        {
            1 or 7 => UiKey.Home,
            2 => UiKey.Delete,
            3 => UiKey.Delete,
            4 or 8 => UiKey.End,
            5 => UiKey.PageUp,
            6 => UiKey.PageDown,
            _ => UiKey.None,
        };

        return new UiEvent(UiEventKind.Key, '\0', key, shift, 0, 0);
    }

    private static bool HasShift(string parameters)
    {
        var parts = parameters.Split(';');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var modifier))
        {
            return false;
        }

        return ((modifier - 1) & 1) != 0;
    }

    private static void ParseMouse(string parameters, char final, List<UiEvent> events)
    {
        var parts = parameters[1..].Split(';');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var button) ||
            !int.TryParse(parts[1], out var x) ||
            !int.TryParse(parts[2], out var y))
        {
            return;
        }

        // SGR reports are 1-based; the UI works with 0-based coordinates.
        x -= 1;
        y -= 1;

        if ((button & 64) != 0)
        {
            var kind = (button & 1) == 0 ? UiEventKind.WheelUp : UiEventKind.WheelDown;
            events.Add(new UiEvent(kind, '\0', UiKey.None, false, x, y));
            return;
        }

        var left = (button & 3) == 0;
        if (!left)
        {
            return;
        }

        if (final == 'm')
        {
            events.Add(new UiEvent(UiEventKind.MouseUp, '\0', UiKey.None, false, x, y));
        }
        else if ((button & 32) != 0)
        {
            events.Add(new UiEvent(UiEventKind.MouseMove, '\0', UiKey.None, false, x, y));
        }
        else
        {
            events.Add(new UiEvent(UiEventKind.MouseDown, '\0', UiKey.None, false, x, y));
        }
    }

    private static UiEvent MapByte(byte value)
    {
        return value switch
        {
            0x03 => new UiEvent(UiEventKind.Key, 'q', UiKey.None, false, 0, 0),
            0x09 => new UiEvent(UiEventKind.Key, '\t', UiKey.Tab, false, 0, 0),
            0x0d or 0x0a => new UiEvent(UiEventKind.Key, '\r', UiKey.Enter, false, 0, 0),
            0x7f => new UiEvent(UiEventKind.Key, '\0', UiKey.Backspace, false, 0, 0),
            >= 0x20 and <= 0x7e => new UiEvent(UiEventKind.Key, (char)value, UiKey.None, char.IsUpper((char)value), 0, 0),
            _ => new UiEvent(UiEventKind.Key, '\0', UiKey.None, false, 0, 0),
        };
    }

    private static string RunStty(string arguments)
    {
        var startInfo = new ProcessStartInfo("stty", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return string.Empty;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(1000);
        return output;
    }
}

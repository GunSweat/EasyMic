using System.Globalization;
using System.Text;
using EasyMic.Audio;

namespace EasyMic.Ui;

public readonly record struct HitArea(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

public sealed record SliderHit(int TrackLeft, int TrackWidth);

public sealed record ToggleHit(int ControlIndex, int Left, int Width);

public sealed record DashboardRow(
    string Text,
    int? ControlIndex,
    SliderHit? Slider = null,
    IReadOnlyList<ToggleHit>? Toggles = null);

public sealed record DashboardFrame(
    int Width,
    int Height,
    int InnerWidth,
    IReadOnlyList<DashboardRow> Header,
    IReadOnlyList<DashboardRow> Content,
    IReadOnlyList<DashboardRow> Footer,
    int ViewportRows);

public readonly record struct SliderArea(int ControlIndex, int Row, int Left, int Width);

public readonly record struct ToggleArea(int ControlIndex, int Row, int Left, int Right);

public sealed class DashboardHits
{
    public Dictionary<int, int> RowToControl { get; } = new();
    public List<SliderArea> Sliders { get; } = new();
    public List<ToggleArea> Toggles { get; } = new();
    public HitArea MonitorBadge { get; set; }
    public HitArea QuitButton { get; set; }
}

/// <summary>
/// Builds and draws the EasyMic terminal UI. Rendering is line based so the
/// middle section can scroll while header and footer stay fixed.
/// </summary>
public static class Dashboard
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";

    private static readonly string Accent = Rgb(90, 205, 255);
    private static readonly string Good = Rgb(95, 220, 140);
    private static readonly string Bad = Rgb(255, 115, 115);
    private static readonly string Muted = Rgb(130, 142, 158);
    private static readonly string Text = Rgb(222, 228, 238);
    private static readonly string Yellow = Rgb(242, 202, 112);

    public static DashboardFrame Build(AppState state)
    {
        var width = Math.Max(56, GetWidth());
        var height = Math.Max(16, GetHeight());
        var inner = width - 2;
        var viewport = Math.Max(4, height - 6);

        var header = BuildHeader(state, width, inner);
        var footer = BuildFooter(state, width, inner);
        var content = BuildContent(state, inner);

        return new DashboardFrame(width, height, inner, header, content, footer, viewport);
    }

    public static DashboardHits Draw(DashboardFrame frame, AppState state)
    {
        var hits = new DashboardHits();

        var selectedRow = frame.Content
            .Select((row, index) => (row, index))
            .FirstOrDefault(item => item.row.ControlIndex == state.Selected)
            .index;

        if (!frame.Content.Any(row => row.ControlIndex == state.Selected))
        {
            selectedRow = -1;
        }

        if (selectedRow >= 0)
        {
            if (selectedRow < state.Scroll)
            {
                state.Scroll = selectedRow;
            }
            else if (selectedRow >= state.Scroll + frame.ViewportRows)
            {
                state.Scroll = selectedRow - frame.ViewportRows + 1;
            }
        }

        state.Scroll = Math.Clamp(state.Scroll, 0, Math.Max(0, frame.Content.Count - frame.ViewportRows));

        var lines = new List<string>(frame.Height);

        foreach (var row in frame.Header)
        {
            lines.Add(row.Text);
        }

        for (var i = 0; i < frame.ViewportRows; i++)
        {
            var index = state.Scroll + i;
            var y = lines.Count;

            if (index < frame.Content.Count)
            {
                var row = frame.Content[index];
                lines.Add(row.Text);

                if (row.ControlIndex is not null)
                {
                    hits.RowToControl[y] = row.ControlIndex.Value;

                    if (row.Slider is { } slider)
                    {
                        hits.Sliders.Add(new SliderArea(
                            row.ControlIndex.Value,
                            y,
                            slider.TrackLeft,
                            slider.TrackWidth));
                    }
                }

                if (row.Toggles is not null)
                {
                    foreach (var toggle in row.Toggles)
                    {
                        hits.Toggles.Add(new ToggleArea(
                            toggle.ControlIndex,
                            y,
                            toggle.Left,
                            toggle.Left + toggle.Width - 1));
                    }
                }
            }
            else
            {
                var line = new Line();
                line.Add("│", Muted);
                line.PadTo(frame.Width - 1);
                line.Add("│", Muted);
                lines.Add(line.Text);
            }
        }

        var footerStart = lines.Count;
        foreach (var row in frame.Footer)
        {
            lines.Add(row.Text);
        }

        var builder = new StringBuilder(frame.Width * frame.Height + 512);
        for (var y = 0; y < lines.Count; y++)
        {
            builder.Append("\x1b[").Append(y + 1).Append(";1H");
            builder.Append(lines[y]);
            builder.Append("\x1b[K");
        }

        builder.Append("\x1b[").Append(frame.Height).Append(";1H");
        TerminalInput.Write(builder.ToString());

        var badgeText = MonitorBadgeText(state);
        var badgeLeft = 2;
        var badgeRow = frame.Height - 1;
        hits.MonitorBadge = new HitArea(badgeLeft, badgeRow, badgeLeft + badgeText.Length - 1, badgeRow);

        var quitText = "Q Quit ";
        var quitLeft = frame.Width - 2 - quitText.Length;
        hits.QuitButton = new HitArea(quitLeft, badgeRow, quitLeft + quitText.Length - 1, badgeRow);
        _ = footerStart;

        return hits;
    }

    private static List<DashboardRow> BuildHeader(AppState state, int width, int inner)
    {
        var rows = new List<DashboardRow>();

        var top = new Line();
        top.Add("┌─ ", Muted);
        top.Add("EasyMic", Bold + Accent);
        top.Add(" " + new string('─', Math.Max(1, inner - 10)), Muted);
        top.Add("┐", Muted);
        rows.Add(new DashboardRow(top.Text, null));

        var status = new Line();
        status.Add("│", Muted);

        var live = state.Running;
        var liveText = live ? "LIVE" : "OFFLINE";
        var prefixLength = 2 + liveText.Length + "  ·  48 kHz · Mono  ·  ".Length;

        var right = new Line();
        right.Add("Virtual: ", Muted);
        right.Add(state.VirtualMicAvailable ? "ON" : "OFF",
            state.VirtualMicAvailable ? Good : Bad);
        right.Add("   Monitor: ", Muted);
        right.Add(state.MonitorEnabled ? "ON" : "OFF", state.MonitorEnabled ? Good : Muted);
        right.Add($"   {state.LatencyMs} ms", Muted);

        var available = inner - prefixLength - right.Length - 1;
        if (available < 8)
        {
            right = new Line();
            right.Add(state.MonitorEnabled ? "● MON" : "○ MON", state.MonitorEnabled ? Good : Muted);
            available = inner - prefixLength - right.Length - 1;
        }

        var source = Clip(state.SourceDevice ?? "no microphone", Math.Max(4, available));

        status.Add("● ", live ? Good : Bad);
        status.Add(liveText, live ? Bold + Good : Bold + Bad);
        status.Add("  ·  48 kHz · Mono  ·  ", Muted);
        status.Add(source, Text);

        available = inner - status.Length - right.Length - 1;
        if (available > 0)
        {
            status.Space(available);
            status.Raw(right.Text, right.Length);
        }

        status.PadTo(inner);
        status.Add("│", Muted);
        rows.Add(new DashboardRow(status.Text, null));

        var separator = new Line();
        separator.Add("├" + new string('─', inner) + "┤", Muted);
        rows.Add(new DashboardRow(separator.Text, null));

        return rows;
    }

    private static List<DashboardRow> BuildFooter(AppState state, int width, int inner)
    {
        var rows = new List<DashboardRow>();

        var separator = new Line();
        separator.Add("├" + new string('─', inner) + "┤", Muted);
        rows.Add(new DashboardRow(separator.Text, null));

        var hint = new Line();
        hint.Add("│", Muted);
        hint.Add(Clip(" Mouse: click/drag = sliders & switches   Wheel = scroll   M = Monitor   Q = Quit", inner), Muted);
        hint.PadTo(inner);
        hint.Add("│", Muted);
        rows.Add(new DashboardRow(hint.Text, null));

        var bottom = new Line();
        bottom.Add("╰─", Muted);

        var badge = MonitorBadgeText(state);
        bottom.Add(badge, state.MonitorEnabled ? Bold + Good : Bold + Muted);

        var rightText = "Q Quit ";
        var fill = inner - badge.Length - rightText.Length - 2;
        if (fill > 0)
        {
            bottom.Add(new string('─', fill), Muted);
        }

        bottom.Add(rightText, Muted);
        bottom.Add("─╯", Muted);
        rows.Add(new DashboardRow(bottom.Text, null));

        return rows;
    }

    private static List<DashboardRow> BuildContent(AppState state, int inner)
    {
        var rows = new List<DashboardRow>();

        rows.Add(DeviceRow(state, inner));
        rows.Add(MeterRow(state, inner));

        var notice = state.Notice ?? state.Status?.Notice;
        var noticeIsError = state.Notice is not null ? state.NoticeIsError : state.Status?.NoticeIsError == true;

        if (!string.IsNullOrWhiteSpace(notice))
        {
            var line = new Line();
            line.Add((noticeIsError ? "! " : "  ") + Clip(notice, inner - 2), noticeIsError ? Bad : Muted);
            line.PadTo(inner);
            rows.Add(new DashboardRow(line.Text, null));
        }
        else
        {
            rows.Add(Blank(inner));
        }

        rows.Add(Section("EFFECTS", inner));

        for (var i = 0; i < ParameterCatalog.Main.Count; i++)
        {
            rows.Add(Slider(ParameterCatalog.Main[i], state, i, inner));
        }

        rows.Add(Blank(inner));
        rows.Add(BoxTop("NOISE GATE & DENOISE", inner));

        var boxContentWidth = inner - 4;

        rows.Add(BoxRow(ToggleRow(state, boxContentWidth, 8, 9), inner));

        rows.Add(BoxRow(SubHeader("Gate Details", boxContentWidth), inner));

        for (var i = 0; i < 4; i++)
        {
            var parameter = ParameterCatalog.Details[i];
            rows.Add(BoxRow(Slider(parameter, state, 10 + i, boxContentWidth), inner));
        }

        rows.Add(BoxRow(SubHeader("Denoise Details", boxContentWidth), inner));

        for (var i = 4; i < 6; i++)
        {
            var parameter = ParameterCatalog.Details[i];
            rows.Add(BoxRow(Slider(parameter, state, 10 + i, boxContentWidth), inner));
        }

        rows.Add(BoxRow(LimiterRow(state, boxContentWidth), inner));
        rows.Add(BoxBottom(inner));
        rows.Add(Blank(inner));

        return rows;
    }

    private static DashboardRow DeviceRow(AppState state, int inner)
    {
        var line = new Line();
        line.Add(" INPUT    ", Muted);
        line.Add(Clip(state.SourceDevice ?? "–", Math.Max(8, inner / 2 - 12)), Text);
        line.Add("     OUTPUT   ", Muted);
        line.Add(Clip(state.SinkDevice ?? "–", Math.Max(8, inner / 2 - 14)), Text);
        line.PadTo(inner);
        return new DashboardRow(line.Text, null);
    }

    private static DashboardRow MeterRow(AppState state, int inner)
    {
        var line = new Line();
        line.Add(" LEVEL    ", Muted);

        var valueWidth = 19;
        var barWidth = Math.Max(10, inner - 10 - valueWidth);
        var peak = state.Meter.PeakDb;
        var level = Math.Clamp((peak + LevelAnalyzer.FloorDb) / -LevelAnalyzer.FloorDb, 0d, 1d);
        var position = (int)Math.Round(level * (barWidth - 1));
        var color = peak > -6 ? Bad : peak > -18 ? Yellow : Good;

        for (var i = 0; i < barWidth; i++)
        {
            if (i <= position)
            {
                line.Raw("█", 1, color);
            }
            else
            {
                line.Raw("░", 1, Dim + Muted);
            }
        }

        var peakText = peak <= LevelAnalyzer.FloorDb + 0.01
            ? "  –∞ dB"
            : peak.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5) + " dB";

        line.Add("  " + peakText.PadRight(valueWidth), Text);
        line.PadTo(inner);
        return new DashboardRow(line.Text, null);
    }

    private static DashboardRow Section(string title, int inner)
    {
        var line = new Line();
        line.Add(" " + title, Bold + Accent);
        var remaining = inner - title.Length - 2;
        if (remaining > 0)
        {
            line.Add(" " + new string('─', remaining), Muted);
        }

        line.PadTo(inner);
        return new DashboardRow(line.Text, null);
    }

    private static DashboardRow SubHeader(string title, int width)
    {
        var line = new Line();
        line.Add(" " + title, Muted);
        line.PadTo(width);
        return new DashboardRow(line.Text, null);
    }

    private static DashboardRow Slider(TuningParameter parameter, AppState state, int controlIndex, int width)
    {
        var selected = state.Selected == controlIndex;
        var labelWidth = Math.Clamp(width / 5, 9, 14);
        var valueWidth = Math.Clamp(width / 5, 11, 15);
        var trackWidth = width - labelWidth - valueWidth - 2;

        if (trackWidth < 8)
        {
            trackWidth = Math.Max(4, width - labelWidth - 11);
            valueWidth = width - labelWidth - trackWidth - 2;
        }

        var line = new Line();
        line.Add(Pad(Clip(parameter.Label, labelWidth), labelWidth) + " ",
            selected ? Bold + Accent : Muted);

        var value = parameter.Read(state.Processing);
        var normalization = (value - parameter.Minimum) / (parameter.Maximum - parameter.Minimum);
        normalization = Math.Clamp(normalization, 0f, 1f);

        BuildTrack(line, trackWidth, normalization, selected);
        line.Add(" " + Pad(parameter.Format(value), Math.Max(1, valueWidth)),
            selected ? Bold + Text : Text);

        line.PadTo(width);
        return new DashboardRow(
            line.Text,
            controlIndex,
            new SliderHit(labelWidth + 1, trackWidth));
    }

    private static void BuildTrack(Line line, int width, float normalization, bool selected)
    {
        var center = (width - 1) / 2;
        var position = (int)MathF.Round(Math.Clamp(normalization, 0f, 1f) * (width - 1));
        var accent = selected ? Bold + Accent : Accent;
        var baseColor = selected ? Text : Muted;

        for (var i = 0; i < width; i++)
        {
            if (i == position)
            {
                line.Raw("●", 1, accent);
            }
            else if (i == center)
            {
                line.Raw("┼", 1, baseColor);
            }
            else
            {
                line.Raw("─", 1, i < position ? baseColor : Dim + Muted);
            }
        }
    }

    private static DashboardRow ToggleRow(AppState state, int width, int firstIndex, int secondIndex)
    {
        var line = new Line();
        line.Add(" On/Off:   ", Muted);

        var first = AppState.Controls[firstIndex];
        var second = AppState.Controls[secondIndex];
        var toggles = new List<ToggleHit>();

        var (left, length) = AddToggle(line, first, firstIndex, state);
        toggles.Add(new ToggleHit(firstIndex, left, length));
        line.Add("    ", Reset);
        (left, length) = AddToggle(line, second, secondIndex, state);
        toggles.Add(new ToggleHit(secondIndex, left, length));

        line.PadTo(width);
        return new DashboardRow(line.Text, null, null, toggles);
    }

    private static (int Left, int Length) AddToggle(
        Line line,
        ControlRef control,
        int index,
        AppState state)
    {
        var toggle = control.Toggle!;
        var on = toggle.Read(state.Processing);
        var selected = state.Selected == index;
        var text = $"[{(on ? "●" : "○")} {toggle.Label} {(on ? "ON" : "OFF")}]";
        var color = selected ? Bold + Accent : on ? Good : Muted;
        var left = line.Length;
        line.Add(text, color);
        return (left, text.Length);
    }

    private static DashboardRow LimiterRow(AppState state, int width)
    {
        var line = new Line();
        line.Add(" Output:   ", Muted);
        var (left, length) = AddToggle(line, AppState.Controls[16], 16, state);
        line.PadTo(width);
        return new DashboardRow(
            line.Text,
            16,
            null,
            new[] { new ToggleHit(16, left, length) });
    }

    private static DashboardRow BoxTop(string title, int inner)
    {
        var line = new Line();
        line.Add("┌─ " + title + " ", Bold + Accent);
        var remaining = inner - title.Length - 5;
        if (remaining > 0)
        {
            line.Add(new string('─', remaining), Muted);
        }

        line.Add("┐", Muted);
        line.PadTo(inner);
        return new DashboardRow(line.Text, null);
    }

    private static DashboardRow BoxBottom(int inner)
    {
        var line = new Line();
        line.Add("└" + new string('─', Math.Max(1, inner - 2)) + "┘", Muted);
        line.PadTo(inner);
        return new DashboardRow(line.Text, null);
    }

    private static DashboardRow BoxRow(DashboardRow content, int inner)
    {
        var line = new Line();
        line.Add("│", Muted);
        line.Add(" ", Reset);
        line.Raw(content.Text, inner - 4);
        line.Add(" ", Reset);
        line.Add("│", Muted);
        line.PadTo(inner);

        var slider = content.Slider is { } hit
            ? hit with { TrackLeft = hit.TrackLeft + 2 }
            : null;

        var toggles = content.Toggles?
            .Select(toggle => toggle with { Left = toggle.Left + 2 })
            .ToList();

        return new DashboardRow(line.Text, content.ControlIndex, slider, toggles);
    }

    private static DashboardRow Blank(int inner)
    {
        var line = new Line();
        line.PadTo(inner);
        return new DashboardRow(line.Text, null);
    }

    private static string MonitorBadgeText(AppState state)
        => $" [M] Monitoring: {(state.MonitorEnabled ? "ON" : "OFF")} ";

    private static string Pad(string text, int width)
        => text.Length >= width ? text[..width] : text.PadRight(width);

    private static string Clip(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= width)
        {
            return text;
        }

        if (width == 1)
        {
            return text[..1];
        }

        return text[..(width - 1)] + "…";
    }

    private static int GetWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch
        {
            return 80;
        }
    }

    private static int GetHeight()
    {
        try
        {
            return Console.WindowHeight;
        }
        catch
        {
            return 24;
        }
    }

    private static string Rgb(int r, int g, int b) => $"\x1b[38;2;{r};{g};{b}m";

    private sealed class Line
    {
        private readonly StringBuilder _builder = new();

        public int Length { get; private set; }

        public string Text => _builder.ToString();

        public void Add(string plain, string style = Reset)
        {
            if (plain.Length == 0)
            {
                return;
            }

            if (!string.IsNullOrEmpty(style) && style != Reset)
            {
                _builder.Append(style);
                _builder.Append(plain);
                _builder.Append(Reset);
            }
            else
            {
                _builder.Append(plain);
            }

            Length += plain.Length;
        }

        public void Raw(string styled, int visibleLength, string style = Reset)
        {
            if (!string.IsNullOrEmpty(style) && style != Reset)
            {
                _builder.Append(style);
                _builder.Append(styled);
                _builder.Append(Reset);
            }
            else
            {
                _builder.Append(styled);
            }

            Length += visibleLength;
        }

        public void Space(int count)
        {
            if (count > 0)
            {
                _builder.Append(' ', count);
                Length += count;
            }
        }

        public void PadTo(int width, string style = Reset)
        {
            if (Length < width)
            {
                if (!string.IsNullOrEmpty(style) && style != Reset)
                {
                    _builder.Append(style);
                    _builder.Append(' ', width - Length);
                    _builder.Append(Reset);
                }
                else
                {
                    _builder.Append(' ', width - Length);
                }

                Length = width;
            }
        }
    }
}

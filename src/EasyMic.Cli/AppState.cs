using System.Globalization;
using EasyMic.Audio;
using EasyMic.Ipc;

namespace EasyMic;

public sealed record ControlRef(TuningParameter? Parameter, TuningToggle? Toggle)
{
    public string Label => Parameter?.Label ?? Toggle?.Label ?? string.Empty;

    public bool IsToggle => Toggle is not null;

    public string Group => Parameter?.Group ?? string.Empty;
}

/// <summary>
/// UI-side state. Audio processing lives in the daemon; this class only mirrors
/// the last status snapshot and applies optimistic updates for responsiveness.
/// </summary>
public sealed class AppState
{
    public static IReadOnlyList<ControlRef> Controls { get; } = BuildControls();

    public StatusSnapshot? Status { get; private set; }
    public ProcessingSettings Processing { get; private set; } = ProcessingSettings.Default;
    public bool MonitorEnabled { get; private set; }

    public string? Notice { get; set; }
    public bool NoticeIsError { get; set; }

    public int Selected { get; set; }
    public int Scroll { get; set; }

    public bool Running => Status?.Running ?? false;
    public bool VirtualMicAvailable => Status?.VirtualMicAvailable ?? false;
    public string? SourceDevice => Status?.SourceDevice;
    public string? SinkDevice => Status?.SinkDevice;
    public int LatencyMs => Status?.LatencyMs ?? 20;
    public MeterSnapshot Meter => Status?.Meter ?? default;

    public ControlRef SelectedControl
        => Controls[Math.Clamp(Selected, 0, Controls.Count - 1)];

    public void Apply(StatusSnapshot status)
    {
        Status = status;
        Processing = status.Processing;
        MonitorEnabled = status.MonitorEnabled;
    }

    public string Format(ControlRef control)
    {
        if (control.Parameter is not null)
        {
            return control.Parameter.Format(control.Parameter.Read(Processing));
        }

        return control.Toggle!.Read(Processing) ? "AN" : "AUS";
    }

    public IpcRequest? Adjust(int direction, bool coarse)
    {
        var control = SelectedControl;

        if (control.Toggle is not null)
        {
            return SetToggle(control.Toggle, direction > 0);
        }

        var parameter = control.Parameter;
        if (parameter is null)
        {
            return null;
        }

        var step = coarse ? parameter.CoarseStep : parameter.FineStep;
        var value = parameter.Clamp(parameter.Read(Processing) + direction * step);
        Processing = parameter.Write(Processing, value);

        return new IpcRequest(
            "set",
            parameter.Id,
            value.ToString("R", CultureInfo.InvariantCulture));
    }

    public IpcRequest? ToggleSelected()
    {
        var control = SelectedControl;
        return control.Toggle is null ? null : Flip(control.Toggle);
    }

    public IpcRequest? SetValue(int controlIndex, float value)
    {
        if (controlIndex < 0 || controlIndex >= Controls.Count)
        {
            return null;
        }

        var parameter = Controls[controlIndex].Parameter;
        if (parameter is null)
        {
            return null;
        }

        var clamped = parameter.Clamp(value);
        Processing = parameter.Write(Processing, clamped);

        return new IpcRequest(
            "set",
            parameter.Id,
            clamped.ToString("R", CultureInfo.InvariantCulture));
    }

    public IpcRequest? ToggleMonitor()
    {
        MonitorEnabled = !MonitorEnabled;
        return new IpcRequest("monitor", "toggle");
    }

    public IpcRequest? Reset()
    {
        Processing = ProcessingSettings.Default;
        return new IpcRequest("reset");
    }

    private IpcRequest? Flip(TuningToggle toggle)
    {
        var value = !toggle.Read(Processing);
        return SetToggle(toggle, value);
    }

    private IpcRequest? SetToggle(TuningToggle toggle, bool value)
    {
        Processing = toggle.Write(Processing, value);
        return new IpcRequest("set", toggle.Id, value ? "on" : "off");
    }

    private static IReadOnlyList<ControlRef> BuildControls()
    {
        var controls = new List<ControlRef>();

        foreach (var parameter in ParameterCatalog.Main)
        {
            controls.Add(new ControlRef(parameter, null));
        }

        controls.Add(new ControlRef(null, ParameterCatalog.Toggles[0]));
        controls.Add(new ControlRef(null, ParameterCatalog.Toggles[1]));

        foreach (var parameter in ParameterCatalog.Details.Where(parameter => parameter.Group == "Gate-Details"))
        {
            controls.Add(new ControlRef(parameter, null));
        }

        foreach (var parameter in ParameterCatalog.Details.Where(parameter => parameter.Group == "Denoise-Details"))
        {
            controls.Add(new ControlRef(parameter, null));
        }

        controls.Add(new ControlRef(null, ParameterCatalog.Toggles[2]));
        return controls;
    }
}

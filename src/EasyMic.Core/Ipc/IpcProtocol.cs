using System.Text.Json;
using System.Text.Json.Serialization;
using EasyMic.Audio;
using EasyMic.Settings;

namespace EasyMic.Ipc;

public static class IpcProtocol
{
    public const string SocketEnvironmentVariable = "EASYMIC_SOCKET";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultSocketPath
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable(SocketEnvironmentVariable);
            return string.IsNullOrWhiteSpace(custom) ? EasyMicPaths.SocketPath : custom;
        }
    }
}

public sealed record IpcRequest(string Command, string? Name = null, string? Value = null)
{
    public static IpcRequest Status { get; } = new("status");
    public static IpcRequest Subscribe { get; } = new("subscribe");
}

public sealed record DeviceInfo(string Name, string Description)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : Description;

    public static DeviceInfo From(AudioDevice device)
        => new(device.Name, device.Description);
}

public sealed record IpcResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public StatusSnapshot? Status { get; init; }
    public AppSettings? Settings { get; init; }
    public IReadOnlyList<DeviceInfo>? Devices { get; init; }

    public static IpcResponse Success(string? message = null) => new() { Ok = true, Message = message };

    public static IpcResponse Failure(string error) => new() { Ok = false, Error = error };
}

public sealed record IpcMessage(string Type, StatusSnapshot? Status = null, IpcResponse? Response = null)
{
    public static IpcMessage FromStatus(StatusSnapshot status) => new("status", status);

    public static IpcMessage FromResponse(IpcResponse response) => new("response", Response: response);
}

public sealed record StatusSnapshot
{
    public bool Running { get; init; }
    public bool MonitorEnabled { get; init; }
    public bool VirtualMicAvailable { get; init; }
    public bool LimiterActive { get; init; }
    public string? SourceDevice { get; init; }
    public string? SinkDevice { get; init; }
    public string? VirtualDevice { get; init; }
    public string? Notice { get; init; }
    public bool NoticeIsError { get; init; }
    public int LatencyMs { get; init; }
    public ProcessingSettings Processing { get; init; } = ProcessingSettings.Default;
    public MeterSnapshot Meter { get; init; }
}

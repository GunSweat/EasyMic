namespace EasyMic.Audio;

public sealed record AudioDevice(
    int Index,
    string Name,
    string Description,
    string State,
    bool IsMonitor)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : Description;
}

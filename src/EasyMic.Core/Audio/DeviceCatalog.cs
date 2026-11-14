using System.Text.Json;

namespace EasyMic.Audio;

public static class DeviceCatalog
{
    public static async Task<IReadOnlyList<AudioDevice>> GetSourcesAsync()
        => await GetDevicesAsync("sources").ConfigureAwait(false);

    public static async Task<IReadOnlyList<AudioDevice>> GetSinksAsync()
        => await GetDevicesAsync("sinks").ConfigureAwait(false);

    public static async Task<string?> GetDefaultSourceAsync()
        => await GetDefaultAsync("get-default-source").ConfigureAwait(false);

    public static async Task<string?> GetDefaultSinkAsync()
        => await GetDefaultAsync("get-default-sink").ConfigureAwait(false);

    public static AudioDevice ResolveOrThrow(IReadOnlyList<AudioDevice> devices, string? query, string kind)
    {
        if (devices.Count == 0)
        {
            throw new InvalidOperationException($"No {kind} devices found.");
        }

        var resolved = Resolve(devices, query);
        if (resolved is not null)
        {
            return resolved;
        }

        var available = string.Join(
            Environment.NewLine,
            devices.Take(10).Select(device => $"  • {device.DisplayName}  ({device.Name})"));

        throw new InvalidOperationException(
            $"No {kind} found for \"{query}\".{Environment.NewLine}Available:{Environment.NewLine}{available}");
    }

    public static AudioDevice? Resolve(IReadOnlyList<AudioDevice> devices, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var value = query.Trim();

        var exactName = devices.FirstOrDefault(
            device => string.Equals(device.Name, value, StringComparison.OrdinalIgnoreCase));
        if (exactName is not null)
        {
            return exactName;
        }

        if (int.TryParse(value, out var index))
        {
            var byIndex = devices.FirstOrDefault(device => device.Index == index);
            if (byIndex is not null)
            {
                return byIndex;
            }
        }

        var exactDescription = devices.FirstOrDefault(
            device => string.Equals(device.Description, value, StringComparison.OrdinalIgnoreCase));
        if (exactDescription is not null)
        {
            return exactDescription;
        }

        var matches = devices
            .Where(device =>
                device.Name.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                device.Description.Contains(value, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count > 1)
        {
            var regularDevices = matches.Where(device => !device.IsMonitor).ToList();
            if (regularDevices.Count > 0)
            {
                matches = regularDevices;
            }
        }

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"\"{value}\" is ambiguous. Please use a more specific name: "
                + string.Join(", ", matches.Select(device => device.DisplayName))),
        };
    }

    private static async Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(string kind)
    {
        var result = await Pactl.RunAsync("-f", "json", "list", kind).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pactl list {kind} failed: {FirstLine(result.StdErr)}");
        }

        return Parse(result.StdOut);
    }

    private static async Task<string?> GetDefaultAsync(string command)
    {
        var result = await Pactl.RunAsync(command).ConfigureAwait(false);
        return result.ExitCode == 0 && result.StdOut.Length > 0 ? result.StdOut.Trim() : null;
    }

    private static List<AudioDevice> Parse(string json)
    {
        var devices = new List<AudioDevice>();

        if (string.IsNullOrWhiteSpace(json))
        {
            return devices;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return devices;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = GetString(element, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var description = GetString(element, "description");
            if (string.IsNullOrWhiteSpace(description))
            {
                description = name;
            }

            var isMonitor =
                name.EndsWith(".monitor", StringComparison.OrdinalIgnoreCase) ||
                GetString(element, "monitor_source").Length > 0;

            devices.Add(new AudioDevice(
                GetInt32(element, "index"),
                name,
                description,
                GetString(element, "state"),
                isMonitor));
        }

        return devices;
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var index)
            ? index
            : -1;

    private static string FirstLine(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

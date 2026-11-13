using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyMic.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path { get; }

    public SettingsStore(string? path = null)
    {
        Path = path ?? EasyMicPaths.SettingsPath;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(Path);
            var settings = await JsonSerializer
                .DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return (settings ?? new AppSettings()).Clamp();
        }
        catch
        {
            return new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = Path + ".tmp";
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer
                    .SerializeAsync(stream, settings.Clamp(), JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}

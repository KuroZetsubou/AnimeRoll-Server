using System.Text.Json;
using AnimeRoll.Server.Api.Options;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Downloads;

public sealed record QueueSettings(int MaxConcurrentDownloads = 3)
{
    public const int Min = 1, Max = 10;
}

/// Queue settings changed from the apps, kept in /data/queue-settings.json (no restart needed).
public sealed class QueueSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _lock = new();

    public QueueSettingsStore(IOptions<AnimeRollOptions> options)
    {
        _path = Path.Combine(options.Value.DataPath, "queue-settings.json");
        try { Current = File.Exists(_path) ? JsonSerializer.Deserialize<QueueSettings>(File.ReadAllText(_path), Json) ?? new() : new(); }
        catch (JsonException) { Current = new(); }
    }

    public QueueSettings Current { get; private set; }

    public QueueSettings Update(QueueSettings value)
    {
        var clamped = value with { MaxConcurrentDownloads = Math.Clamp(value.MaxConcurrentDownloads, QueueSettings.Min, QueueSettings.Max) };
        lock (_lock)
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(clamped, Json));
            Current = clamped;
        }
        return clamped;
    }
}

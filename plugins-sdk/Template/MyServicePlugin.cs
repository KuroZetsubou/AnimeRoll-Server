using AnimeRoll.Plugins;

namespace AnimeRoll.Plugin.Template;

/// <summary>
/// Skeleton for a new AnimeRoll source. Copy this folder, rename the project and the class,
/// fill in the TODOs. See PLUGINS.md for the full contract.
/// </summary>
public sealed class MyServicePlugin : ISourcePlugin
{
    private PluginContext _ctx = null!;

    // TODO: id (lowercase, stable), name shown in the apps, and what you implement.
    // Remove a capability if you don't implement that method.
    public PluginInfo Info { get; } = new(
        Id: "my-service",
        Name: "Il mio servizio",
        Capabilities: PluginCapabilities.Search | PluginCapabilities.Episodes,
        Hint: "Cerca e scarica da Il mio servizio.",
        RefPattern: null,          // e.g. @"^https://example\.com/" for download-only plugins
        Version: "0.1.0");

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        _ctx = context;
        // TODO: read settings (put secrets in the environment, not in plugin.json):
        //   AnimeRoll__Plugins__Settings__my-service__username=...
        // var user = context.RequireSetting("username");
        // Throw here if the configuration is incomplete: the source shows as "non disponibile".
        // Use context.DataDirectory to persist tokens or caches across restarts.
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        // TODO: call the service with _ctx.Http and map the results.
        // Ref is opaque to AnimeRoll: put whatever you need to find the series again.
        // Set AniListId when you know it, so downloads are linked to AniList automatically.
        await Task.CompletedTask;
        return [];
    }

    public async Task<IReadOnlyList<PluginEpisode>> GetEpisodesAsync(string seriesRef, CancellationToken ct)
    {
        // TODO: list the episodes of the series identified by seriesRef.
        await Task.CompletedTask;
        return [];
    }

    // Optional: add PluginCapabilities.Stream and return a playable URL (MP4/HLS) to watch without downloading.
    // public Task<string?> ResolveStreamUrlAsync(string episodeRef, CancellationToken ct) => ...

    public async Task<IReadOnlyList<string>> DownloadAsync(PluginDownloadRequest request, IPluginDownloadContext context, CancellationToken ct)
    {
        // TODO: download request.Ref into context.OutputDirectory and return the video file paths.
        // Report progress with context.ReportProgressAsync(0..1, "message").
        // Any format ffmpeg reads is fine: AnimeRoll converts to MP4 and moves the file into the library.
        await context.ReportProgressAsync(0, "Avvio…");
        throw new NotImplementedException("TODO: implementa il download");
    }
}

using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AnimeRoll.Plugins;

namespace AnimeRoll.Plugin.InternetArchive;

/// <summary>
/// Working example: public-domain films and cartoons from the Internet Archive (archive.org).
/// Shows search, episodes (one per video file), direct streaming and download with progress.
/// Setting "collections" (plugin.json) limits the search, default: animation and feature films.
/// </summary>
public sealed class InternetArchivePlugin : ISourcePlugin
{
    private PluginContext _ctx = null!;
    private string _collections = "animationandcartoons OR feature_films";

    public PluginInfo Info { get; } = new(
        Id: "internet-archive",
        Name: "Internet Archive",
        Capabilities: PluginCapabilities.Search | PluginCapabilities.Episodes | PluginCapabilities.Stream,
        Hint: "Film e cartoni di pubblico dominio da archive.org (plugin d'esempio).",
        Version: "1.0.0");

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _collections = context.GetSetting("collections") ?? _collections;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        var q = $"title:({query}) AND mediatype:(movies) AND collection:({_collections})";
        var url = $"https://archive.org/advancedsearch.php?q={Uri.EscapeDataString(q)}&fl[]=identifier&fl[]=title&fl[]=year&rows=30&output=json";
        var json = await _ctx.Http.GetFromJsonAsync<JsonObject>(url, ct);
        return json?["response"]?["docs"]?.AsArray().Select(d =>
        {
            var id = d!["identifier"]!.GetValue<string>();
            return new PluginSearchResult(id, d["title"]?.ToString() ?? id, $"https://archive.org/services/img/{id}", d["year"]?.ToString());
        }).ToList() ?? [];
    }

    /// Each MP4 file of the item becomes an "episode". Ref = "identifier/file name".
    public async Task<IReadOnlyList<PluginEpisode>> GetEpisodesAsync(string seriesRef, CancellationToken ct)
    {
        var json = await _ctx.Http.GetFromJsonAsync<JsonObject>($"https://archive.org/metadata/{Uri.EscapeDataString(seriesRef)}", ct);
        var files = json?["files"]?.AsArray()
            .Select(f => f!["name"]!.GetValue<string>())
            .Where(n => n.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            .Order().ToList() ?? [];
        return files.Select((name, i) => new PluginEpisode($"{seriesRef}/{name}", i + 1, Path.GetFileNameWithoutExtension(name))).ToList();
    }

    public Task<string?> ResolveStreamUrlAsync(string episodeRef, CancellationToken ct) =>
        Task.FromResult<string?>(FileUrl(episodeRef));

    public async Task<IReadOnlyList<string>> DownloadAsync(PluginDownloadRequest request, IPluginDownloadContext context, CancellationToken ct)
    {
        // A search-result ref (no "/") means "the first video of that item".
        var episodeRef = request.Ref.Contains('/') ? request.Ref
            : (await GetEpisodesAsync(request.Ref, ct)).FirstOrDefault()?.Ref
              ?? throw new InvalidOperationException("Nessun video MP4 in questo elemento.");

        using var res = await _ctx.Http.GetAsync(FileUrl(episodeRef), HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        var path = Path.Combine(context.OutputDirectory, "video.mp4");
        await using (var input = await res.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(path))
        {
            var buffer = new byte[1 << 16];
            long done = 0; var last = DateTime.MinValue; int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (DateTime.UtcNow - last > TimeSpan.FromSeconds(1))
                {
                    last = DateTime.UtcNow;
                    await context.ReportProgressAsync(total > 0 ? (double)done / total : 0, $"{done / 1048576} MB");
                }
            }
        }
        return [path];
    }

    private static string FileUrl(string episodeRef)
    {
        var slash = episodeRef.IndexOf('/');
        var id = episodeRef[..slash];
        var file = string.Join('/', episodeRef[(slash + 1)..].Split('/').Select(Uri.EscapeDataString));
        return $"https://archive.org/download/{Uri.EscapeDataString(id)}/{file}";
    }
}

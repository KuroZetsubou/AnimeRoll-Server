using AnimeRoll.Server.Api.Data;

namespace AnimeRoll.Server.Api.Providers;

public sealed record SourceSearchResult(
    string Ref, string Title, string? PosterUrl = null, string? Subtitle = null,
    int? AniListId = null, Dictionary<string, string>? Extra = null);

public sealed record SourceEpisode(string Ref, double? Number, string Title, Dictionary<string, string>? Extra = null);

public sealed record ProviderInfo(string Id, string Name, bool Enabled, bool CanSearch, bool CanListEpisodes, bool CanStream, string? Hint,
    string? RefPattern = null, bool IsPlugin = false, bool CanSubtitles = false);

/// What a provider gets to run one download job.
public sealed class DownloadContext(DownloadJob job, string workDir, Func<double, string?, Task> report)
{
    public DownloadJob Job { get; } = job;
    /// Empty temp folder for this job; files left here are imported afterwards.
    public string WorkDir { get; } = workDir;
    public Task ReportAsync(double progress01, string? message = null) => report(progress01, message);
}

/// A place anime can come from. Every provider can download to the NAS;
/// search, episode lists and direct streaming are optional.
public interface ISourceProvider
{
    string Id { get; }
    string Name { get; }
    bool Enabled { get; }
    bool CanSearch => true;
    bool CanListEpisodes => true;
    bool CanStream => false;
    /// Can fetch just the subtitle files of an episode (see FetchSubtitlesAsync).
    bool CanSubtitles => false;
    string? Hint => null;

    Task<IReadOnlyList<SourceSearchResult>> SearchAsync(string query, CancellationToken ct);
    Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string seriesRef, CancellationToken ct);

    /// Direct playable URL without downloading (only if CanStream).
    Task<string?> ResolveStreamUrlAsync(string episodeRef, CancellationToken ct) => Task.FromResult<string?>(null);

    /// Subtitle files only, into outputDirectory (only if CanSubtitles).
    Task<IReadOnlyList<AnimeRoll.Plugins.PluginSubtitle>> FetchSubtitlesAsync(string episodeRef, string outputDirectory, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AnimeRoll.Plugins.PluginSubtitle>>([]);

    /// Downloads into ctx.WorkDir and returns the produced video files.
    Task<IReadOnlyList<string>> DownloadAsync(DownloadContext ctx, CancellationToken ct);

    ProviderInfo Info => new(Id, Name, Enabled, CanSearch, CanListEpisodes, CanStream, Hint, null, false, CanSubtitles);
}

/// A source that can refuse a download request up front (e.g. a torrent that cannot work); returns the reason, or null.
public interface IRequestValidator
{
    Task<string?> ValidateAsync(IEnumerable<string> refs, AppDb db);
}

/// Built-in sources + every loaded plugin.
public sealed class ProviderRegistry(IEnumerable<ISourceProvider> providers, AnimeRoll.Server.Api.Plugins.PluginHost plugins)
{
    private readonly Dictionary<string, ISourceProvider> _map = providers.Concat(plugins.Providers)
        .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    public IEnumerable<ISourceProvider> All => _map.Values;
    public ISourceProvider? Get(string id) => _map.GetValueOrDefault(id);
}

public static class HttpDownload
{
    /// Streams to disk; a partial file left by a pause (or a restart) is resumed with a Range request.
    public static async Task<string> ToFileAsync(HttpClient http, string url, string path, DownloadContext ctx, CancellationToken ct,
        string? referer = null)
    {
        long existing = File.Exists(path) ? new FileInfo(path).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer != null) req.Headers.Referrer = new Uri(referer);
        if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) return path;   // already complete
        res.EnsureSuccessStatusCode();
        var resumed = res.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;                                                                 // server ignored Range
        var total = existing + (res.Content.Headers.ContentLength ?? 0);
        // Fail early (and clearly) when the rest of the file doesn't fit; the file lands in the work folder, then the library.
        if (res.Content.Headers.ContentLength is > 0 and var remaining)
            AnimeRoll.Server.Api.Infrastructure.DiskSpace.EnsureRoom(remaining, Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var input = await res.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1 << 16];
        long done = existing; var lastReport = DateTime.MinValue;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0 && DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(1))
            {
                lastReport = DateTime.UtcNow;
                await ctx.ReportAsync((double)done / total, $"{done / 1048576} / {total / 1048576} MB");
            }
        }
        return path;
    }
}

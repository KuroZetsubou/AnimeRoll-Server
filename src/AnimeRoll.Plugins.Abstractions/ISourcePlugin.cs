namespace AnimeRoll.Plugins;

/// <summary>
/// A source of anime episodes for AnimeRoll. Implement this in a class library that references
/// AnimeRoll.Plugins.Abstractions, drop the build output in <c>/plugins/&lt;name&gt;/</c> on the NAS
/// and restart the container. Every plugin appears in the apps as a source next to the built-in ones.
///
/// Only <see cref="Info"/> and <see cref="DownloadAsync"/> are required; declare in
/// <see cref="PluginInfo.Capabilities"/> which of the optional methods you implement.
/// </summary>
public interface ISourcePlugin
{
    /// Identity and capabilities. Read once at load time.
    PluginInfo Info { get; }

    /// Called once after loading. Read settings, log in, validate configuration.
    /// Throw to mark the plugin as not available (the message is shown in the apps).
    Task InitializeAsync(PluginContext context, CancellationToken ct) => Task.CompletedTask;

    /// <see cref="PluginCapabilities.Search"/>: find series/movies/files matching a free-text query.
    Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PluginSearchResult>>([]);

    /// <see cref="PluginCapabilities.Episodes"/>: list the episodes behind a search result
    /// (<paramref name="seriesRef"/> is a <see cref="PluginSearchResult.Ref"/>).
    Task<IReadOnlyList<PluginEpisode>> GetEpisodesAsync(string seriesRef, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PluginEpisode>>([]);

    /// <see cref="PluginCapabilities.Stream"/>: a URL the apps can play directly (MP4 or HLS)
    /// without downloading to the NAS. Return null if not available for this episode.
    Task<string?> ResolveStreamUrlAsync(string episodeRef, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <see cref="PluginCapabilities.Subtitles"/>: download only the subtitle files of one episode into
    /// <paramref name="outputDirectory"/>. Used to give episodes already in the library their original ASS subtitles.
    Task<IReadOnlyList<PluginSubtitle>> FetchSubtitlesAsync(string episodeRef, string outputDirectory, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PluginSubtitle>>([]);

    /// Download one item into <see cref="IPluginDownloadContext.OutputDirectory"/> and return the
    /// paths of the video files produced. AnimeRoll then converts them to MP4 if needed, moves them
    /// into the library and links them to AniList. Throw to fail the job (the message is shown).
    Task<IReadOnlyList<string>> DownloadAsync(PluginDownloadRequest request, IPluginDownloadContext context, CancellationToken ct);
}

[Flags]
public enum PluginCapabilities
{
    /// Download only: the user pastes a reference/URL in the apps.
    None = 0,
    Search = 1,
    Episodes = 2,
    Stream = 4,
    /// Can fetch the subtitle files of an episode on their own (no video).
    Subtitles = 8,
}

/// <param name="Id">Stable lowercase id (letters, digits, '-'). Used in URLs and settings keys.</param>
/// <param name="Name">Name shown in the apps.</param>
/// <param name="Hint">Optional one-line help shown under the source picker.</param>
/// <param name="RefPattern">Optional regex the pasted reference must match (download-only plugins).</param>
public sealed record PluginInfo(
    string Id,
    string Name,
    PluginCapabilities Capabilities,
    string? Hint = null,
    string? RefPattern = null,
    string? Version = null);

/// <param name="Ref">Opaque value AnimeRoll passes back to <c>GetEpisodesAsync</c> / <c>DownloadAsync</c>.</param>
/// <param name="AniListId">If you know it, results are linked to AniList automatically.</param>
public sealed record PluginSearchResult(
    string Ref,
    string Title,
    string? PosterUrl = null,
    string? Subtitle = null,
    int? AniListId = null,
    IReadOnlyDictionary<string, string>? Extra = null);

public sealed record PluginEpisode(
    string Ref,
    double? Number,
    string Title,
    int? AniListId = null,
    IReadOnlyDictionary<string, string>? Extra = null);

/// A subtitle file produced by <see cref="ISourcePlugin.FetchSubtitlesAsync"/>.
/// <param name="Path">Relative to the output folder.</param>
/// <param name="Language">ISO 639-2 code ("ita", "eng", "jpn").</param>
/// <param name="Format">"ass", "srt" or "vtt".</param>
public sealed record PluginSubtitle(string Path, string? Language, string? Title = null, bool Forced = false, bool IsDefault = false, string Format = "ass");

/// What the user asked to download.
/// <param name="Ref">An episode ref, a search-result ref, or whatever the user pasted.</param>
public sealed record PluginDownloadRequest(string Ref, string SeriesTitle, double? EpisodeNumber, int? AniListId);

public interface IPluginDownloadContext
{
    /// Empty folder reserved for this job. Put the downloaded files here.
    string OutputDirectory { get; }

    /// Progress 0…1 plus an optional short status shown in the apps. Cheap to call often.
    Task ReportProgressAsync(double fraction, string? message = null);
}

/// Services and configuration AnimeRoll gives to a plugin.
public sealed class PluginContext(
    string pluginDirectory,
    string dataDirectory,
    IReadOnlyDictionary<string, string> settings,
    HttpClient http,
    Action<string> log)
{
    /// Folder the plugin was loaded from (read-only by convention).
    public string PluginDirectory { get; } = pluginDirectory;

    /// Persistent writable folder for this plugin (tokens, cache…): /data/plugins/&lt;id&gt;.
    public string DataDirectory { get; } = dataDirectory;

    /// Settings from plugin.json "settings", overridden by environment variables
    /// AnimeRoll__Plugins__Settings__&lt;id&gt;__&lt;key&gt;. Put secrets in the environment, not in plugin.json.
    public IReadOnlyDictionary<string, string> Settings { get; } = settings;

    /// Shared HttpClient (no base address, infinite timeout: use the CancellationToken).
    public HttpClient Http { get; } = http;

    /// Writes to the AnimeRoll container log.
    public void Log(string message) => log(message);

    public string? GetSetting(string key) => Settings.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public string RequireSetting(string key) =>
        GetSetting(key) ?? throw new InvalidOperationException($"Impostazione mancante: {key}");
}

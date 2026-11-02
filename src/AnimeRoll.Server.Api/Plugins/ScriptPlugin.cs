using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnimeRoll.Plugins;

namespace AnimeRoll.Server.Api.Plugins;

/// <summary>
/// A plugin written in any language. AnimeRoll runs <c>command... &lt;action&gt;</c> from the plugin
/// folder, writes one JSON request on stdin and reads JSON lines from stdout:
/// <code>
/// {"type":"result","data":...}                      search / episodes / stream answer
/// {"type":"progress","fraction":0.42,"message":"…"} download progress
/// {"type":"file","path":"episode.mkv"}              a produced video (relative to outputDirectory or absolute inside it)
/// {"type":"log","message":"…"}                      written to the container log
/// {"type":"error","message":"…"}                    shown to the user; also exit with code != 0
/// </code>
/// Full contract in PLUGINS.md.
/// </summary>
public sealed class ScriptPlugin : ISourcePlugin
{
    private readonly string _dir;
    private readonly PluginManifest _m;
    private PluginContext? _context;

    public ScriptPlugin(string dir, PluginManifest manifest)
    {
        _dir = dir;
        _m = manifest;
        if (manifest.Command is not { Count: > 0 }) throw new InvalidOperationException("plugin.json: \"command\" mancante.");
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidOperationException("plugin.json: \"id\" e \"name\" sono obbligatori per i plugin script.");

        var caps = PluginCapabilities.None;
        foreach (var c in manifest.Capabilities ?? [])
            caps |= Enum.TryParse<PluginCapabilities>(c, ignoreCase: true, out var v) ? v
                : c.Equals("download", StringComparison.OrdinalIgnoreCase) ? PluginCapabilities.None
                : throw new InvalidOperationException($"plugin.json: capability sconosciuta \"{c}\".");
        Info = new PluginInfo(manifest.Id, manifest.Name, caps, manifest.Hint, manifest.RefPattern, manifest.Version);
    }

    public PluginInfo Info { get; }

    public async Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        _context = context;
        await RunAsync("init", new JsonObject(), null, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken ct) =>
        Deserialize<List<PluginSearchResult>>(await RunAsync("search", new JsonObject { ["query"] = query }, null, TimeSpan.FromSeconds(60), ct)) ?? [];

    public async Task<IReadOnlyList<PluginEpisode>> GetEpisodesAsync(string seriesRef, CancellationToken ct) =>
        Deserialize<List<PluginEpisode>>(await RunAsync("episodes", new JsonObject { ["ref"] = seriesRef }, null, TimeSpan.FromSeconds(60), ct)) ?? [];

    public async Task<IReadOnlyList<PluginSubtitle>> FetchSubtitlesAsync(string episodeRef, string outputDirectory, CancellationToken ct) =>
        Deserialize<List<PluginSubtitle>>(await RunAsync("subtitles", new JsonObject { ["ref"] = episodeRef, ["outputDirectory"] = outputDirectory },
            null, TimeSpan.FromSeconds(90), ct)) ?? [];

    public async Task<string?> ResolveStreamUrlAsync(string episodeRef, CancellationToken ct) =>
        (await RunAsync("stream", new JsonObject { ["ref"] = episodeRef }, null, TimeSpan.FromSeconds(60), ct))?.GetValue<string>();

    public async Task<IReadOnlyList<string>> DownloadAsync(PluginDownloadRequest request, IPluginDownloadContext context, CancellationToken ct)
    {
        var files = new List<string>();
        await RunAsync("download", new JsonObject
        {
            ["ref"] = request.Ref,
            ["seriesTitle"] = request.SeriesTitle,
            ["episodeNumber"] = request.EpisodeNumber,
            ["aniListId"] = request.AniListId,
            ["outputDirectory"] = context.OutputDirectory,
        }, new DownloadSink(context, files), null, ct);
        return files;
    }

    private sealed record DownloadSink(IPluginDownloadContext Context, List<string> Files);

    private static T? Deserialize<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(PluginHost.Json);

    /// Runs one action and returns the "data" of the last "result" line.
    private async Task<JsonNode?> RunAsync(string action, JsonObject request, DownloadSink? sink, TimeSpan? timeout, CancellationToken ct)
    {
        var ctx = _context ?? throw new InvalidOperationException("Plugin non inizializzato.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);

        var psi = new ProcessStartInfo(_m.Command![0])
        {
            WorkingDirectory = _dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in _m.Command.Skip(1)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(action);
        foreach (var (k, v) in ctx.Settings) psi.Environment[$"ANIMEROLL_SETTING_{k.ToUpperInvariant().Replace('-', '_')}"] = v;
        psi.Environment["ANIMEROLL_PLUGIN_DATA"] = ctx.DataDirectory;

        request["action"] = action;
        request["pluginDirectory"] = _dir;
        request["dataDirectory"] = ctx.DataDirectory;
        request["settings"] = JsonSerializer.SerializeToNode(ctx.Settings);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Impossibile avviare {_m.Command[0]}.");
        await proc.StandardInput.WriteLineAsync(request.ToJsonString());
        proc.StandardInput.Close();

        JsonNode? result = null;
        string? error = null;
        var stderrTail = new Queue<string>();

        var readErr = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync(cts.Token) is { } line)
            {
                stderrTail.Enqueue(line);
                if (stderrTail.Count > 5) stderrTail.Dequeue();
            }
        }, cts.Token);

        try
        {
            while (await proc.StandardOutput.ReadLineAsync(cts.Token) is { } line)
            {
                JsonNode? msg = null;
                if (line.TrimStart().StartsWith('{')) { try { msg = JsonNode.Parse(line); } catch (JsonException) { } }
                if (msg is null) { if (!string.IsNullOrWhiteSpace(line)) ctx.Log(line); continue; }

                switch (msg["type"]?.GetValue<string>())
                {
                    case "result": result = msg["data"]?.DeepClone(); break;
                    case "log": ctx.Log(msg["message"]?.ToString() ?? ""); break;
                    case "error": error = msg["message"]?.ToString(); break;
                    case "progress" when sink is not null:
                        await sink.Context.ReportProgressAsync(msg["fraction"]?.GetValue<double>() ?? 0, msg["message"]?.ToString());
                        break;
                    case "file" when sink is not null && msg["path"]?.ToString() is { } path:
                        sink.Files.Add(Path.IsPathRooted(path) ? path : Path.Combine(sink.Context.OutputDirectory, path));
                        break;
                }
            }
            await proc.WaitForExitAsync(cts.Token);
            await readErr;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"Il plugin {Info.Id} non ha risposto in tempo ({action}).");
        }

        if (proc.ExitCode != 0 || error is not null)
            throw new InvalidOperationException(error ?? (stderrTail.Count > 0 ? string.Join(" | ", stderrTail) : $"{action}: codice di uscita {proc.ExitCode}"));
        return result;
    }
}

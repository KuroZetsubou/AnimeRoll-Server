using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnimeRoll.Plugins;
using AnimeRoll.Server.Api.Options;
using AnimeRoll.Server.Api.Providers;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Plugins;

/// <summary>plugin.json next to the plugin files (optional for .NET plugins, required for scripts).</summary>
public sealed class PluginManifest
{
    /// "dotnet" (default) or "script".
    public string Type { get; set; } = "dotnet";
    /// .NET: the DLL to load (default: every *.dll that contains an ISourcePlugin).
    public string? Assembly { get; set; }
    /// Script: executable + fixed arguments, e.g. ["python3", "main.py"]. Run from the plugin folder.
    public List<string>? Command { get; set; }
    /// Script only (a .NET plugin describes itself in code).
    public string? Id { get; set; }
    public string? Name { get; set; }
    public List<string>? Capabilities { get; set; }
    public string? Hint { get; set; }
    public string? RefPattern { get; set; }
    public string? Version { get; set; }
    public bool Enabled { get; set; } = true;
    /// Non-secret defaults. Secrets go in the environment (AnimeRoll__Plugins__Settings__<id>__<key>).
    public Dictionary<string, string>? Settings { get; set; }
}

public sealed record PluginStatus(string Id, string Name, string Folder, string Type, string? Version, bool Loaded, string? Error);

/// Loads every plugin in /plugins at startup and exposes them as source providers.
public sealed class PluginHost(IOptions<AnimeRollOptions> options, IHttpClientFactory httpFactory, ILogger<PluginHost> log)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private readonly AnimeRollOptions _o = options.Value;
    private readonly List<ISourceProvider> _providers = [];
    private readonly List<PluginStatus> _status = [];

    public IReadOnlyList<ISourceProvider> Providers => _providers;
    public IReadOnlyList<PluginStatus> Status => _status;

    public async Task LoadAsync(CancellationToken ct)
    {
        var root = _o.Plugins.Directory;
        if (!Directory.Exists(root)) { log.LogInformation("Nessuna cartella plugin in {Dir}", root); return; }

        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith('.') || folder.StartsWith('_')) continue; // "_disabled" style folders are skipped
            PluginManifest manifest;
            try
            {
                var manifestPath = Path.Combine(dir, "plugin.json");
                manifest = File.Exists(manifestPath)
                    ? JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(manifestPath, ct), Json) ?? new()
                    : new PluginManifest();
            }
            catch (Exception e) { Fail(folder, folder, "?", $"plugin.json non valido: {e.Message}"); continue; }
            if (!manifest.Enabled) continue;

            List<ISourcePlugin> plugins;
            try
            {
                plugins = manifest.Type.Equals("script", StringComparison.OrdinalIgnoreCase)
                    ? [new ScriptPlugin(dir, manifest)]
                    : LoadDotNet(dir, manifest);
                if (plugins.Count == 0) throw new InvalidOperationException("Nessuna classe che implementa ISourcePlugin.");
            }
            catch (Exception e) { Fail(folder, folder, manifest.Type, e.Message); continue; }

            foreach (var plugin in plugins) await InitializeAsync(plugin, dir, folder, manifest, ct);
        }
        log.LogInformation("Plugin caricati: {Count}", _providers.Count(p => p.Enabled));
    }

    private async Task InitializeAsync(ISourcePlugin plugin, string dir, string folder, PluginManifest manifest, CancellationToken ct)
    {
        PluginInfo info;
        try { info = plugin.Info; }
        catch (Exception e) { Fail(folder, folder, manifest.Type, $"Info: {e.Message}"); return; }

        if (!Regex.IsMatch(info.Id, "^[a-z0-9][a-z0-9-]{1,40}$"))
        { Fail(info.Id, folder, manifest.Type, "Id non valido (minuscole, cifre e '-')."); return; }
        if (_providers.Any(p => p.Id.Equals(info.Id, StringComparison.OrdinalIgnoreCase)) || BuiltInIds.Contains(info.Id))
        { Fail(info.Id, folder, manifest.Type, "Id già usato da un'altra fonte."); return; }

        var settings = new Dictionary<string, string>(manifest.Settings ?? [], StringComparer.OrdinalIgnoreCase);
        if (_o.Plugins.Settings.TryGetValue(info.Id, out var fromEnv))
            foreach (var (k, v) in fromEnv) settings[k] = v;

        var dataDir = Path.Combine(_o.DataPath, "plugins", info.Id);
        Directory.CreateDirectory(dataDir);
        var pluginLog = log;
        var context = new PluginContext(dir, dataDir, settings, httpFactory.CreateClient("plugins"),
            msg => pluginLog.LogInformation("[plugin:{Id}] {Message}", info.Id, msg));

        string? error = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await plugin.InitializeAsync(context, timeout.Token);
        }
        catch (Exception e) { error = e.Message; log.LogWarning(e, "Plugin {Id} non inizializzato", info.Id); }

        _providers.Add(new PluginSourceProvider(plugin, info, error));
        _status.Add(new PluginStatus(info.Id, info.Name, folder, manifest.Type, info.Version, error is null, error));
    }

    private static readonly HashSet<string> BuiltInIds = new(StringComparer.OrdinalIgnoreCase) { "streamsite", "torrent", "telegram", "local" };

    private void Fail(string id, string folder, string type, string error)
    {
        log.LogWarning("Plugin {Folder} non caricato: {Error}", folder, error);
        _status.Add(new PluginStatus(id, id, folder, type, null, false, error));
    }

    private static List<ISourcePlugin> LoadDotNet(string dir, PluginManifest manifest)
    {
        var dlls = manifest.Assembly is { } a ? [Path.Combine(dir, a)] : Directory.GetFiles(dir, "*.dll");
        var result = new List<ISourcePlugin>();
        foreach (var dll in dlls)
        {
            if (Path.GetFileName(dll).StartsWith("AnimeRoll.Plugins.Abstractions", StringComparison.OrdinalIgnoreCase)) continue;
            var context = new PluginLoadContext(dll);
            Assembly asm;
            try { asm = context.LoadFromAssemblyPath(Path.GetFullPath(dll)); }
            catch (BadImageFormatException) { continue; } // native or non-.NET file
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.OfType<Type>().ToArray(); }
            foreach (var t in types.Where(t => typeof(ISourcePlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }))
                result.Add((ISourcePlugin)(Activator.CreateInstance(t)
                    ?? throw new InvalidOperationException($"{t.Name} deve avere un costruttore senza parametri.")));
        }
        return result;
    }

    /// Each plugin gets its own dependencies, but shares AnimeRoll.Plugins.Abstractions with the host
    /// (otherwise ISourcePlugin would be a different type and the cast would fail).
    private sealed class PluginLoadContext(string mainAssembly) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(ISourcePlugin).Assembly.GetName().Name) return null; // use the host's copy
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

/// Makes a plugin look like any built-in source (search, episodes, stream, download queue).
public sealed class PluginSourceProvider(ISourcePlugin plugin, PluginInfo info, string? initError) : ISourceProvider
{
    public string Id => info.Id;
    public string Name => info.Name;
    public bool Enabled => initError is null;
    public bool CanSearch => info.Capabilities.HasFlag(PluginCapabilities.Search);
    public bool CanListEpisodes => info.Capabilities.HasFlag(PluginCapabilities.Episodes);
    public bool CanStream => info.Capabilities.HasFlag(PluginCapabilities.Stream);
    public string? Hint => initError is null ? info.Hint : $"Non disponibile: {initError}";

    public ProviderInfo Info => new(Id, Name, Enabled, CanSearch, CanListEpisodes, CanStream, Hint, info.RefPattern, IsPlugin: true);

    public async Task<IReadOnlyList<SourceSearchResult>> SearchAsync(string query, CancellationToken ct) =>
        (await plugin.SearchAsync(query, ct)).Select(r => new SourceSearchResult(r.Ref, r.Title, r.PosterUrl, r.Subtitle, r.AniListId,
            r.Extra?.ToDictionary())).ToList();

    public async Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string seriesRef, CancellationToken ct) =>
        (await plugin.GetEpisodesAsync(seriesRef, ct)).Select(e =>
        {
            var extra = e.Extra?.ToDictionary() ?? [];
            if (e.AniListId is { } id) extra["anilistId"] = id.ToString(); // same key StreamSite uses
            return new SourceEpisode(e.Ref, e.Number, e.Title, extra.Count > 0 ? extra : null);
        }).ToList();

    public Task<string?> ResolveStreamUrlAsync(string episodeRef, CancellationToken ct) => plugin.ResolveStreamUrlAsync(episodeRef, ct);

    public async Task<IReadOnlyList<string>> DownloadAsync(DownloadContext ctx, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException(Hint);
        if (info.RefPattern is { } pattern && !Regex.IsMatch(ctx.Job.SourceRef, pattern))
            throw new InvalidOperationException($"Riferimento non valido per {Name}.");
        var request = new PluginDownloadRequest(ctx.Job.SourceRef, ctx.Job.SeriesTitle, ctx.Job.EpisodeNumber, ctx.Job.AniListId);
        var files = await plugin.DownloadAsync(request, new DownloadContextAdapter(ctx), ct);

        // Only accept files inside the job folder: a plugin can't import arbitrary paths from the NAS.
        var root = Path.GetFullPath(ctx.WorkDir) + Path.DirectorySeparatorChar;
        var checkedFiles = files.Select(f => Path.GetFullPath(Path.IsPathRooted(f) ? f : Path.Combine(ctx.WorkDir, f))).ToList();
        if (checkedFiles.FirstOrDefault(f => !f.StartsWith(root, StringComparison.Ordinal) || !File.Exists(f)) is { } bad)
            throw new InvalidOperationException($"Il plugin ha restituito un file non valido: {Path.GetFileName(bad)}");
        return checkedFiles;
    }

    private sealed class DownloadContextAdapter(DownloadContext ctx) : IPluginDownloadContext
    {
        public string OutputDirectory => ctx.WorkDir;
        public Task ReportProgressAsync(double fraction, string? message = null) => ctx.ReportAsync(fraction, message);
    }
}

public static class PluginEndpoints
{
    public static void MapPlugins(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/plugins", (PluginHost host) => host.Status).WithTags("Plugins").RequireAuthorization();
}

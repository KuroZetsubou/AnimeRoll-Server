using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AnimeRoll.Server.Api.Options;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Providers;

/// Crunchyroll via an external downloader that you install and configure yourself
/// (mounted at /tools in the container). AnimeRoll only launches the command,
/// reads its progress output and imports the files it produces.
///
/// Configure AnimeRoll__Crunchyroll__CommandTemplate, e.g.
///   "/tools/my-cr-downloader --url {url} --episode {episode} --output {output}"
/// SourceRef = series or episode URL pasted by the user.
public sealed partial class CrunchyrollProvider(IOptions<AnimeRollOptions> options, ILogger<CrunchyrollProvider> log) : ISourceProvider
{
    private static readonly string[] VideoExt = [".mkv", ".mp4", ".m4v"];
    private readonly CrunchyrollOptions _o = options.Value.Crunchyroll;

    public string Id => "crunchyroll";
    public string Name => "Crunchyroll";
    public bool Enabled => !string.IsNullOrWhiteSpace(_o.CommandTemplate);
    public bool CanSearch => false;
    public bool CanListEpisodes => false;
    public string? Hint => "Incolla l'URL della serie o dell'episodio. Lo scarica il tuo script configurato sul NAS.";

    public Task<IReadOnlyList<SourceSearchResult>> SearchAsync(string query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceSearchResult>>([]);

    public Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string seriesRef, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceEpisode>>([]);

    public async Task<IReadOnlyList<string>> DownloadAsync(DownloadContext ctx, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Crunchyroll non configurato (CommandTemplate vuoto).");
        if (!Uri.TryCreate(ctx.Job.SourceRef, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("crunchyroll.com"))
            throw new InvalidOperationException("Serve un URL crunchyroll.com.");

        var args = SplitArgs(_o.CommandTemplate
            .Replace("{url}", uri.ToString())
            .Replace("{output}", ctx.WorkDir)
            .Replace("{episode}", ctx.Job.EpisodeNumber?.ToString(CultureInfo.InvariantCulture) ?? ""));
        var psi = new ProcessStartInfo(args[0])
        {
            WorkingDirectory = Directory.Exists(_o.WorkingDirectory) ? _o.WorkingDirectory : ctx.WorkDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Impossibile avviare il downloader.");
        async Task Pump(StreamReader r)
        {
            while (await r.ReadLineAsync(ct) is { } line)
            {
                log.LogDebug("[cr] {Line}", line);
                if (PercentRegex().Match(line) is { Success: true } m &&
                    double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var pct))
                    await ctx.ReportAsync(pct / 100, line.Length > 120 ? line[..120] : line);
            }
        }
        await Task.WhenAll(Pump(proc.StandardOutput), Pump(proc.StandardError), proc.WaitForExitAsync(ct));
        if (proc.ExitCode != 0) throw new InvalidOperationException($"Downloader terminato con codice {proc.ExitCode}.");

        return Directory.EnumerateFiles(ctx.WorkDir, "*", SearchOption.AllDirectories)
            .Where(f => VideoExt.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f).ToList();
    }

    private static List<string> SplitArgs(string cmd) =>
        ArgRegex().Matches(cmd).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Value).ToList();

    [GeneratedRegex(@"(\d{1,3}(?:\.\d+)?)\s*%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex("\"([^\"]*)\"|\\S+")]
    private static partial Regex ArgRegex();
}

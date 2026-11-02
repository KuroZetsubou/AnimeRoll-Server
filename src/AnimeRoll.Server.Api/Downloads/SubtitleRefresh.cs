using AnimeRoll.Server.Api.Auth;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Library;
using AnimeRoll.Server.Api.Options;
using AnimeRoll.Server.Api.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Downloads;

/// <param name="Source">Provider id (e.g. crunchyroll) that can fetch subtitles on their own.</param>
/// <param name="SeriesRef">The provider's reference for the same series (a search result "ref").</param>
public sealed record SubtitleRefreshRequest(string Source, string SeriesRef);

/// "Aggiorna sottotitoli": gives episodes already in the library the original ASS subtitles from a source, without
/// downloading the video again. Each episode is one job in the download queue, so the apps show progress and errors.
public static class SubtitleRefresh
{
    public const string Source = "subtitles";

    public static void MapSubtitleRefresh(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/library").WithTags("Library").RequireAuthorization();

        g.MapPost("/series/{id:int}/subtitles", async (int id, SubtitleRefreshRequest req, HttpContext ctx, AppDb db, DownloadQueue queue,
            ProviderRegistry reg, CancellationToken ct) =>
        {
            if (reg.Get(req.Source) is not { Enabled: true, CanSubtitles: true } provider)
                return Results.BadRequest(new { error = "Questa fonte non può scaricare solo i sottotitoli." });
            var series = await db.Series.FindAsync([id], ct);
            if (series is null) return Results.NotFound();
            var eps = await db.Episodes.Where(e => e.SeriesId == id).OrderBy(e => e.Number).ToListAsync(ct);
            var remote = await provider.EpisodesAsync(req.SeriesRef, ct);

            var jobs = new List<DownloadJob>();
            var missing = new List<double>();
            foreach (var ep in eps)
            {
                var match = remote.FirstOrDefault(r => r.Number is { } n && Math.Abs(n - ep.Number) < 0.001);
                if (match is null) { missing.Add(ep.Number); continue; }
                var job = new DownloadJob
                {
                    RequestedBy = ctx.User.UserId(), Source = Source, SourceRef = $"{ep.Id}|{req.Source}|{match.Ref}",
                    SeriesTitle = series.Title, AniListId = series.AniListId, EpisodeNumber = ep.Number,
                    Message = "In coda per i sottotitoli",
                };
                db.Downloads.Add(job);
                jobs.Add(job);
            }
            await db.SaveChangesAsync(ct);
            foreach (var j in jobs) await queue.EnqueueAsync(j.Id);
            return Results.Accepted("/api/downloads", new { queued = jobs.Count, notFound = missing });
        });
    }

    /// Runs one queued job: fetch the files, convert, replace the episode's subtitle tracks. Returns the message shown in the app.
    public static async Task<string> RunAsync(IServiceProvider services, string sourceRef, string workDir, CancellationToken ct)
    {
        var parts = sourceRef.Split('|', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var episodeId)) throw new InvalidOperationException("Richiesta sottotitoli non valida.");
        var db = services.GetRequiredService<AppDb>();
        var options = services.GetRequiredService<IOptions<AnimeRollOptions>>().Value;
        var provider = services.GetRequiredService<ProviderRegistry>().Get(parts[1])
                       ?? throw new InvalidOperationException($"Fonte sconosciuta: {parts[1]}");
        var ep = await db.Episodes.FindAsync([episodeId], ct) ?? throw new InvalidOperationException("Episodio non trovato.");
        var mp4 = Path.Combine(options.MediaPath, ep.FilePath);
        if (!File.Exists(mp4)) throw new InvalidOperationException("Il file dell'episodio non esiste più sul NAS.");

        var fetched = await provider.FetchSubtitlesAsync(parts[2], workDir, ct);
        var root = Path.GetFullPath(workDir) + Path.DirectorySeparatorChar;
        var tracks = new List<(SubtitleTrack Track, string TempPath)>();
        foreach (var f in fetched)
        {
            var path = Path.GetFullPath(Path.Combine(workDir, f.Path));
            if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path)) continue;   // plugins stay inside their folder
            if (!f.Format.Equals("ass", StringComparison.OrdinalIgnoreCase)) continue;
            var i = tracks.Count;
            var vtt = Path.Combine(workDir, $"s{i}.vtt");
            var text = await File.ReadAllTextAsync(path, ct);
            await File.WriteAllTextAsync(vtt, AssSubtitles.ToWebVtt(text), ct);
            File.Copy(path, Path.ChangeExtension(vtt, ".ass"), overwrite: true);
            tracks.Add((new SubtitleTrack(i, Path.GetFileName(vtt), f.Language, f.Title, f.IsDefault, f.Forced), vtt));
        }
        if (tracks.Count == 0) throw new InvalidOperationException("La fonte non ha restituito sottotitoli ASS per questo episodio.");

        SubtitleFiles.MoveNextTo(mp4, tracks);
        return $"{tracks.Count} sottotitoli aggiornati";
    }
}

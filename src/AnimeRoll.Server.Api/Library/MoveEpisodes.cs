using System.Globalization;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Downloads;
using AnimeRoll.Server.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Library;

/// <param name="EpisodeIds">Episodes to move (they may come from different series).</param>
/// <param name="AniListId">Target series, by AniList id (created from AniList when it is not in the library yet).</param>
/// <param name="NumberOffset">Added to every episode number, e.g. -12 when "episode 13" of the old series is episode 1 of the new one.</param>
public sealed record MoveEpisodesRequest(List<int> EpisodeIds, int AniListId, double NumberOffset = 0);

/// Moves episodes to another series (wrong season, wrong entry): the files go to the new series folder, subtitles
/// and watch progress follow them, and a series left empty disappears.
public static class MoveEpisodes
{
    public static void MapMoveEpisodes(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/library").WithTags("Library").RequireAuthorization();

        g.MapPost("/episodes/move", async (MoveEpisodesRequest req, AppDb db, MediaPipeline pipeline, IOptions<AnimeRollOptions> o, CancellationToken ct) =>
        {
            if (req.EpisodeIds is not { Count: > 0 }) return Results.BadRequest(new { error = "Nessun episodio selezionato." });
            var episodes = await db.Episodes.Include(e => e.Series).Where(e => req.EpisodeIds.Contains(e.Id)).ToListAsync(ct);
            if (episodes.Count == 0) return Results.NotFound();

            var target = await pipeline.EnsureSeriesAsync("", req.AniListId, ct);
            if (string.IsNullOrWhiteSpace(target.Title))
            {
                db.Series.Remove(target); await db.SaveChangesAsync(ct);   // created empty because AniList did not answer
                return Results.BadRequest(new { error = "Serie AniList non trovata." });
            }

            // Refuse before touching any file: same number twice in the target, or a clash with an episode already there.
            var newNumbers = episodes.ToDictionary(e => e.Id, e => e.Number + req.NumberOffset);
            if (newNumbers.Values.Any(n => n <= 0 && n != 0 && n < 0)) return Results.BadRequest(new { error = "Il numero risultante non può essere negativo." });
            if (episodes.GroupBy(e => (e.IsExtra, newNumbers[e.Id])).Any(x => x.Count() > 1))
                return Results.Conflict(new { error = "Due episodi finirebbero con lo stesso numero: usa un altro scostamento." });
            var existing = await db.Episodes.Where(e => e.SeriesId == target.Id && !req.EpisodeIds.Contains(e.Id)).Select(e => new { e.Number, e.IsExtra }).ToListAsync(ct);
            var clash = episodes.Where(e => existing.Any(x => x.IsExtra == e.IsExtra && Math.Abs(x.Number - newNumbers[e.Id]) < 0.001)).Select(e => newNumbers[e.Id]).ToList();
            if (clash.Count > 0)
                return Results.Conflict(new { error = $"La serie di destinazione ha già gli episodi: {string.Join(", ", clash.Select(n => n.ToString("0.##", CultureInfo.InvariantCulture)))}." });

            var folder = Path.Combine(o.Value.MediaPath, MediaPipeline.Safe(target.Title));
            Directory.CreateDirectory(folder);
            var oldSeries = episodes.Select(e => e.SeriesId).Distinct().ToList();
            foreach (var ep in episodes)
            {
                var number = newNumbers[ep.Id];
                var oldPath = Path.Combine(o.Value.MediaPath, ep.FilePath);
                var newName = EditEpisode.FileName(target.Title, number, ep.IsExtra);
                var newPath = Path.Combine(folder, newName);
                if (File.Exists(oldPath))
                {
                    File.Move(oldPath, newPath);
                    SubtitleFiles.MoveEpisode(oldPath, newPath);
                }
                ep.SeriesId = target.Id;
                ep.Series = target;
                ep.Number = number;
                ep.FilePath = Path.GetRelativePath(o.Value.MediaPath, newPath);
            }
            target.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            // A series left without episodes goes away, folder included when empty.
            foreach (var sid in oldSeries.Where(id => id != target.Id))
            {
                if (await db.Episodes.AnyAsync(e => e.SeriesId == sid, ct)) continue;
                var old = await db.Series.FindAsync([sid], ct);
                if (old is null) continue;
                db.Series.Remove(old);
                var oldFolder = Path.Combine(o.Value.MediaPath, MediaPipeline.Safe(old.Title));
                if (Directory.Exists(oldFolder) && !Directory.EnumerateFileSystemEntries(oldFolder).Any()) Directory.Delete(oldFolder);
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { seriesId = target.Id, moved = episodes.Count });
        });
    }
}

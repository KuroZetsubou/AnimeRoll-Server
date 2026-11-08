using System.Globalization;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Downloads;
using AnimeRoll.Server.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Library;

/// <param name="Number">New episode number (null keeps it).</param>
/// <param name="IsExtra">Bonus content ("EX1"): shown apart and never counted for AniList (null keeps it).</param>
public sealed record EditEpisodeRequest(double? Number, bool? IsExtra);

/// Fixes the number of one episode by hand, or flags it as an extra. The file and its subtitles follow the new name.
public static class EditEpisode
{
    public static string FileName(string seriesTitle, double number, bool isExtra) =>
        $"{MediaPipeline.Safe(seriesTitle)} - {(isExtra ? "EX" : "E")}{number.ToString("00.##", CultureInfo.InvariantCulture)}.mp4";

    public static void MapEditEpisode(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/library").WithTags("Library").RequireAuthorization();

        g.MapPatch("/episodes/{id:int}", async (int id, EditEpisodeRequest req, AppDb db, IOptions<AnimeRollOptions> o, CancellationToken ct) =>
        {
            var ep = await db.Episodes.Include(e => e.Series).FirstOrDefaultAsync(e => e.Id == id, ct);
            if (ep is null) return Results.NotFound();
            var number = req.Number ?? ep.Number;
            var extra = req.IsExtra ?? ep.IsExtra;
            if (number < 0) return Results.BadRequest(new { error = "Il numero non può essere negativo." });
            if (Math.Abs(number - ep.Number) < 0.001 && extra == ep.IsExtra) return Results.Ok(new { ep.Number, ep.IsExtra });

            var siblings = await db.Episodes.Where(e => e.SeriesId == ep.SeriesId && e.Id != id).Select(e => new { e.Number, e.IsExtra }).ToListAsync(ct);
            if (siblings.Any(s => s.IsExtra == extra && Math.Abs(s.Number - number) < 0.001))
                return Results.Conflict(new { error = $"Esiste già {(extra ? "EX" : "l'episodio ")}{number.ToString("0.##", CultureInfo.InvariantCulture)} in questa serie." });

            var oldPath = Path.Combine(o.Value.MediaPath, ep.FilePath);
            var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, FileName(ep.Series!.Title, number, extra));
            if (File.Exists(oldPath) && !string.Equals(oldPath, newPath, StringComparison.Ordinal))
            {
                if (File.Exists(newPath)) return Results.Conflict(new { error = "Un file con quel nome esiste già." });
                File.Move(oldPath, newPath);
                SubtitleFiles.MoveEpisode(oldPath, newPath);
                ep.FilePath = Path.GetRelativePath(o.Value.MediaPath, newPath);
            }
            ep.Number = number;
            ep.IsExtra = extra;
            ep.Series.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ep.Number, ep.IsExtra });
        });
    }
}

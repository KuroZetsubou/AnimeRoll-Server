using AnimeRoll.Server.Api.AniList;
using AnimeRoll.Server.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AnimeRoll.Server.Api.Library;

/// Every field is optional: null leaves it as it is, an empty string clears a text field.
/// <param name="AniListId">Links the series to another AniList entry (0 unlinks it).</param>
/// <param name="RefreshFromAniList">Re-reads title, description, images, episodes, genres and year from AniList after saving.</param>
public sealed record EditSeriesRequest(string? Title, string? TitleRomaji, string? Description, string? PosterUrl, string? BannerUrl,
    int? TotalEpisodes, string? Genres, int? Year, int? AniListId, bool RefreshFromAniList = false);

/// Fixes the metadata of a series by hand (wrong or missing poster, wrong AniList entry, bad title).
public static class EditSeries
{
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool IsHttp(string? u) => u is null || Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme is "http" or "https";

    /// Fills in the metadata of series that have an AniList id but no poster (created while AniList was down).
    /// Returns how many were completed; stops early when AniList does not answer.
    public static async Task<int> FillMissingAsync(AppDb db, AniListClient al, ILogger log, CancellationToken ct)
    {
        var done = 0;
        var todo = await db.Series.Where(x => x.AniListId != null && (x.PosterUrl == null || x.Description == null)).ToListAsync(ct);
        foreach (var s in todo)
        {
            try
            {
                if (await al.MediaAsync(s.AniListId!.Value, ct) is not { } m) continue;
                s.TitleRomaji ??= m.TitleRomaji; s.Description ??= m.Description; s.PosterUrl ??= m.CoverUrl; s.BannerUrl ??= m.BannerUrl;
                s.TotalEpisodes ??= m.Episodes; s.Genres ??= string.Join(", ", m.Genres); s.Year ??= m.Year;
                await db.SaveChangesAsync(ct);
                done++;
                await Task.Delay(700, ct);   // gentle with AniList's rate limit
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { log.LogWarning(e, "AniList non risponde: riprovo al prossimo avvio"); break; }
        }
        return done;
    }

    public static void MapEditSeries(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/library").WithTags("Library").RequireAuthorization();

        g.MapPost("/series/fill-missing", async (AppDb db, AniListClient al, ILoggerFactory lf, CancellationToken ct) =>
            Results.Ok(new { filled = await FillMissingAsync(db, al, lf.CreateLogger("EditSeries"), ct) }));

        g.MapPatch("/series/{id:int}", async (int id, EditSeriesRequest req, AppDb db, AniListClient al, ILoggerFactory lf, CancellationToken ct) =>
        {
            var s = await db.Series.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();

            if (req.Title is not null)
            {
                if (Clean(req.Title) is not { } title) return Results.BadRequest(new { error = "Il titolo non può essere vuoto." });
                s.Title = title;
            }
            if (req.TitleRomaji is not null) s.TitleRomaji = Clean(req.TitleRomaji);
            if (req.Description is not null) s.Description = Clean(req.Description);
            if (req.PosterUrl is not null) { s.PosterUrl = Clean(req.PosterUrl); if (!IsHttp(s.PosterUrl)) return Results.BadRequest(new { error = "L'indirizzo del poster non è valido." }); }
            if (req.BannerUrl is not null) { s.BannerUrl = Clean(req.BannerUrl); if (!IsHttp(s.BannerUrl)) return Results.BadRequest(new { error = "L'indirizzo del banner non è valido." }); }
            if (req.TotalEpisodes is { } te) s.TotalEpisodes = te > 0 ? te : null;
            if (req.Genres is not null) s.Genres = Clean(req.Genres);
            if (req.Year is { } y) s.Year = y > 0 ? y : null;

            if (req.AniListId is { } newId && newId != (s.AniListId ?? 0))
            {
                if (newId < 0) return Results.BadRequest(new { error = "ID AniList non valido." });
                if (newId > 0 && await db.Series.FirstOrDefaultAsync(x => x.AniListId == newId && x.Id != id, ct) is { } other)
                    return Results.Conflict(new { error = $"AniList {newId} è già collegato a «{other.Title}». Sposta gli episodi lì, oppure scollega quella serie." });
                s.AniListId = newId > 0 ? newId : null;
            }

            if (req.RefreshFromAniList)
            {
                if (s.AniListId is not { } aid) return Results.BadRequest(new { error = "Questa serie non è collegata ad AniList." });
                AniListMedia? m;
                try { m = await al.MediaAsync(aid, ct); }
                catch (Exception e) { lf.CreateLogger("EditSeries").LogWarning(e, "Aggiornamento da AniList fallito per {Id}", aid); return Results.Json(new { error = "AniList non risponde, riprova più tardi." }, statusCode: StatusCodes.Status502BadGateway); }
                if (m is null) return Results.BadRequest(new { error = $"AniList {aid} non esiste." });
                s.Title = m.Title; s.TitleRomaji = m.TitleRomaji; s.Description = m.Description;
                s.PosterUrl = m.CoverUrl; s.BannerUrl = m.BannerUrl; s.TotalEpisodes = m.Episodes;
                s.Genres = string.Join(", ", m.Genres); s.Year = m.Year;
            }

            s.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            var count = await db.Episodes.CountAsync(e => e.SeriesId == id, ct);
            return Results.Ok(new SeriesDto(s.Id, s.AniListId, s.Title, s.TitleRomaji, s.Description, s.PosterUrl, s.BannerUrl,
                s.TotalEpisodes, s.Genres, s.Year, count, s.UpdatedAt));
        });
    }
}

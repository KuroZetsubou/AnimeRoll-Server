using AnimeRoll.Server.Api.AniList;
using AnimeRoll.Server.Api.Auth;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Tracking;

public sealed record TrackerAccount(bool Configured, bool Connected, string? UserName);
public sealed record TrackerInfo(string? Active, TrackerAccount AniList, TrackerAccount Mal);
public sealed record ActiveTrackerRequest(string Provider);

/// The list of the signed-in profile, on AniList or MyAnimeList: the same endpoints for both.
public static class TrackerEndpoints
{
    public static TrackerInfo Info(User u, AnimeRollOptions o) => new(TrackerService.ActiveId(u),
        new TrackerAccount(!string.IsNullOrEmpty(o.AniList.ClientId), TrackerService.HasAniList(u), u.AniListUserName),
        new TrackerAccount(!string.IsNullOrEmpty(o.Mal.ClientId), TrackerService.HasMal(u), u.MalUserName));

    public static void MapTracker(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tracker").WithTags("Tracker").RequireAuthorization();
        string Redirect(AnimeRollOptions o) => $"{o.PublicBaseUrl.TrimEnd('/')}/api/mal/callback";

        g.MapGet("", async (HttpContext ctx, AppDb db, IOptions<AnimeRollOptions> o) =>
            Info(await db.Users.FirstAsync(u => u.Id == ctx.User.UserId()), o.Value));

        g.MapPut("/active", async (ActiveTrackerRequest req, HttpContext ctx, AppDb db, IOptions<AnimeRollOptions> o) =>
        {
            var p = req.Provider.ToLowerInvariant();
            if (p is not ("anilist" or "mal")) return Results.BadRequest(new { error = "Provider non valido." });
            var user = await db.Users.FirstAsync(u => u.Id == ctx.User.UserId());
            if (p == "anilist" ? !TrackerService.HasAniList(user) : !TrackerService.HasMal(user))
                return Results.Conflict(new { error = "Collega prima l'account." });
            user.Tracker = p;
            await db.SaveChangesAsync();
            return Results.Ok(Info(user, o.Value));
        });

        // ---- MyAnimeList connection (AniList's one stays under /api/anilist/connect) ----
        g.MapGet("/mal/connect", (string? client, HttpContext ctx, MalClient mal, TokenService tokens, IOptions<AnimeRollOptions> o) =>
        {
            if (!mal.Configured) return Results.Problem("MyAnimeList non è configurato sul server.", statusCode: 409);
            var state = tokens.CreateState(ctx.User.UserId(), client == "web" ? "web" : null);
            return Results.Ok(new { url = mal.AuthorizeUrl(Redirect(o.Value), state, tokens.PkceVerifier(state)) });
        });

        g.MapDelete("/mal/connect", async (HttpContext ctx, AppDb db) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == ctx.User.UserId());
            user.MalAccessToken = null; user.MalRefreshToken = null; user.MalExpiresAt = null; user.MalUserName = null;
            if (user.Tracker == "mal") user.Tracker = null;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- List and entries of the active tracker ----
        async Task<(User User, ITracker? Tracker)> Resolve(HttpContext ctx, AppDb db, TrackerService svc)
        {
            var user = await db.Users.FirstAsync(u => u.Id == ctx.User.UserId());
            return (user, svc.For(user));
        }
        IResult NotConnected() => Results.Problem("Nessuna lista collegata (AniList o MyAnimeList)", statusCode: 409);

        g.MapGet("/list/{status}", async (string status, HttpContext ctx, AppDb db, TrackerService svc, CancellationToken ct) =>
        {
            var (_, t) = await Resolve(ctx, db, svc);
            if (t is null) return NotConnected();
            var s = status.ToUpperInvariant();
            if (!TrackUpdates.Statuses.Contains(s)) return Results.BadRequest(new { error = "Stato non valido." });
            return Results.Ok(await t.ListAsync(s, ct));
        });

        g.MapGet("/entry/{mediaId:int}", async (int mediaId, HttpContext ctx, AppDb db, TrackerService svc, CancellationToken ct) =>
        {
            var (_, t) = await Resolve(ctx, db, svc);
            if (t is null) return NotConnected();
            return Results.Ok(await t.EntryAsync(mediaId, ct));
        });

        // Any combination of status, episodes watched and score (0-10, 0 removes it).
        g.MapPut("/entry/{mediaId:int}", async (int mediaId, TrackUpdate body, HttpContext ctx, AppDb db, TrackerService svc, AniListClient al, CancellationToken ct) =>
        {
            var (_, t) = await Resolve(ctx, db, svc);
            if (t is null) return NotConnected();
            if (body.Status != null)
            {
                body = body with { Status = body.Status.ToUpperInvariant() };
                if (!TrackUpdates.Statuses.Contains(body.Status)) return Results.BadRequest(new { error = "Stato non valido." });
            }
            if (body.Progress is < 0 || body.Score is < 0 or > 10) return Results.BadRequest(new { error = "Valore non valido." });
            var media = await al.MediaAsync(mediaId, ct);
            try { await t.SaveAsync(mediaId, media?.Episodes, body, ct); }
            catch (InvalidOperationException e) { return Results.Problem(e.Message, statusCode: 409); }
            return Results.NoContent();
        });
    }

    /// Landing of the MyAnimeList authorization (registered as redirect URL of the MAL app).
    public static void MapMalCallback(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/mal/callback", async (string? code, string? state, AppDb db, MalClient mal, TokenService tokens,
            IOptions<AnimeRollOptions> o, CancellationToken ct) =>
        {
            var userId = tokens.ValidateState(state);
            if (userId is null || code is null || state is null) return Results.BadRequest("Stato OAuth non valido.");
            var t = await mal.ExchangeAsync(code, $"{o.Value.PublicBaseUrl.TrimEnd('/')}/api/mal/callback", tokens.PkceVerifier(state), ct);
            var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
            user.MalAccessToken = t.Access; user.MalRefreshToken = t.Refresh; user.MalExpiresAt = t.ExpiresAt;
            user.MalUserName = await mal.ViewerNameAsync(t.Access, ct);
            user.Tracker ??= "mal";
            await db.SaveChangesAsync(ct);
            return Results.Redirect(tokens.StateTag(state) == "web" ? "/profile" : "animeroll://tracker-connected");
        });
}

using AnimeRoll.Server.Api.Auth;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.AniList;

public sealed record ProgressUpdate(int Progress);
public sealed record StatusUpdate(string Status);

public static class AniListEndpoints
{
    public static void MapAniList(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/anilist").WithTags("AniList");
        string Redirect(AnimeRollOptions o) => $"{o.PublicBaseUrl.TrimEnd('/')}/api/anilist/callback";

        // Client opens this URL in a browser (ASWebAuthenticationSession on Apple).
        g.MapGet("/connect", (HttpContext ctx, AniListClient al, TokenService tokens, IOptions<AnimeRollOptions> o) =>
            Results.Ok(new { url = al.AuthorizeUrl(Redirect(o.Value), tokens.CreateState(ctx.User.UserId())) }))
            .RequireAuthorization();

        g.MapGet("/callback", async (string? code, string? state, AppDb db, AniListClient al, TokenService tokens,
            IOptions<AnimeRollOptions> o, CancellationToken ct) =>
        {
            var userId = tokens.ValidateState(state);
            if (userId is null || code is null) return Results.BadRequest("Stato OAuth non valido.");
            var token = await al.ExchangeCodeAsync(code, Redirect(o.Value), ct);
            var (id, name) = await al.ViewerAsync(token, ct);
            var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
            user.AniListToken = token;
            user.AniListUserId = id;
            user.AniListUserName = name;
            await db.SaveChangesAsync(ct);
            // Apps listen for this custom scheme to close the auth sheet.
            return Results.Redirect("animeroll://anilist-connected");
        });

        g.MapDelete("/connect", async (HttpContext ctx, AppDb db) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == ctx.User.UserId());
            user.AniListToken = null; user.AniListUserId = null; user.AniListUserName = null;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapGet("/search", (string q, AniListClient al, CancellationToken ct) => al.SearchAsync(q, ct)).RequireAuthorization();
        g.MapGet("/trending", (AniListClient al, CancellationToken ct) => al.TrendingAsync(30, ct)).RequireAuthorization();
        // Esplora: one season of one year (season = WINTER|SPRING|SUMMER|FALL), optional genre.
        g.MapGet("/browse", async (string season, int year, string? genre, AniListClient al, CancellationToken ct) =>
        {
            var s = season.ToUpperInvariant();
            if (s is not ("WINTER" or "SPRING" or "SUMMER" or "FALL")) return Results.BadRequest("Stagione non valida.");
            return Results.Ok(await al.BrowseAsync(s, year, genre, ct));
        }).RequireAuthorization();
        g.MapGet("/media/{id:int}/recommendations", (int id, AniListClient al, CancellationToken ct) => al.RecommendationsAsync(id, ct)).RequireAuthorization();
        g.MapGet("/media/{id:int}", async (int id, AniListClient al, CancellationToken ct) =>
            await al.MediaAsync(id, ct) is { } m ? Results.Ok(m) : Results.NotFound()).RequireAuthorization();

        g.MapGet("/list/{status}", async (string status, HttpContext ctx, AppDb db, AniListClient al, CancellationToken ct) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == ctx.User.UserId(), ct);
            if (user.AniListToken is null) return Results.Problem("AniList non collegato", statusCode: 409);
            return Results.Ok(await al.ListAsync(user.AniListToken, user.AniListUserId!.Value, status.ToUpperInvariant(), ct));
        }).RequireAuthorization();

        g.MapGet("/entry/{mediaId:int}", async (int mediaId, HttpContext ctx, AppDb db, AniListClient al, CancellationToken ct) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == ctx.User.UserId(), ct);
            if (user.AniListToken is null) return Results.Problem("AniList non collegato", statusCode: 409);
            return Results.Ok(await al.EntryAsync(user.AniListToken, user.AniListUserId!.Value, mediaId, ct));
        }).RequireAuthorization();

        g.MapPut("/entry/{mediaId:int}/progress", async (int mediaId, ProgressUpdate body, HttpContext ctx, AppDb db, AniListClient al, CancellationToken ct) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == ctx.User.UserId(), ct);
            if (user.AniListToken is null) return Results.Problem("AniList non collegato", statusCode: 409);
            var media = await al.MediaAsync(mediaId, ct);
            await al.SaveProgressAsync(user.AniListToken, mediaId, body.Progress, media?.Episodes, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapPut("/entry/{mediaId:int}/status", async (int mediaId, StatusUpdate body, HttpContext ctx, AppDb db, AniListClient al, CancellationToken ct) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == ctx.User.UserId(), ct);
            if (user.AniListToken is null) return Results.Problem("AniList non collegato", statusCode: 409);
            await al.SaveStatusAsync(user.AniListToken, mediaId, body.Status.ToUpperInvariant(), ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }
}

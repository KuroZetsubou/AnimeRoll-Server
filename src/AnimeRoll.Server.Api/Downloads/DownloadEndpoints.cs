using AnimeRoll.Server.Api.Auth;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Providers;
using Microsoft.EntityFrameworkCore;

namespace AnimeRoll.Server.Api.Downloads;

public sealed record DownloadItem(string SourceRef, double? EpisodeNumber);

/// "Scarica sul NAS": one request can queue a single episode or a whole season.
public sealed record DownloadRequest(string Source, string SeriesTitle, int? AniListId, List<DownloadItem> Items);

public sealed record TelegramLoginRequest(string? Code);
public sealed record TelegramChannelToggle(long Id, string Title, string? Username, bool Enabled);

public static class DownloadEndpoints
{
    public static void MapSourcesAndDownloads(this IEndpointRouteBuilder app)
    {
        var s = app.MapGroup("/api/sources").WithTags("Sources").RequireAuthorization();

        s.MapGet("/", (ProviderRegistry reg) => reg.All.Select(p => p.Info));

        s.MapGet("/{source}/search", async (string source, string q, ProviderRegistry reg, CancellationToken ct) =>
            reg.Get(source) is { Enabled: true } p ? Results.Ok(await p.SearchAsync(q, ct)) : Results.NotFound());

        s.MapGet("/{source}/episodes", async (string source, string @ref, ProviderRegistry reg, CancellationToken ct) =>
            reg.Get(source) is { Enabled: true } p ? Results.Ok(await p.EpisodesAsync(@ref, ct)) : Results.NotFound());

        // Watch directly from the source without downloading (StreamSite).
        s.MapGet("/{source}/stream", async (string source, string @ref, ProviderRegistry reg, CancellationToken ct) =>
        {
            if (reg.Get(source) is not { Enabled: true, CanStream: true } p) return Results.NotFound();
            var url = await p.ResolveStreamUrlAsync(@ref, ct);
            return url is null ? Results.NotFound() : Results.Ok(new { url });
        });

        var d = app.MapGroup("/api/downloads").WithTags("Downloads").RequireAuthorization();

        d.MapPost("/", async (DownloadRequest req, HttpContext ctx, AppDb db, DownloadQueue queue, ProviderRegistry reg) =>
        {
            if (reg.Get(req.Source) is not { Enabled: true }) return Results.BadRequest(new { error = $"Fonte '{req.Source}' non disponibile." });
            if (req.Items.Count == 0) return Results.BadRequest(new { error = "Nessun episodio selezionato." });
            var jobs = req.Items.Select(i => new DownloadJob
            {
                RequestedBy = ctx.User.UserId(), Source = req.Source.ToLowerInvariant(), SourceRef = i.SourceRef,
                SeriesTitle = req.SeriesTitle, AniListId = req.AniListId, EpisodeNumber = i.EpisodeNumber,
            }).ToList();
            db.Downloads.AddRange(jobs);
            await db.SaveChangesAsync();
            foreach (var j in jobs) await queue.EnqueueAsync(j.Id);
            return Results.Accepted("/api/downloads", jobs);
        });

        d.MapGet("/", async (AppDb db, bool? active) =>
        {
            var q = db.Downloads.AsQueryable();
            if (active == true) q = q.Where(j => j.Status == DownloadStatus.Queued || j.Status == DownloadStatus.Running || j.Status == DownloadStatus.PostProcessing);
            return await q.OrderByDescending(j => j.CreatedAt).Take(200).ToListAsync();
        });

        d.MapPost("/{id:guid}/cancel", async (Guid id, AppDb db, DownloadQueue queue) =>
        {
            if (queue.Cancel(id)) return Results.Accepted();
            var job = await db.Downloads.FindAsync(id);
            if (job is null) return Results.NotFound();
            if (job.Status == DownloadStatus.Queued) { job.Status = DownloadStatus.Cancelled; await db.SaveChangesAsync(); }
            return Results.Accepted();
        });

        d.MapPost("/{id:guid}/retry", async (Guid id, AppDb db, DownloadQueue queue) =>
        {
            var job = await db.Downloads.FindAsync(id);
            if (job is null) return Results.NotFound();
            job.Status = DownloadStatus.Queued; job.Progress = 0; job.Message = null; job.FinishedAt = null;
            await db.SaveChangesAsync();
            await queue.EnqueueAsync(id);
            return Results.Accepted();
        });

        d.MapDelete("/finished", async (AppDb db) =>
        {
            await db.Downloads.Where(j => j.Status == DownloadStatus.Completed || j.Status == DownloadStatus.Failed || j.Status == DownloadStatus.Cancelled)
                .ExecuteDeleteAsync();
            return Results.NoContent();
        });

        // ---- Telegram session & channel selection ----
        var t = app.MapGroup("/api/telegram").WithTags("Telegram").RequireAuthorization();

        t.MapGet("/status", (TelegramService tg) => new { configured = tg.Configured, loggedIn = tg.LoggedIn });

        t.MapPost("/login", async (TelegramLoginRequest req, TelegramService tg) =>
        {
            var next = await tg.LoginAsync(req.Code);
            return Results.Ok(new { next, loggedIn = next is null });
        });

        t.MapGet("/channels", async (TelegramService tg, AppDb db) =>
        {
            var selected = await db.TelegramChannels.ToDictionaryAsync(c => c.Id);
            return (await tg.ChannelsAsync()).Select(c => new TelegramChannelToggle(c.Id, c.Title, c.Username,
                selected.TryGetValue(c.Id, out var sel) && sel.Enabled));
        });

        t.MapPut("/channels", async (List<TelegramChannelToggle> channels, AppDb db) =>
        {
            foreach (var c in channels)
            {
                var row = await db.TelegramChannels.FindAsync(c.Id);
                if (row is null) db.TelegramChannels.Add(new TelegramChannel { Id = c.Id, Title = c.Title, Username = c.Username, Enabled = c.Enabled });
                else row.Enabled = c.Enabled;
            }
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

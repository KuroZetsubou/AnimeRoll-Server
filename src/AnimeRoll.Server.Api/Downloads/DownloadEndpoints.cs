using AnimeRoll.Server.Api.Auth;
using AnimeRoll.Server.Api.Infrastructure;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Options;
using Microsoft.Extensions.Options;
using AnimeRoll.Server.Api.Providers;
using Microsoft.EntityFrameworkCore;

namespace AnimeRoll.Server.Api.Downloads;

public sealed record DownloadItem(string SourceRef, double? EpisodeNumber);

/// "Scarica sul server": one request can queue a single episode or a whole season.
public sealed record DownloadRequest(string Source, string SeriesTitle, int? AniListId, List<DownloadItem> Items);

public sealed record TelegramLoginRequest(string? Code);
public sealed record PriorityRequest(int Priority);
public sealed record TelegramChannelToggle(long Id, string Title, string? Username, bool Enabled);

public static class DownloadEndpoints
{
    public const int StartNowPriority = 10;

    public static void MapSourcesAndDownloads(this IEndpointRouteBuilder app)
    {
        var s = app.MapGroup("/api/sources").WithTags("Sources").RequireAuthorization();

        s.MapGet("/", (ProviderRegistry reg) => reg.All.Select(p => p.Info));

        s.MapGet("/{source}/search", async (string source, string q, ProviderRegistry reg, CancellationToken ct) =>
            reg.Get(source) is { Enabled: true } p ? Results.Ok(await p.SearchAsync(q, ct)) : Results.NotFound());

        s.MapGet("/{source}/episodes", async (string source, string @ref, ProviderRegistry reg, CancellationToken ct) =>
            reg.Get(source) is { Enabled: true } p ? Results.Ok(await p.EpisodesAsync(@ref, ct)) : Results.NotFound());

        // Watch directly from the source without downloading.
        s.MapGet("/{source}/stream", async (string source, string @ref, ProviderRegistry reg, CancellationToken ct) =>
        {
            if (reg.Get(source) is not { Enabled: true, CanStream: true } p) return Results.NotFound();
            var url = await p.ResolveStreamUrlAsync(@ref, ct);
            return url is null ? Results.NotFound() : Results.Ok(new { url });
        });

        var d = app.MapGroup("/api/downloads").WithTags("Downloads").RequireAuthorization();

        d.MapPost("/", async (DownloadRequest req, HttpContext ctx, AppDb db, DownloadQueue queue, ProviderRegistry reg, IOptions<AnimeRollOptions> o) =>
        {
            if (reg.Get(req.Source) is not { Enabled: true }) return Results.BadRequest(new { error = $"Fonte '{req.Source}' non disponibile." });
            if (req.Items.Count == 0) return Results.BadRequest(new { error = "Nessun episodio selezionato." });
            // Refuse right away when the server is (nearly) full, and catch torrents that cannot work before queueing them.
            try { DiskSpace.EnsureRoom(0, o.Value.MediaPath, Path.Combine(o.Value.DataPath, "work")); }
            catch (InsufficientSpaceException e) { return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status507InsufficientStorage); }
            if (reg.Get(req.Source) is IRequestValidator validator)
            {
                var problem = await validator.ValidateAsync(req.Items.Select(i => i.SourceRef), db);
                if (problem is not null) return Results.BadRequest(new { error = problem });
            }
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

        // Running first, then the queue in the order it will run, then paused, then finished (newest first).
        d.MapGet("/", async (AppDb db, bool? active) =>
        {
            var q = db.Downloads.AsQueryable();
            if (active == true) q = q.Where(j => j.Status == DownloadStatus.Queued || j.Status == DownloadStatus.Running || j.Status == DownloadStatus.PostProcessing);
            var list = await q.OrderByDescending(j => j.CreatedAt).Take(200).ToListAsync();
            static int Rank(DownloadJob j) => j.Status switch
            {
                DownloadStatus.Running or DownloadStatus.PostProcessing => 0,
                DownloadStatus.Queued => 1,
                DownloadStatus.Paused => 2,
                _ => 3,
            };
            var open = list.Where(j => Rank(j) < 3).OrderBy(Rank).ThenByDescending(j => j.Priority).ThenBy(j => j.CreatedAt);
            var done = list.Where(j => Rank(j) == 3).OrderByDescending(j => j.FinishedAt ?? j.CreatedAt);
            return open.Concat(done).ToList();
        });

        d.MapPost("/{id:guid}/cancel", async (Guid id, AppDb db, DownloadQueue queue, IOptions<AnimeRollOptions> o) =>
        {
            if (queue.Cancel(id)) return Results.Accepted();
            var job = await db.Downloads.FindAsync(id);
            if (job is null) return Results.NotFound();
            if (job.Status is DownloadStatus.Queued or DownloadStatus.Paused)
            {
                job.Status = DownloadStatus.Cancelled; job.Message = "Annullato"; job.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                var workDir = Path.Combine(o.Value.DataPath, "work", id.ToString("N"));   // partial files of a paused job
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            }
            return Results.Accepted();
        });

        // Pause: a running job stops (keeping what it downloaded), a queued one is held back.
        d.MapPost("/{id:guid}/pause", async (Guid id, AppDb db, DownloadQueue queue) =>
        {
            if (queue.Pause(id)) return Results.Accepted();
            var job = await db.Downloads.FindAsync(id);
            if (job is null) return Results.NotFound();
            if (job.Status == DownloadStatus.Queued) { job.Status = DownloadStatus.Paused; job.Message = "In pausa"; await db.SaveChangesAsync(); }
            return Results.Accepted();
        });

        d.MapPost("/{id:guid}/resume", async (Guid id, AppDb db, DownloadQueue queue) =>
        {
            var job = await db.Downloads.FindAsync(id);
            if (job is null) return Results.NotFound();
            if (job.Status == DownloadStatus.Paused) { job.Status = DownloadStatus.Queued; job.Message = "In coda"; await db.SaveChangesAsync(); queue.Wake(); }
            return Results.Accepted();
        });

        d.MapPut("/{id:guid}/priority", async (Guid id, PriorityRequest req, AppDb db, DownloadQueue queue) =>
        {
            var job = await db.Downloads.FindAsync(id);
            if (job is null) return Results.NotFound();
            job.Priority = Math.Clamp(req.Priority, -1, 1);
            await db.SaveChangesAsync();
            if (queue.Running.TryGetValue(id, out var rj)) rj.Priority = job.Priority;
            queue.Wake();
            return Results.Ok(job);
        });

        // "Avvia subito": top of the queue; with no free slot the lowest-priority running job steps back
        // into the queue (it resumes by itself later, keeping what it downloaded).
        d.MapPost("/{id:guid}/start-now", async (Guid id, AppDb db, DownloadQueue queue, QueueSettingsStore settings) =>
        {
            var job = await db.Downloads.FindAsync(id);
            if (job is null) return Results.NotFound();
            job.Priority = StartNowPriority;
            if (job.Status == DownloadStatus.Paused) { job.Status = DownloadStatus.Queued; job.Message = "In coda"; }
            await db.SaveChangesAsync();
            if (job.Status == DownloadStatus.Queued && !queue.Running.ContainsKey(id)
                && queue.Running.Count >= settings.Current.MaxConcurrentDownloads)
            {
                var victim = queue.Running.Where(r => r.Value.Priority < StartNowPriority)
                    .OrderBy(r => r.Value.Priority).Select(r => (Guid?)r.Key).FirstOrDefault();
                if (victim is { } v) queue.Requeue(v);
            }
            queue.Wake();
            return Results.Accepted();
        });

        d.MapPost("/pause-all", async (AppDb db, DownloadQueue queue) =>
        {
            foreach (var id in queue.Running.Keys) queue.Pause(id);
            await db.Downloads.Where(j => j.Status == DownloadStatus.Queued)
                .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, DownloadStatus.Paused).SetProperty(j => j.Message, "In pausa"));
            return Results.Accepted();
        });

        d.MapPost("/resume-all", async (AppDb db, DownloadQueue queue) =>
        {
            await db.Downloads.Where(j => j.Status == DownloadStatus.Paused)
                .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, DownloadStatus.Queued).SetProperty(j => j.Message, "In coda"));
            queue.Wake();
            return Results.Accepted();
        });

        // Free space on the server (Profilo): disk total and free, what the library takes, and the reserve kept free.
        app.MapGet("/api/settings/storage", async (AppDb db, IOptions<AnimeRollOptions> o) =>
        {
            var disk = DiskSpace.Read(o.Value.MediaPath);
            var library = await db.Episodes.SumAsync(e => (long?)e.SizeBytes) ?? 0;
            return new StorageInfo(disk?.Total ?? 0, disk?.Free ?? 0, library, DiskSpace.ReserveBytes);
        }).WithTags("Downloads").RequireAuthorization();

        var settingsGroup = app.MapGroup("/api/settings/queue").WithTags("Downloads").RequireAuthorization();
        settingsGroup.MapGet("/", (QueueSettingsStore s) => s.Current);
        settingsGroup.MapPut("/", (QueueSettings req, QueueSettingsStore s, DownloadQueue queue) =>
        {
            var saved = s.Update(req);
            queue.Wake();   // more slots: start waiting jobs now (fewer: running ones finish, none are killed)
            return Results.Ok(saved);
        });

        d.MapPost("/{id:guid}/retry", async (Guid id, AppDb db, DownloadQueue queue) =>
        {
            var job = await db.Downloads.FindAsync(id);
            if (job is null) return Results.NotFound();
            if (queue.Running.ContainsKey(id)) return Results.Conflict(new { error = "Il lavoro è già in corso." });
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

using AnimeRoll.Server.Api.Auth;
using AnimeRoll.Server.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AnimeRoll.Server.Api.Downloads;

/// Repairs run through the download queue, so the apps show them in "Download" with progress and errors.
public static class RepairJob
{
    public const string Source = "repair";

    public static void MapRepair(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/library").WithTags("Library").RequireAuthorization();

        g.MapPost("/episodes/{id:int}/repair", async (int id, HttpContext ctx, AppDb db, DownloadQueue queue) =>
        {
            var ep = await db.Episodes.Include(e => e.Series).FirstOrDefaultAsync(e => e.Id == id);
            if (ep is null) return Results.NotFound();
            var job = await EnqueueAsync(ep, ctx.User.UserId(), db, queue);
            return Results.Accepted("/api/downloads", job);
        });

        g.MapPost("/series/{id:int}/repair", async (int id, HttpContext ctx, AppDb db, DownloadQueue queue) =>
        {
            var eps = await db.Episodes.Include(e => e.Series).Where(e => e.SeriesId == id).OrderBy(e => e.Number).ToListAsync();
            if (eps.Count == 0) return Results.NotFound();
            var jobs = new List<DownloadJob>();
            foreach (var ep in eps) jobs.Add(await EnqueueAsync(ep, ctx.User.UserId(), db, queue));
            return Results.Accepted("/api/downloads", jobs);
        });
    }

    private static async Task<DownloadJob> EnqueueAsync(Episode ep, Guid userId, AppDb db, DownloadQueue queue)
    {
        // One repair per episode at a time.
        var running = await db.Downloads.FirstOrDefaultAsync(j => j.Source == Source && j.SourceRef == ep.Id.ToString()
            && (j.Status == DownloadStatus.Queued || j.Status == DownloadStatus.Running || j.Status == DownloadStatus.PostProcessing));
        if (running is not null) return running;

        var job = new DownloadJob
        {
            RequestedBy = userId, Source = Source, SourceRef = ep.Id.ToString(),
            SeriesTitle = ep.Series!.Title, AniListId = ep.Series.AniListId, EpisodeNumber = ep.Number,
            Message = "In coda per la riparazione",
        };
        db.Downloads.Add(job);
        await db.SaveChangesAsync();
        await queue.EnqueueAsync(job.Id);
        return job;
    }
}

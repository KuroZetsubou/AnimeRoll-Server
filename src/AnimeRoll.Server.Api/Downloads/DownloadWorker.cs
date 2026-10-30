using System.Collections.Concurrent;
using System.Threading.Channels;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Options;
using AnimeRoll.Server.Api.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Downloads;

/// In-process queue. Jobs are persisted in SQLite, so queued/running jobs
/// are picked up again after a container restart.
public sealed class DownloadQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public readonly ConcurrentDictionary<Guid, CancellationTokenSource> Running = new();
    public ValueTask EnqueueAsync(Guid id) => _channel.Writer.WriteAsync(id);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    public bool Cancel(Guid id)
    {
        if (!Running.TryGetValue(id, out var cts)) return false;
        cts.Cancel();
        return true;
    }
}

public sealed class DownloadWorker(DownloadQueue queue, IServiceScopeFactory scopes, IOptions<AnimeRollOptions> options,
    ILogger<DownloadWorker> log) : BackgroundService
{
    /// Parallel downloads (torrent jobs mostly wait on qBittorrent).
    private const int Parallelism = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var pending = await db.Downloads
                .Where(d => d.Status == DownloadStatus.Queued || d.Status == DownloadStatus.Running || d.Status == DownloadStatus.PostProcessing)
                .OrderBy(d => d.CreatedAt).Select(d => d.Id).ToListAsync(stoppingToken);
            foreach (var id in pending) await queue.EnqueueAsync(id);
        }

        await Parallel.ForEachAsync(queue.ReadAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = stoppingToken },
            async (id, ct) => await RunJobAsync(id, ct));
    }

    private async Task RunJobAsync(Guid id, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        queue.Running[id] = cts;
        var ct = cts.Token;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var job = await db.Downloads.FindAsync([id], ct);
        if (job is null || job.Status is DownloadStatus.Completed or DownloadStatus.Cancelled) { queue.Running.TryRemove(id, out _); return; }

        var workDir = Path.Combine(options.Value.DataPath, "work", id.ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var provider = scope.ServiceProvider.GetRequiredService<ProviderRegistry>().Get(job.Source)
                           ?? throw new InvalidOperationException($"Fonte sconosciuta: {job.Source}");
            job.Status = DownloadStatus.Running; job.Message = "Avvio…";
            await db.SaveChangesAsync(ct);

            // Providers may report from several tasks at once (or fire-and-forget): the DbContext is not
            // thread-safe, so only one save runs at a time and a report that finds it busy just skips saving.
            var saveGate = new SemaphoreSlim(1, 1);
            var lastSave = DateTime.MinValue;
            var context = new DownloadContext(job, workDir, async (p, msg) =>
            {
                job.Progress = Math.Clamp(double.IsFinite(p) ? p : 0, 0, 1);
                job.Message = msg;
                if (DateTime.UtcNow - lastSave < TimeSpan.FromSeconds(2) || !saveGate.Wait(0)) return;
                try
                {
                    lastSave = DateTime.UtcNow;
                    await db.SaveChangesAsync(CancellationToken.None);
                }
                finally { saveGate.Release(); }
            });

            IReadOnlyList<string> files;
            try { files = await provider.DownloadAsync(context, ct); }
            finally { await saveGate.WaitAsync(CancellationToken.None); } // late reports stop saving from here on
            if (files.Count == 0) throw new InvalidOperationException("Nessun file video prodotto.");

            job.Status = DownloadStatus.PostProcessing; job.Message = "Conversione e importazione…";
            await db.SaveChangesAsync(ct);
            var pipeline = scope.ServiceProvider.GetRequiredService<MediaPipeline>();
            var episodes = await pipeline.ImportAsync(files, job.SeriesTitle, job.AniListId, job.EpisodeNumber, job.Source, ct);

            job.Status = DownloadStatus.Completed; job.Progress = 1;
            job.EpisodeId = episodes.FirstOrDefault()?.Id;
            job.Message = episodes.Count == 1 ? "Completato" : $"Importati {episodes.Count} episodi";
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            job.Status = DownloadStatus.Cancelled; job.Message = "Annullato";
        }
        catch (OperationCanceledException)
        {
            return; // shutting down: keep status so it resumes on restart
        }
        catch (Exception e)
        {
            log.LogError(e, "Download {Id} fallito", id);
            job.Status = DownloadStatus.Failed; job.Message = e.Message;
        }
        finally
        {
            queue.Running.TryRemove(id, out _);
        }

        job.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        try { Directory.Delete(workDir, true); } catch { /* best effort */ }
    }
}

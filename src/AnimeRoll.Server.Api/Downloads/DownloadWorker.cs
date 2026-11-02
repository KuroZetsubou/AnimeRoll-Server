using System.Collections.Concurrent;
using System.Threading.Channels;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Options;
using AnimeRoll.Server.Api.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Downloads;

/// Why a running job was stopped from outside.
public enum StopReason { None, Cancel, Pause, Requeue }

public sealed class RunningJob(CancellationTokenSource cts, int priority)
{
    public CancellationTokenSource Cts { get; } = cts;
    public int Priority { get; set; } = priority;
    public volatile StopReason Reason;
}

/// Shared state between the API and the scheduler. Jobs live in SQLite (so they survive restarts);
/// this only tracks what is running right now and wakes the scheduler when something changes.
public sealed class DownloadQueue
{
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public readonly ConcurrentDictionary<Guid, RunningJob> Running = new();

    /// New job, changed priority or settings: let the scheduler look at the queue now.
    public ValueTask EnqueueAsync(Guid _) { Wake(); return ValueTask.CompletedTask; }
    public void Wake() => _wake.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan max, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(max);
        try { await _wake.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* periodic check */ }
    }

    public bool Cancel(Guid id) => Stop(id, StopReason.Cancel);
    public bool Pause(Guid id) => Stop(id, StopReason.Pause);
    public bool Requeue(Guid id) => Stop(id, StopReason.Requeue);

    private bool Stop(Guid id, StopReason reason)
    {
        if (!Running.TryGetValue(id, out var job)) return false;
        job.Reason = reason;
        job.Cts.Cancel();
        return true;
    }
}

/// Picks queued jobs by priority (then age) and runs up to the configured number at once.
public sealed class DownloadWorker(DownloadQueue queue, IServiceScopeFactory scopes, IOptions<AnimeRollOptions> options,
    QueueSettingsStore settings, ILogger<DownloadWorker> log) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Task> _tasks = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
        {
            // Jobs interrupted by a restart go back in the queue (paused ones stay paused).
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await db.Downloads.Where(d => d.Status == DownloadStatus.Running || d.Status == DownloadStatus.PostProcessing)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DownloadStatus.Queued), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await FillSlotsAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Scheduler download"); }
            await queue.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
        }
        // Shutdown: running jobs see the cancelled token and keep their status, so they resume on restart.
        await Task.WhenAny(Task.WhenAll(_tasks.Values), Task.Delay(TimeSpan.FromSeconds(10)));
    }

    private async Task FillSlotsAsync(CancellationToken stoppingToken)
    {
        var free = settings.Current.MaxConcurrentDownloads - queue.Running.Count;
        if (free <= 0) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var running = queue.Running.Keys.ToList();
        var next = await db.Downloads
            .Where(d => d.Status == DownloadStatus.Queued && !running.Contains(d.Id))
            .OrderByDescending(d => d.Priority).ThenBy(d => d.CreatedAt)
            .Select(d => new { d.Id, d.Priority })
            .Take(free).ToListAsync(stoppingToken);

        foreach (var j in next)
        {
            var rj = new RunningJob(CancellationTokenSource.CreateLinkedTokenSource(stoppingToken), j.Priority);
            if (!queue.Running.TryAdd(j.Id, rj)) continue;
            _tasks[j.Id] = Task.Run(async () =>
            {
                try { await RunJobAsync(j.Id, rj, stoppingToken); }
                finally
                {
                    queue.Running.TryRemove(j.Id, out _);
                    _tasks.TryRemove(j.Id, out _);
                    rj.Cts.Dispose();
                    queue.Wake();      // a slot just freed up
                }
            }, CancellationToken.None);
        }
    }

    private async Task RunJobAsync(Guid id, RunningJob rj, CancellationToken stoppingToken)
    {
        var ct = rj.Cts.Token;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var job = await db.Downloads.FindAsync([id], CancellationToken.None);
        if (job is null || job.Status != DownloadStatus.Queued) return;

        // Kept on pause so a resumed download continues from the partial file.
        var workDir = Path.Combine(options.Value.DataPath, "work", id.ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            if (job.Source == SubtitleRefresh.Source)
            {
                job.Status = DownloadStatus.PostProcessing; job.Message = "Scarico i sottotitoli…"; job.Progress = 0.1;
                await db.SaveChangesAsync(ct);
                job.Message = await SubtitleRefresh.RunAsync(scope.ServiceProvider, job.SourceRef, workDir, ct);
                job.Status = DownloadStatus.Completed; job.Progress = 1;
                job.EpisodeId = int.Parse(job.SourceRef.Split('|')[0]);
                // Crunchyroll limits playback requests: a short pause between episodes.
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            else if (job.Source == RepairJob.Source)
            {
                // "Ripara episodio" requested from a client: same pipeline as a new download, on the file already in the library.
                job.Status = DownloadStatus.PostProcessing; job.Message = "Riparazione in corso…"; job.Progress = 0.1;
                await db.SaveChangesAsync(ct);
                job.Message = await scope.ServiceProvider.GetRequiredService<MediaPipeline>()
                    .RepairAsync(int.Parse(job.SourceRef), workDir, ct);
                job.Status = DownloadStatus.Completed; job.Progress = 1;
                job.EpisodeId = int.Parse(job.SourceRef);
            }
            else
            {
                var provider = scope.ServiceProvider.GetRequiredService<ProviderRegistry>().Get(job.Source)
                               ?? throw new InvalidOperationException($"Fonte sconosciuta: {job.Source}");
                job.Status = DownloadStatus.Running; job.Message = job.Progress > 0 ? "Ripresa…" : "Avvio…";
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
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return; // shutting down: status stays Running/PostProcessing and goes back to Queued on restart
        }
        catch (OperationCanceledException) when (rj.Reason is StopReason.Pause or StopReason.Requeue)
        {
            job.Status = rj.Reason == StopReason.Pause ? DownloadStatus.Paused : DownloadStatus.Queued;
            job.Message = rj.Reason == StopReason.Pause ? "In pausa" : "In attesa: precedenza a un altro lavoro";
            await db.SaveChangesAsync(CancellationToken.None);
            return;   // keep workDir: resuming continues from what was already downloaded
        }
        catch (OperationCanceledException)
        {
            job.Status = DownloadStatus.Cancelled; job.Message = "Annullato";
        }
        catch (Exception e)
        {
            log.LogError(e, "Download {Id} fallito", id);
            job.Status = DownloadStatus.Failed; job.Message = e.Message;
        }

        job.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        try { Directory.Delete(workDir, true); } catch { /* best effort */ }
    }
}

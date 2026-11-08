using AnimeRoll.Server.Api.Infrastructure;
using AnimeRoll.Server.Api.AniList;
using AnimeRoll.Server.Api.Auth;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Downloads;
using AnimeRoll.Server.Api.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Library;

public sealed record SeriesDto(int Id, int? AniListId, string Title, string? TitleRomaji, string? Description, string? PosterUrl,
    string? BannerUrl, int? TotalEpisodes, string? Genres, int? Year, int EpisodeCount, DateTime UpdatedAt);

public sealed record EpisodeDto(int Id, int SeriesId, double Number, string? Title, long SizeBytes, double? DurationSeconds,
    string Source, double? PositionSeconds, bool Completed, bool IsExtra = false);

public sealed record ContinueWatchingDto(SeriesDto Series, EpisodeDto Episode);

public sealed record HomeDto(
    List<ContinueWatchingDto> ContinueWatching,
    List<SeriesDto> RecentlyAdded,
    List<AniListEntry> AniListWatching,
    List<AniListMedia> Trending,
    List<AniListMedia> Seasonal);

public sealed record ProgressRequest(double PositionSeconds, double? DurationSeconds);

public sealed record SubtitleInfo(int Index, string? Language, string Label, bool IsDefault, bool IsForced, bool IsSignsOnly, string Url, string? AssUrl = null);

public sealed record StreamInfo(string Url, string DownloadUrl, double? DurationSeconds, double? ResumeAt, List<SubtitleInfo> Subtitles, List<string>? Fonts = null, List<AudioInfo>? AudioTracks = null);
public sealed record AudioInfo(int Index, string? Language, string Label, string Url);

public static class LibraryEndpoints
{
    /// Watched past this fraction → episode completed → AniList progress updated.
    private const double CompletedThreshold = 0.85;

    private static SeriesDto ToDto(Series s, int count) =>
        new(s.Id, s.AniListId, s.Title, s.TitleRomaji, s.Description, s.PosterUrl, s.BannerUrl, s.TotalEpisodes, s.Genres, s.Year, count, s.UpdatedAt);

    private static EpisodeDto ToDto(Episode e, WatchProgress? p) =>
        new(e.Id, e.SeriesId, e.Number, e.Title, e.SizeBytes, e.DurationSeconds, e.Source, p?.PositionSeconds, p?.Completed ?? false, e.IsExtra);

    public static void MapLibrary(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/library").WithTags("Library").RequireAuthorization();

        g.MapGet("/home", async (HttpContext ctx, AppDb db, AniListClient al, ILoggerFactory lf, CancellationToken ct) =>
        {
            var uid = ctx.User.UserId();
            var user = await db.Users.FirstAsync(u => u.Id == uid, ct);
            var log = lf.CreateLogger("Home");

            var progress = await db.WatchProgress.Include(w => w.Episode).ThenInclude(e => e!.Series)
                .Where(w => w.UserId == uid && !w.Completed).OrderByDescending(w => w.UpdatedAt).Take(20).ToListAsync(ct);
            var cont = progress.GroupBy(p => p.Episode!.SeriesId).Select(g => g.First())
                .Select(p => new ContinueWatchingDto(ToDto(p.Episode!.Series!, 0), ToDto(p.Episode!, p))).ToList();

            var recent = await db.Series.OrderByDescending(s => s.UpdatedAt).Take(30)
                .Select(s => new { s, c = s.Episodes.Count }).ToListAsync(ct);

            async Task<List<T>> Safe<T>(Func<Task<List<T>>> f)
            {
                try { return await f(); } catch (Exception e) { log.LogWarning(e, "AniList non raggiungibile"); return []; }
            }
            var watchingTask = user.AniListToken is null ? Task.FromResult(new List<AniListEntry>())
                : Safe(() => al.ListAsync(user.AniListToken, user.AniListUserId!.Value, "CURRENT", ct));
            var trendingTask = Safe(() => al.TrendingAsync(20, ct));
            var seasonalTask = Safe(() => al.SeasonalAsync(20, ct));
            await Task.WhenAll(watchingTask, trendingTask, seasonalTask);

            return new HomeDto(cont, recent.Select(x => ToDto(x.s, x.c)).ToList(), watchingTask.Result, trendingTask.Result, seasonalTask.Result);
        });

        g.MapGet("/series", async (AppDb db, string? q) =>
        {
            var query = db.Series.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(s => EF.Functions.Like(s.Title, $"%{q}%") || EF.Functions.Like(s.TitleRomaji!, $"%{q}%"));
            return (await query.OrderBy(s => s.Title).Select(s => new { s, c = s.Episodes.Count }).ToListAsync()).Select(x => ToDto(x.s, x.c));
        });

        g.MapGet("/series/{id:int}", async (int id, AppDb db) =>
            await db.Series.Where(s => s.Id == id).Select(s => new { s, c = s.Episodes.Count }).FirstOrDefaultAsync() is { } x
                ? Results.Ok(ToDto(x.s, x.c)) : Results.NotFound());

        g.MapGet("/by-anilist/{aniListId:int}", async (int aniListId, AppDb db) =>
            await db.Series.Where(s => s.AniListId == aniListId).Select(s => new { s, c = s.Episodes.Count }).FirstOrDefaultAsync() is { } x
                ? Results.Ok(ToDto(x.s, x.c)) : Results.NotFound());

        g.MapGet("/series/{id:int}/episodes", async (int id, HttpContext ctx, AppDb db) =>
        {
            var uid = ctx.User.UserId();
            var eps = await db.Episodes.Where(e => e.SeriesId == id).OrderBy(e => e.IsExtra).ThenBy(e => e.Number).ToListAsync();
            var ids = eps.Select(e => e.Id).ToList();
            var prog = await db.WatchProgress.Where(w => w.UserId == uid && ids.Contains(w.EpisodeId)).ToDictionaryAsync(w => w.EpisodeId);
            return eps.Select(e => ToDto(e, prog.GetValueOrDefault(e.Id)));
        });

        // Signed URLs usable by AVPlayer / ExoPlayer / <video> and by background downloads on the device.
        g.MapGet("/episodes/{id:int}/stream", async (int id, HttpContext ctx, AppDb db, TokenService tokens, IOptions<AnimeRollOptions> o, MediaPipeline pipeline, CancellationToken ct) =>
        {
            var uid = ctx.User.UserId();
            var ep = await db.Episodes.FindAsync(id);
            if (ep is null) return Results.NotFound();
            var p = await db.WatchProgress.FirstOrDefaultAsync(w => w.UserId == uid && w.EpisodeId == id);
            var token = Uri.EscapeDataString(tokens.CreateStreamToken(uid, id));
            var baseUrl = o.Value.PublicBaseUrl.TrimEnd('/');
            var mp4Path = Path.Combine(o.Value.MediaPath, ep.FilePath);
            var subs = (SubtitleFiles.Load(mp4Path) ?? [])
                .Select(s => new SubtitleInfo(s.Index, s.Language, s.Label, s.IsDefault, s.IsForced, s.IsSignsOnly,
                    $"{baseUrl}/media/episodes/{id}/subtitles/{s.Index}.vtt?t={token}",
                    s.AssFile is null ? null : $"{baseUrl}/media/episodes/{id}/subtitles/{s.Index}.ass?t={token}"))
                .ToList();
            var fontsDir = SubtitleFiles.FontsDir(mp4Path);
            var fonts = Directory.Exists(fontsDir)
                ? Directory.GetFiles(fontsDir).Select(f => $"{baseUrl}/media/episodes/{id}/fonts/{Uri.EscapeDataString(Path.GetFileName(f))}?t={token}").ToList()
                : [];
            var audioProbe = await pipeline.ProbeAudioAsync(mp4Path, ct);
            var audio = audioProbe.Count < 2 ? null : audioProbe.Select((a, i) => new AudioInfo(i, a.Language,
                string.IsNullOrWhiteSpace(a.Title) ? (a.Language?.ToUpperInvariant() ?? $"Traccia {i + 1}") : a.Title!,
                i == 0 ? $"{baseUrl}/media/episodes/{id}?t={token}" : $"{baseUrl}/media/episodes/{id}/audio/{i}.m4a?t={token}")).ToList();
            if (audio is { Count: > 1 })
            {
                // Extract the other dubs in the background so they are ready by the time someone opens the audio menu.
                var scopes = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
                var count = audio.Count;
                _ = Task.Run(async () =>
                {
                    using var scope = scopes.CreateScope();
                    var pl = scope.ServiceProvider.GetRequiredService<MediaPipeline>();
                    for (var i = 1; i < count; i++) { try { await pl.ExtractAudioAsync(mp4Path, id, i, CancellationToken.None); } catch { /* best effort */ } }
                });
            }
            return Results.Ok(new StreamInfo(
                $"{baseUrl}/media/episodes/{id}?t={token}",
                $"{baseUrl}/media/episodes/{id}?t={token}&download=1",
                ep.DurationSeconds, p is { Completed: false } ? p.PositionSeconds : null, subs, fonts, audio));
        });

        g.MapPut("/episodes/{id:int}/progress", async (int id, ProgressRequest req, HttpContext ctx, AppDb db, AniListClient al,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var uid = ctx.User.UserId();
            var ep = await db.Episodes.Include(e => e.Series).FirstOrDefaultAsync(e => e.Id == id, ct);
            if (ep is null) return Results.NotFound();
            // Atomic "create if missing" on the unique (UserId, EpisodeId) index: two saves arriving together
            // (the player closing, a periodic save) can't both insert the row. Same TEXT format EF uses for Guid.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT OR IGNORE INTO WatchProgress (UserId, EpisodeId, PositionSeconds, Completed, SyncedToAniList, UpdatedAt)
                VALUES ({uid.ToString().ToUpperInvariant()}, {id}, 0, 0, 0, {DateTime.UtcNow})
                """, ct);
            var p = await db.WatchProgress.FirstAsync(w => w.UserId == uid && w.EpisodeId == id, ct);
            p.PositionSeconds = req.PositionSeconds;
            p.DurationSeconds = req.DurationSeconds ?? ep.DurationSeconds;
            p.UpdatedAt = DateTime.UtcNow;
            if (p.DurationSeconds > 0 && req.PositionSeconds / p.DurationSeconds >= CompletedThreshold) p.Completed = true;

            var syncedNow = false;
            if (p.Completed && !p.SyncedToAniList && !ep.IsExtra && ep.Series!.AniListId is { } mediaId)
            {
                var user = await db.Users.FirstAsync(u => u.Id == uid, ct);
                if (user.AniListToken != null)
                {
                    try
                    {
                        // Never move AniList progress backwards when rewatching an old episode.
                        var entry = await al.EntryAsync(user.AniListToken, user.AniListUserId!.Value, mediaId, ct);
                        var newProgress = (int)Math.Floor(ep.Number);
                        if (entry is null || entry.Progress < newProgress)
                            await al.SaveProgressAsync(user.AniListToken, mediaId, newProgress, ep.Series.TotalEpisodes, ct);
                        p.SyncedToAniList = true; syncedNow = true;
                    }
                    catch (Exception e) { lf.CreateLogger("Progress").LogWarning(e, "Sync AniList fallito"); }
                }
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { p.Completed, aniListSynced = syncedNow });
        });

        // Delete an episode from the NAS: the file, its subtitles and everyone's progress. A series left
        // without episodes disappears too (its folder as well, when empty).
        g.MapDelete("/episodes/{id:int}", async (int id, AppDb db, IOptions<AnimeRollOptions> o) =>
        {
            var ep = await db.Episodes.FindAsync(id);
            if (ep is null) return Results.NotFound();
            await RemoveEpisodesAsync(db, o.Value, [ep]);
            return Results.NoContent();
        });

        // Delete a whole series (to free space): every episode, file and subtitle.
        g.MapDelete("/series/{id:int}", async (int id, AppDb db, IOptions<AnimeRollOptions> o) =>
        {
            var series = await db.Series.Include(s => s.Episodes).FirstOrDefaultAsync(s => s.Id == id);
            if (series is null) return Results.NotFound();
            await RemoveEpisodesAsync(db, o.Value, series.Episodes.ToList());
            return Results.NoContent();
        });

        // ---- Local files: upload from any client ----
        g.MapPost("/upload", async ([FromForm] IFormFile file, [FromForm] string seriesTitle, [FromForm] int? aniListId,
            [FromForm] double? episodeNumber, MediaPipeline pipeline, IOptions<AnimeRollOptions> o, CancellationToken ct) =>
        {
            // Temp copy in the work folder, then the converted file in the library: refuse before copying anything.
            try { DiskSpace.EnsureRoom(file.Length, Path.Combine(o.Value.DataPath, "work"), o.Value.MediaPath); }
            catch (InsufficientSpaceException e) { return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status507InsufficientStorage); }
            var dir = Path.Combine(o.Value.DataPath, "work", "upload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, MediaPipeline.Safe(Path.GetFileName(file.FileName)));
            try
            {
                await using (var fs = File.Create(tmp)) await file.CopyToAsync(fs, ct);
                var eps = await pipeline.ImportAsync([tmp], seriesTitle, aniListId, episodeNumber, "local", ct);
                return Results.Ok(eps.Select(e => ToDto(e, null)));
            }
            catch (InsufficientSpaceException e)
            {
                return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status507InsufficientStorage);
            }
            catch (InvalidOperationException e)
            {
                // Rejected file (e.g. audio/video out of sync): tell the user why.
                return Results.UnprocessableEntity(new { error = e.Message });
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(20L * 1024 * 1024 * 1024));

        // ---- Local files: import a folder already on the NAS (/media/inbox) ----
        g.MapPost("/scan-inbox", async (AppDb db, MediaPipeline pipeline, IOptions<AnimeRollOptions> o, CancellationToken ct) =>
        {
            var inbox = Path.Combine(o.Value.MediaPath, "_inbox");
            Directory.CreateDirectory(inbox);
            var imported = 0;
            // Layout: _inbox/{Series Title}/files...  (optional "[anilist-12345]" in folder name)
            foreach (var dir in Directory.GetDirectories(inbox))
            {
                var name = Path.GetFileName(dir);
                var m = System.Text.RegularExpressions.Regex.Match(name, @"\[anilist-(\d+)\]");
                int? alId = m.Success ? int.Parse(m.Groups[1].Value) : null;
                var title = m.Success ? name.Replace(m.Value, "").Trim() : name;
                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => new[] { ".mkv", ".mp4", ".m4v", ".avi", ".webm" }.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
                if (files.Count == 0) continue;
                imported += (await pipeline.ImportAsync(files, title, alId, null, "local", ct)).Count;
                if (!Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Any(File.Exists)) Directory.Delete(dir, true);
            }
            return Results.Ok(new { imported });
        });
    }

    private static async Task RemoveEpisodesAsync(AppDb db, AnimeRollOptions o, List<Episode> episodes)
    {
        var seriesIds = episodes.Select(e => e.SeriesId).Distinct().ToList();
        var ids = episodes.Select(e => e.Id).ToList();
        foreach (var ep in episodes)
        {
            var path = Path.Combine(o.MediaPath, ep.FilePath);
            SubtitleFiles.DeleteFor(path);
            if (File.Exists(path)) File.Delete(path);
        }
        db.WatchProgress.RemoveRange(db.WatchProgress.Where(w => ids.Contains(w.EpisodeId)));
        db.Episodes.RemoveRange(episodes);
        await db.SaveChangesAsync();
        foreach (var sid in seriesIds)
        {
            if (await db.Episodes.AnyAsync(e => e.SeriesId == sid)) continue;
            var series = await db.Series.FindAsync(sid);
            if (series is null) continue;
            db.Series.Remove(series);
            var folder = Path.Combine(o.MediaPath, MediaPipeline.Safe(series.Title));
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        await db.SaveChangesAsync();
    }

    /// Byte-range file serving (seek + resumable downloads). Auth via signed ?t= token.
    public static void MapMedia(this IEndpointRouteBuilder app)
    {
        app.MapGet("/media/episodes/{id:int}", async (int id, string? t, string? download, AppDb db, TokenService tokens, IOptions<AnimeRollOptions> o) =>
        {
            if (tokens.ValidateStreamToken(t, id) is null) return Results.Unauthorized();
            var ep = await db.Episodes.Include(e => e.Series).FirstOrDefaultAsync(e => e.Id == id);
            if (ep is null) return Results.NotFound();
            var path = Path.GetFullPath(Path.Combine(o.Value.MediaPath, ep.FilePath));
            if (!path.StartsWith(Path.GetFullPath(o.Value.MediaPath)) || !File.Exists(path)) return Results.NotFound();
            return Results.File(path, "video/mp4",
                fileDownloadName: download is "1" or "true" ? Path.GetFileName(path) : null, enableRangeProcessing: true);
        }).WithTags("Media");

        // Waits until the remuxed copy exists, so the player can show progress instead of a stalled video.
        app.MapGet("/media/episodes/{id:int}/audio/{index:int}/ready", async (int id, int index, string? t, AppDb db, TokenService tokens,
            IOptions<AnimeRollOptions> o, MediaPipeline pipeline, CancellationToken ct) =>
        {
            if (tokens.ValidateStreamToken(t, id) is null) return Results.Unauthorized();
            var ep = await db.Episodes.FindAsync(id);
            if (ep is null) return Results.NotFound();
            var path = Path.GetFullPath(Path.Combine(o.Value.MediaPath, ep.FilePath));
            if (!path.StartsWith(Path.GetFullPath(o.Value.MediaPath)) || !File.Exists(path)) return Results.NotFound();
            return await pipeline.ExtractAudioAsync(path, id, index, ct) is null ? Results.NotFound() : Results.NoContent();
        }).WithTags("Media");

        app.MapGet("/media/episodes/{id:int}/audio/{index:int}.m4a", async (int id, int index, string? t, AppDb db, TokenService tokens,
            IOptions<AnimeRollOptions> o, MediaPipeline pipeline, CancellationToken ct) =>
        {
            if (tokens.ValidateStreamToken(t, id) is null) return Results.Unauthorized();
            var ep = await db.Episodes.FindAsync(id);
            if (ep is null) return Results.NotFound();
            var path = Path.GetFullPath(Path.Combine(o.Value.MediaPath, ep.FilePath));
            if (!path.StartsWith(Path.GetFullPath(o.Value.MediaPath)) || !File.Exists(path)) return Results.NotFound();
            var remuxed = await pipeline.ExtractAudioAsync(path, id, index, ct);
            return remuxed is null ? Results.NotFound() : Results.File(remuxed, "audio/mp4", enableRangeProcessing: true);
        }).WithTags("Media");

        app.MapGet("/media/episodes/{id:int}/subtitles/{index:int}.vtt", async (int id, int index, string? t, AppDb db, TokenService tokens,
            IOptions<AnimeRollOptions> o) =>
        {
            if (tokens.ValidateStreamToken(t, id) is null) return Results.Unauthorized();
            var ep = await db.Episodes.FindAsync(id);
            if (ep is null) return Results.NotFound();
            var mp4 = Path.GetFullPath(Path.Combine(o.Value.MediaPath, ep.FilePath));
            var track = SubtitleFiles.Load(mp4)?.FirstOrDefault(s => s.Index == index);
            if (track is null) return Results.NotFound();
            var path = Path.Combine(Path.GetDirectoryName(mp4)!, Path.GetFileName(track.File));
            return File.Exists(path) ? Results.File(path, "text/vtt; charset=utf-8") : Results.NotFound();
        }).WithTags("Media");

        // Original ASS and the fonts embedded in the source, for the libass renderers.
        app.MapGet("/media/episodes/{id:int}/subtitles/{index:int}.ass", async (int id, int index, string? t, string? embed, AppDb db, TokenService tokens,
            IOptions<AnimeRollOptions> o) =>
        {
            if (tokens.ValidateStreamToken(t, id) is null) return Results.Unauthorized();
            var ep = await db.Episodes.FindAsync(id);
            if (ep is null) return Results.NotFound();
            var mp4 = Path.GetFullPath(Path.Combine(o.Value.MediaPath, ep.FilePath));
            var track = SubtitleFiles.Load(mp4)?.FirstOrDefault(s => s.Index == index);
            if (track?.AssFile is null) return Results.NotFound();
            var path = Path.Combine(Path.GetDirectoryName(mp4)!, Path.GetFileName(track.AssFile));
            if (!File.Exists(path)) return Results.NotFound();
            // embed=1: fonts inside the file, for players that load nothing else (ExoPlayer, AVPlayer-side libass).
            var fontsDir = SubtitleFiles.FontsDir(mp4);
            if (embed == "1" && Directory.Exists(fontsDir))
                return Results.Text(AssSubtitles.WithFonts(await File.ReadAllTextAsync(path), Directory.GetFiles(fontsDir)), "text/x-ssa; charset=utf-8");
            return Results.File(path, "text/x-ssa; charset=utf-8");
        }).WithTags("Media");

        app.MapGet("/media/episodes/{id:int}/fonts/{name}", async (int id, string name, string? t, AppDb db, TokenService tokens,
            IOptions<AnimeRollOptions> o) =>
        {
            if (tokens.ValidateStreamToken(t, id) is null) return Results.Unauthorized();
            var ep = await db.Episodes.FindAsync(id);
            if (ep is null) return Results.NotFound();
            var mp4 = Path.GetFullPath(Path.Combine(o.Value.MediaPath, ep.FilePath));
            var path = Path.Combine(SubtitleFiles.FontsDir(mp4), Path.GetFileName(name));
            return File.Exists(path) ? Results.File(path, "application/octet-stream") : Results.NotFound();
        }).WithTags("Media");
    }
}

using AnimeRoll.Server.Api.AniList;
using AnimeRoll.Server.Api.Data;

namespace AnimeRoll.Server.Api.Tracking;

/// What to change on a list entry: only the fields that are not null. Score is 0-10 (0 clears it).
public sealed record TrackUpdate(string? Status, int? Progress, double? Score);

/// A list the user keeps their progress on. Everything is expressed with AniList ids and statuses
/// (CURRENT, PLANNING, COMPLETED, PAUSED, DROPPED, REPEATING): MAL is translated on the way in and out.
public interface ITracker
{
    string Id { get; }
    Task<List<AniListEntry>> ListAsync(string status, CancellationToken ct);
    Task<AniListEntry?> EntryAsync(int aniListId, CancellationToken ct);
    Task SaveAsync(int aniListId, int? totalEpisodes, TrackUpdate update, CancellationToken ct);
}

public static class TrackUpdates
{
    public static readonly string[] Statuses = ["CURRENT", "PLANNING", "COMPLETED", "PAUSED", "DROPPED", "REPEATING"];

    /// Episodes watched without an explicit status: watching, or completed once the last one is reached.
    /// A status that is chosen without a count: completed means everything watched.
    public static TrackUpdate Complete(TrackUpdate u, int? total)
    {
        if (u.Status == null && u.Progress != null)
            return u with { Status = total is > 0 && u.Progress >= total ? "COMPLETED" : "CURRENT" };
        if (u.Status == "COMPLETED" && u.Progress == null && total is > 0)
            return u with { Progress = total };
        return u;
    }
}

public sealed class AniListTracker(User user, AniListClient al) : ITracker
{
    public string Id => "anilist";
    public Task<List<AniListEntry>> ListAsync(string status, CancellationToken ct) =>
        al.ListAsync(user.AniListToken!, user.AniListUserId!.Value, status, ct);
    public Task<AniListEntry?> EntryAsync(int aniListId, CancellationToken ct) =>
        al.EntryAsync(user.AniListToken!, user.AniListUserId!.Value, aniListId, ct);
    public Task SaveAsync(int aniListId, int? totalEpisodes, TrackUpdate u, CancellationToken ct)
    {
        u = TrackUpdates.Complete(u, totalEpisodes);
        return al.SaveEntryAsync(user.AniListToken!, aniListId, u.Status, u.Progress, u.Score, ct);
    }
}

public sealed class MalTracker(User user, MalClient mal, AniListClient al, Func<CancellationToken, Task> persist) : ITracker
{
    public string Id => "mal";

    private static string ToMal(string s) => s switch
    {
        "CURRENT" or "REPEATING" => "watching", "COMPLETED" => "completed", "PAUSED" => "on_hold",
        "DROPPED" => "dropped", _ => "plan_to_watch",
    };
    private static string FromMal(MalListStatus s) => s.Rewatching ? "REPEATING" : s.Status switch
    {
        "watching" => "CURRENT", "completed" => "COMPLETED", "on_hold" => "PAUSED", "dropped" => "DROPPED", _ => "PLANNING",
    };

    /// A token valid for the next day at least; refreshed (and stored) when it is about to expire.
    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (user.MalAccessToken is null || user.MalRefreshToken is null) throw new InvalidOperationException("MyAnimeList non collegato");
        if (user.MalExpiresAt is { } exp && exp > DateTime.UtcNow.AddDays(1)) return user.MalAccessToken;
        var t = await mal.RefreshAsync(user.MalRefreshToken, ct);
        user.MalAccessToken = t.Access; user.MalRefreshToken = t.Refresh; user.MalExpiresAt = t.ExpiresAt;
        await persist(ct);
        return t.Access;
    }

    public async Task<List<AniListEntry>> ListAsync(string status, CancellationToken ct)
    {
        var items = await mal.ListAsync(await TokenAsync(ct), ToMal(status), ct);
        var byMal = new Dictionary<int, AniListMedia>();
        foreach (var chunk in items.Select(i => i.MalId).Chunk(50))
            foreach (var m in await al.MediaByMalIdsAsync(chunk, ct))
                if (m.MalId is { } id) byMal[id] = m;
        return items.Where(i => byMal.ContainsKey(i.MalId))
            .Select(i => new AniListEntry(byMal[i.MalId], FromMal(i.Status), i.Status.Watched, i.Status.Score)).ToList();
    }

    public async Task<AniListEntry?> EntryAsync(int aniListId, CancellationToken ct)
    {
        var media = await al.MediaAsync(aniListId, ct);
        if (media?.MalId is not { } malId) return null;
        var st = await mal.GetStatusAsync(await TokenAsync(ct), malId, ct);
        return st is null ? null : new AniListEntry(media, FromMal(st), st.Watched, st.Score);
    }

    public async Task SaveAsync(int aniListId, int? totalEpisodes, TrackUpdate u, CancellationToken ct)
    {
        var media = await al.MediaAsync(aniListId, ct);
        if (media?.MalId is not { } malId)
            throw new InvalidOperationException("Questo titolo non ha un corrispondente su MyAnimeList.");
        u = TrackUpdates.Complete(u, totalEpisodes ?? media.Episodes);
        await mal.SetStatusAsync(await TokenAsync(ct), malId, u.Status == null ? null : ToMal(u.Status), u.Progress,
            u.Score == null ? null : (int)Math.Round(u.Score.Value), u.Status == null ? null : u.Status == "REPEATING", ct);
    }
}

/// Picks the list a profile syncs with: the one it chose, else the one that is connected (AniList first).
public sealed class TrackerService(AppDb db, AniListClient al, MalClient mal)
{
    public static bool HasAniList(User u) => u.AniListToken != null && u.AniListUserId != null;
    public static bool HasMal(User u) => u.MalAccessToken != null && u.MalRefreshToken != null;

    public static string? ActiveId(User u) => u.Tracker switch
    {
        "mal" when HasMal(u) => "mal",
        "anilist" when HasAniList(u) => "anilist",
        _ => HasAniList(u) ? "anilist" : HasMal(u) ? "mal" : null,
    };

    public ITracker? For(User u) => ActiveId(u) switch
    {
        "anilist" => new AniListTracker(u, al),
        "mal" => new MalTracker(u, mal, al, async ct => await db.SaveChangesAsync(ct)),
        _ => null,
    };
}

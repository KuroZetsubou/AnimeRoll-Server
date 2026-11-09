using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnimeRoll.Server.Api.Options;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.AniList;

public sealed record AniListMedia(
    int Id, string Title, string? TitleRomaji, string? Description, string? CoverUrl, string? BannerUrl,
    int? Episodes, int? Year, string? Status, string? Format, List<string> Genres, int? AverageScore, int? NextAiringEpisode, long? NextAiringAt = null, int? MalId = null);

/// A title linked to another by AniList (prequel, sequel, side story…); Relation is already in Italian.
public sealed record AniListRelated(string Relation, AniListMedia Media);

public sealed record AniListEntry(AniListMedia Media, string Status, int Progress, double? Score);

/// Thin GraphQL client for https://graphql.anilist.co
public sealed class AniListClient(HttpClient http, IOptions<AnimeRollOptions> options)
{
    private const string Endpoint = "https://graphql.anilist.co";
    private readonly AniListOptions _o = options.Value.AniList;

    private const string MediaFields = """
        id idMal episodes seasonYear status format genres averageScore description(asHtml:false)
        title { english romaji userPreferred }
        coverImage { extraLarge large }
        bannerImage
        nextAiringEpisode { episode airingAt }
        """;

    public string AuthorizeUrl(string redirectUri, string state) =>
        $"https://anilist.co/api/v2/oauth/authorize?client_id={_o.ClientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code&state={Uri.EscapeDataString(state)}";

    public async Task<string> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        var res = await http.PostAsJsonAsync("https://anilist.co/api/v2/oauth/token", new
        {
            grant_type = "authorization_code",
            client_id = _o.ClientId,
            client_secret = _o.ClientSecret,
            redirect_uri = redirectUri,
            code,
        }, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        return json!["access_token"]!.GetValue<string>();
    }

    public async Task<(int Id, string Name)> ViewerAsync(string token, CancellationToken ct)
    {
        var data = await QueryAsync("query { Viewer { id name } }", null, token, ct);
        var v = data["Viewer"]!;
        return (v["id"]!.GetValue<int>(), v["name"]!.GetValue<string>());
    }

    public async Task<List<AniListMedia>> TrendingAsync(int perPage, CancellationToken ct) =>
        ParseMediaList((await QueryAsync($$"""
            query($p:Int){ Page(perPage:$p){ media(type:ANIME, sort:TRENDING_DESC, isAdult:false){ {{MediaFields}} } } }
            """, new { p = perPage }, null, ct))["Page"]!["media"]!.AsArray());

    public async Task<List<AniListMedia>> SeasonalAsync(int perPage, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var season = now.Month switch { <= 3 => "WINTER", <= 6 => "SPRING", <= 9 => "SUMMER", _ => "FALL" };
        return ParseMediaList((await QueryAsync($$"""
            query($p:Int,$s:MediaSeason,$y:Int){ Page(perPage:$p){ media(type:ANIME, season:$s, seasonYear:$y, sort:POPULARITY_DESC, isAdult:false){ {{MediaFields}} } } }
            """, new { p = perPage, s = season, y = now.Year }, null, ct))["Page"]!["media"]!.AsArray());
    }

    /// Titles of one season (WINTER, SPRING, SUMMER, FALL) of a year, optionally limited to one genre.
    public async Task<List<AniListMedia>> BrowseAsync(string season, int year, string? genre, CancellationToken ct) =>
        ParseMediaList((await QueryAsync($$"""
            query($s:MediaSeason,$y:Int,$g:String){ Page(perPage:50){ media(type:ANIME, season:$s, seasonYear:$y, genre:$g, sort:POPULARITY_DESC, isAdult:false){ {{MediaFields}} } } }
            """, new { s = season, y = year, g = string.IsNullOrWhiteSpace(genre) ? null : genre }, null, ct))["Page"]!["media"]!.AsArray());

    public async Task<List<AniListMedia>> SearchAsync(string q, CancellationToken ct) =>
        ParseMediaList((await QueryAsync($$"""
            query($q:String){ Page(perPage:30){ media(type:ANIME, search:$q, sort:SEARCH_MATCH){ {{MediaFields}} } } }
            """, new { q }, null, ct))["Page"]!["media"]!.AsArray());

    /// What AniList recommends to someone who liked this title (most recommended first, adult titles hidden).
    public async Task<List<AniListMedia>> RecommendationsAsync(int id, CancellationToken ct)
    {
        var data = await QueryAsync($$"""
            query($id:Int){ Media(id:$id, type:ANIME){ recommendations(sort:RATING_DESC, perPage:24){ nodes { rating mediaRecommendation { isAdult {{MediaFields}} } } } } }
            """, new { id }, null, ct);
        var nodes = data["Media"]?["recommendations"]?["nodes"]?.AsArray();
        if (nodes is null) return [];
        return nodes
            .Select(n => n?["mediaRecommendation"])
            .Where(m => m is not null && m["isAdult"]?.GetValueKind() != JsonValueKind.True)
            .Select(m => ParseMedia(m!))
            .ToList();
    }

    private static readonly Dictionary<string, (string Label, int Order)> RelationLabels = new()
    {
        ["PREQUEL"] = ("Stagione precedente", 0), ["SEQUEL"] = ("Stagione successiva", 1), ["PARENT"] = ("Serie principale", 2),
        ["SIDE_STORY"] = ("Storia parallela", 3), ["SPIN_OFF"] = ("Spin-off", 4), ["ALTERNATIVE"] = ("Versione alternativa", 5),
        ["SUMMARY"] = ("Riassunto", 6), ["CHARACTER"] = ("Stessi personaggi", 7), ["OTHER"] = ("Correlato", 8),
    };

    /// Other anime AniList links to this one: seasons before/after, side stories, spin-offs. Manga/novel adaptations are left out.
    public async Task<List<AniListRelated>> RelationsAsync(int id, CancellationToken ct)
    {
        var data = await QueryAsync($$"""
            query($id:Int){ Media(id:$id, type:ANIME){ relations { edges { relationType node { type isAdult {{MediaFields}} } } } } }
            """, new { id }, null, ct);
        var edges = data["Media"]?["relations"]?["edges"]?.AsArray();
        if (edges is null) return [];
        return edges
            .Where(e => e?["node"]?["type"]?.ToString() == "ANIME" && e["node"]!["isAdult"]?.GetValueKind() != JsonValueKind.True)
            .Select(e => (Type: e!["relationType"]?.ToString() ?? "OTHER", Media: ParseMedia(e["node"]!)))
            .Select(x => (x.Media, Info: RelationLabels.GetValueOrDefault(x.Type, ("Correlato", 9))))
            .OrderBy(x => x.Info.Item2).ThenBy(x => x.Media.Year ?? 0)
            .Select(x => new AniListRelated(x.Info.Item1, x.Media))
            .ToList();
    }

    public async Task<AniListMedia?> MediaAsync(int id, CancellationToken ct)
    {
        var m = (await QueryAsync($$"""query($id:Int){ Media(id:$id, type:ANIME){ {{MediaFields}} } }""", new { id }, null, ct))["Media"];
        return m is null ? null : ParseMedia(m);
    }

    /// The viewer's list entries by status (CURRENT, PLANNING, COMPLETED, PAUSED, DROPPED, REPEATING).
    public async Task<List<AniListEntry>> ListAsync(string token, int userId, string status, CancellationToken ct)
    {
        var data = await QueryAsync($$"""
            query($u:Int,$s:MediaListStatus){ MediaListCollection(userId:$u, type:ANIME, status:$s, sort:UPDATED_TIME_DESC){
              lists { entries { status progress score(format:POINT_10_DECIMAL) media { {{MediaFields}} } } } } }
            """, new { u = userId, s = status }, token, ct);
        var result = new List<AniListEntry>();
        foreach (var list in data["MediaListCollection"]!["lists"]!.AsArray())
        foreach (var e in list!["entries"]!.AsArray())
            result.Add(new AniListEntry(ParseMedia(e!["media"]!), e["status"]!.GetValue<string>(),
                e["progress"]?.GetValue<int>() ?? 0, e["score"]?.GetValue<double>()));
        return result;
    }

    public async Task<AniListEntry?> EntryAsync(string token, int userId, int mediaId, CancellationToken ct)
    {
        try
        {
            var data = await QueryAsync($$"""
                query($u:Int,$m:Int){ MediaList(userId:$u, mediaId:$m){ status progress score(format:POINT_10_DECIMAL) media { {{MediaFields}} } } }
                """, new { u = userId, m = mediaId }, token, ct);
            var e = data["MediaList"];
            return e is null ? null : new AniListEntry(ParseMedia(e["media"]!), e["status"]!.GetValue<string>(),
                e["progress"]?.GetValue<int>() ?? 0, e["score"]?.GetValue<double>());
        }
        catch (HttpRequestException) { return null; } // 404 = not on list
    }

    /// Sets progress; marks COMPLETED when the last episode is reached.
    public async Task SaveProgressAsync(string token, int mediaId, int progress, int? totalEpisodes, CancellationToken ct)
    {
        var status = totalEpisodes is > 0 && progress >= totalEpisodes ? "COMPLETED" : "CURRENT";
        await QueryAsync("""
            mutation($m:Int,$p:Int,$s:MediaListStatus){ SaveMediaListEntry(mediaId:$m, progress:$p, status:$s){ id progress status } }
            """, new { m = mediaId, p = progress, s = status }, token, ct);
    }

    /// Saves only the fields that are given (status, episodes watched, score 0-10 where 0 clears it).
    public async Task SaveEntryAsync(string token, int mediaId, string? status, int? progress, double? score, CancellationToken ct)
    {
        var decl = new List<string> { "$m:Int" };
        var args = new List<string> { "mediaId:$m" };
        var vars = new Dictionary<string, object?> { ["m"] = mediaId };
        if (status != null) { decl.Add("$s:MediaListStatus"); args.Add("status:$s"); vars["s"] = status; }
        if (progress != null) { decl.Add("$p:Int"); args.Add("progress:$p"); vars["p"] = progress; }
        if (score != null) { decl.Add("$r:Int"); args.Add("scoreRaw:$r"); vars["r"] = (int)Math.Round(Math.Clamp(score.Value, 0, 10) * 10); }
        await QueryAsync($"mutation({string.Join(",", decl)}){{ SaveMediaListEntry({string.Join(",", args)}){{ id }} }}", vars, token, ct);
    }

    /// AniList titles for a batch of MyAnimeList ids (used to show a MAL list with AniList metadata).
    public async Task<List<AniListMedia>> MediaByMalIdsAsync(IReadOnlyCollection<int> malIds, CancellationToken ct)
    {
        if (malIds.Count == 0) return [];
        return ParseMediaList((await QueryAsync($$"""
            query($ids:[Int]){ Page(perPage:50){ media(idMal_in:$ids, type:ANIME){ {{MediaFields}} } } }
            """, new { ids = malIds.Take(50).ToArray() }, null, ct))["Page"]!["media"]!.AsArray());
    }

    public async Task SaveStatusAsync(string token, int mediaId, string status, CancellationToken ct) =>
        await QueryAsync("mutation($m:Int,$s:MediaListStatus){ SaveMediaListEntry(mediaId:$m, status:$s){ id } }",
            new { m = mediaId, s = status }, token, ct);

    // ---- Cache ----
    // Every query is cached on disk (DataPath/anilist-cache). A fresh copy is served without asking AniList; when AniList
    // is down, slow or rate-limiting, the last copy is served however old it is, so the apps keep working. Data that
    // belongs to a user is keyed by their token and dropped when they change something on AniList.
    private static readonly TimeSpan PublicTtl = TimeSpan.FromHours(6), UserTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(12);
    private string CacheDir => Path.Combine(options.Value.DataPath, "anilist-cache");

    private static string Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private string CachePath(string query, object? variables, string? token)
    {
        var owner = token is null ? "pub" : Hex(token)[..10];
        return Path.Combine(CacheDir, $"{owner}-{Hex(query + "|" + JsonSerializer.Serialize(variables))[..40]}.json");
    }

    private void DropUserCache(string token)
    {
        try { foreach (var f in Directory.EnumerateFiles(CacheDir, Hex(token)[..10] + "-*.json")) File.Delete(f); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    private static JsonNode? ReadCache(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    private async Task<JsonNode> QueryAsync(string query, object? variables, string? token, CancellationToken ct)
    {
        if (query.TrimStart().StartsWith("mutation", StringComparison.Ordinal))
        {
            var result = await FetchAsync(query, variables, token, ct, LiveTimeout);
            if (token != null) DropUserCache(token);
            return result;
        }
        var path = CachePath(query, variables, token);
        var age = File.Exists(path) ? DateTime.UtcNow - File.GetLastWriteTimeUtc(path) : (TimeSpan?)null;
        if (age is { } a && a < (token is null ? PublicTtl : UserTtl) && ReadCache(path) is { } fresh) return fresh;
        try
        {
            var live = await FetchAsync(query, variables, token, ct, LiveTimeout);
            Directory.CreateDirectory(CacheDir);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(tmp, live.ToJsonString(), ct);
            File.Move(tmp, path, overwrite: true);
            return live;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && IsOutage(e))
        {
            if (ReadCache(path) is { } stale) return stale;   // AniList is unreachable: the last answer is better than an error
            throw;
        }
    }

    /// Network failure, timeout, rate limit or AniList-side error: worth falling back to the cached answer.
    private static bool IsOutage(Exception e) => e switch
    {
        HttpRequestException h => h.StatusCode is null or >= System.Net.HttpStatusCode.InternalServerError or System.Net.HttpStatusCode.TooManyRequests,
        TaskCanceledException or JsonException => true,
        _ => false,
    };

    private async Task<JsonNode> FetchAsync(string query, object? variables, string? token, CancellationToken ct, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        ct = cts.Token;
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new { query, variables }),
        };
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        if (!res.IsSuccessStatusCode || body?["errors"] is JsonArray { Count: > 0 } errs)
            throw new HttpRequestException($"AniList: {body?["errors"]?.ToJsonString() ?? res.StatusCode.ToString()}", null, res.StatusCode);
        return body!["data"]!;
    }

    private static List<AniListMedia> ParseMediaList(JsonArray arr) => arr.Select(m => ParseMedia(m!)).ToList();

    private static AniListMedia ParseMedia(JsonNode m)
    {
        var t = m["title"]!;
        string? S(JsonNode? n) => n?.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;
        int? I(JsonNode? n) => n?.GetValueKind() == JsonValueKind.Number ? n.GetValue<int>() : null;
        return new AniListMedia(
            m["id"]!.GetValue<int>(),
            S(t["english"]) ?? S(t["userPreferred"]) ?? S(t["romaji"]) ?? "?",
            S(t["romaji"]),
            S(m["description"]),
            S(m["coverImage"]?["extraLarge"]) ?? S(m["coverImage"]?["large"]),
            S(m["bannerImage"]),
            I(m["episodes"]),
            I(m["seasonYear"]),
            S(m["status"]),
            S(m["format"]),
            m["genres"]?.AsArray().Select(g => g!.GetValue<string>()).ToList() ?? [],
            I(m["averageScore"]),
            I(m["nextAiringEpisode"]?["episode"]),
            m["nextAiringEpisode"]?["airingAt"] is { } at && at.GetValueKind() == JsonValueKind.Number ? at.GetValue<long>() : null,
            I(m["idMal"]));
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnimeRoll.Server.Api.Options;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.AniList;

public sealed record AniListMedia(
    int Id, string Title, string? TitleRomaji, string? Description, string? CoverUrl, string? BannerUrl,
    int? Episodes, int? Year, string? Status, string? Format, List<string> Genres, int? AverageScore, int? NextAiringEpisode);

public sealed record AniListEntry(AniListMedia Media, string Status, int Progress, double? Score);

/// Thin GraphQL client for https://graphql.anilist.co
public sealed class AniListClient(HttpClient http, IOptions<AnimeRollOptions> options)
{
    private const string Endpoint = "https://graphql.anilist.co";
    private readonly AniListOptions _o = options.Value.AniList;

    private const string MediaFields = """
        id episodes seasonYear status format genres averageScore description(asHtml:false)
        title { english romaji userPreferred }
        coverImage { extraLarge large }
        bannerImage
        nextAiringEpisode { episode }
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

    public async Task<List<AniListMedia>> SearchAsync(string q, CancellationToken ct) =>
        ParseMediaList((await QueryAsync($$"""
            query($q:String){ Page(perPage:30){ media(type:ANIME, search:$q, sort:SEARCH_MATCH){ {{MediaFields}} } } }
            """, new { q }, null, ct))["Page"]!["media"]!.AsArray());

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
              lists { entries { status progress score media { {{MediaFields}} } } } } }
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
                query($u:Int,$m:Int){ MediaList(userId:$u, mediaId:$m){ status progress score media { {{MediaFields}} } } }
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

    public async Task SaveStatusAsync(string token, int mediaId, string status, CancellationToken ct) =>
        await QueryAsync("mutation($m:Int,$s:MediaListStatus){ SaveMediaListEntry(mediaId:$m, status:$s){ id } }",
            new { m = mediaId, s = status }, token, ct);

    private async Task<JsonNode> QueryAsync(string query, object? variables, string? token, CancellationToken ct)
    {
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
            I(m["nextAiringEpisode"]?["episode"]));
    }
}

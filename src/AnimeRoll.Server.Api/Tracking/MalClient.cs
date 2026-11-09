using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnimeRoll.Server.Api.Options;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Tracking;

public sealed record MalToken(string Access, string Refresh, DateTime ExpiresAt);
/// What MyAnimeList keeps for one title of one user's list.
public sealed record MalListStatus(string Status, int Watched, int Score, bool Rewatching);

/// MyAnimeList API v2 (OAuth2 with PKCE, method "plain"). Used only to track the list.
public sealed class MalClient(HttpClient http, IOptions<AnimeRollOptions> options)
{
    private MalOptions O => options.Value.Mal;
    public bool Configured => !string.IsNullOrWhiteSpace(O.ClientId);

    public string AuthorizeUrl(string redirectUri, string state, string verifier) =>
        $"https://myanimelist.net/v1/oauth2/authorize?response_type=code&client_id={Uri.EscapeDataString(O.ClientId)}" +
        $"&code_challenge={verifier}&code_challenge_method=plain&state={Uri.EscapeDataString(state)}&redirect_uri={Uri.EscapeDataString(redirectUri)}";

    public Task<MalToken> ExchangeAsync(string code, string redirectUri, string verifier, CancellationToken ct) =>
        TokenAsync(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri, ["code_verifier"] = verifier,
        }, ct);

    public Task<MalToken> RefreshAsync(string refreshToken, CancellationToken ct) =>
        TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, ct);

    private async Task<MalToken> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = O.ClientId;
        if (!string.IsNullOrEmpty(O.ClientSecret)) form["client_secret"] = O.ClientSecret;
        using var res = await http.PostAsync("https://myanimelist.net/v1/oauth2/token", new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"MyAnimeList: {body}", null, res.StatusCode);
        var j = JsonNode.Parse(body)!;
        return new MalToken(j["access_token"]!.GetValue<string>(), j["refresh_token"]!.GetValue<string>(),
            DateTime.UtcNow.AddSeconds(j["expires_in"]!.GetValue<int>()));
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string url, string token, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"MyAnimeList: {body}", null, res.StatusCode);
        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }

    public async Task<string> ViewerNameAsync(string token, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Get, "https://api.myanimelist.net/v2/users/@me", token, null, ct))?["name"]?.GetValue<string>() ?? "MyAnimeList";

    private static MalListStatus? Parse(JsonNode? s) => s is null ? null : new MalListStatus(
        s["status"]?.GetValue<string>() ?? "plan_to_watch",
        s["num_episodes_watched"]?.GetValue<int>() ?? 0,
        s["score"]?.GetValue<int>() ?? 0,
        s["is_rewatching"]?.GetValueKind() == JsonValueKind.True);

    public async Task<MalListStatus?> GetStatusAsync(string token, int malId, CancellationToken ct) =>
        Parse((await SendAsync(HttpMethod.Get, $"https://api.myanimelist.net/v2/anime/{malId}?fields=my_list_status", token, null, ct))?["my_list_status"]);

    /// Only the fields that are given are sent.
    public async Task SetStatusAsync(string token, int malId, string? status, int? watched, int? score, bool? rewatching, CancellationToken ct)
    {
        var form = new Dictionary<string, string>();
        if (status != null) form["status"] = status;
        if (watched != null) form["num_watched_episodes"] = watched.Value.ToString();
        if (score != null) form["score"] = Math.Clamp(score.Value, 0, 10).ToString();
        if (rewatching != null) form["is_rewatching"] = rewatching.Value ? "true" : "false";
        if (form.Count == 0) return;
        await SendAsync(HttpMethod.Patch, $"https://api.myanimelist.net/v2/anime/{malId}/my_list_status", token, new FormUrlEncodedContent(form), ct);
    }

    /// The user's list for one MAL status (watching, completed, on_hold, dropped, plan_to_watch), most recently updated first.
    public async Task<List<(int MalId, MalListStatus Status)>> ListAsync(string token, string status, CancellationToken ct)
    {
        var result = new List<(int, MalListStatus)>();
        var url = $"https://api.myanimelist.net/v2/users/@me/animelist?status={status}&sort=list_updated_at&limit=100&fields=list_status";
        for (var page = 0; url != null && page < 10; page++)
        {
            var j = await SendAsync(HttpMethod.Get, url, token, null, ct);
            foreach (var item in j?["data"]?.AsArray() ?? [])
                if (item?["node"]?["id"] is { } id && Parse(item["list_status"]) is { } st) result.Add((id.GetValue<int>(), st));
            url = j?["paging"]?["next"]?.GetValue<string>();
        }
        return result;
    }
}

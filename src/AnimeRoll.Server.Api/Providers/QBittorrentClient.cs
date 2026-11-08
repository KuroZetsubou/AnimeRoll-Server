using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Infrastructure;
using AnimeRoll.Server.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Providers;

/// Minimal qBittorrent Web API client, for torrent-based source plugins.
public sealed class QBittorrentClient(HttpClient http, IOptions<AnimeRollOptions> options)
{
    private readonly QBittorrentOptions _o = options.Value.QBittorrent;
    private bool _loggedIn;

    private async Task EnsureLoginAsync(CancellationToken ct)
    {
        if (_loggedIn) return;
        http.BaseAddress ??= new Uri(_o.Url.TrimEnd('/') + "/");
        var res = await http.PostAsync("api/v2/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = _o.Username, ["password"] = _o.Password,
        }), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        // qBittorrent < 5 answers 200 "Ok." (or "Fails." on bad credentials); 5.x answers 204 / 401.
        if (!res.IsSuccessStatusCode || body.Trim() is not ("" or "Ok.")) throw new InvalidOperationException("Login qBittorrent fallito.");
        _loggedIn = true;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        await EnsureLoginAsync(ct);
        var res = await call();
        if (res.StatusCode == HttpStatusCode.Forbidden) { _loggedIn = false; await EnsureLoginAsync(ct); res = await call(); }
        res.EnsureSuccessStatusCode();
        return res;
    }

    /// Adding a torrent qBittorrent already has (a resumed job) is fine: it keeps its progress.
    public async Task AddAsync(string urls, string savePath, CancellationToken ct)
    {
        await EnsureLoginAsync(ct);
        using var res = await http.PostAsync("api/v2/torrents/add", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["urls"] = urls, ["savepath"] = savePath, ["category"] = _o.Category, ["sequentialDownload"] = "true",
        }), ct);
        if (res.StatusCode == HttpStatusCode.Conflict) return;   // qBittorrent 5: already added
        res.EnsureSuccessStatusCode();
    }

    public async Task<JsonNode?> InfoAsync(string hash, CancellationToken ct)
    {
        var res = await SendAsync(() => http.GetAsync($"api/v2/torrents/info?hashes={hash}", ct), ct);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsArray().FirstOrDefault();
    }

    public async Task<JsonArray?> FilesAsync(string hash, CancellationToken ct)
    {
        var res = await SendAsync(() => http.GetAsync($"api/v2/torrents/files?hash={hash}", ct), ct);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsArray();
    }

    public Task DeleteAsync(string hash, bool deleteFiles, CancellationToken ct) =>
        SendAsync(() => http.PostAsync("api/v2/torrents/delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["hashes"] = hash, ["deleteFiles"] = deleteFiles ? "true" : "false",
        }), ct), ct);
}

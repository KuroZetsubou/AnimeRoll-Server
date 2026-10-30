using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TL;

namespace AnimeRoll.Server.Api.Providers;

/// Holds one logged-in Telegram user session (WTelegramClient, MTProto).
/// Login is interactive: POST /api/telegram/login with the code you receive.
public sealed class TelegramService : IAsyncDisposable
{
    private readonly TelegramOptions _o;
    private readonly string _sessionPath;
    private WTelegram.Client? _client;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TelegramService(IOptions<AnimeRollOptions> options)
    {
        _o = options.Value.Telegram;
        _sessionPath = Path.Combine(options.Value.DataPath, "telegram.session");
        WTelegram.Helpers.Log = (_, _) => { };
    }

    public bool Configured => _o.ApiId != 0 && !string.IsNullOrEmpty(_o.ApiHash);
    public bool LoggedIn => _client?.User != null;

    private WTelegram.Client Client => _client ??= new WTelegram.Client(what => what switch
    {
        "api_id" => _o.ApiId.ToString(),
        "api_hash" => _o.ApiHash,
        "phone_number" => _o.PhoneNumber,
        "session_pathname" => _sessionPath,
        _ => null,
    });

    /// Call with null first; returns what Telegram needs next
    /// ("verification_code", "password") or null when logged in.
    public async Task<string?> LoginAsync(string? input)
    {
        if (!Configured) throw new InvalidOperationException("Telegram non configurato (ApiId/ApiHash).");
        await _lock.WaitAsync();
        try { return await Client.Login(input ?? _o.PhoneNumber); }
        finally { _lock.Release(); }
    }

    public async Task<WTelegram.Client> ReadyAsync()
    {
        if (LoggedIn) return _client!;
        var next = await LoginAsync(null);
        if (next != null) throw new InvalidOperationException($"Telegram richiede login: {next}");
        return _client!;
    }

    public async Task<List<(long Id, string Title, string? Username)>> ChannelsAsync()
    {
        var c = await ReadyAsync();
        var chats = await c.Messages_GetAllChats();
        return chats.chats.Values.OfType<Channel>().Select(ch => (ch.id, ch.title, (string?)ch.username)).ToList();
    }

    public async Task<Channel> ChannelAsync(long id)
    {
        var c = await ReadyAsync();
        var chats = await c.Messages_GetAllChats();
        return chats.chats.TryGetValue(id, out var chat) && chat is Channel ch
            ? ch : throw new InvalidOperationException("Canale non trovato tra le tue chat.");
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await Task.CompletedTask;
    }
}

/// Videos posted in the Telegram channels you selected in the app.
/// SourceRef = "{channelId}:{messageId}".
public sealed class TelegramProvider(TelegramService tg, IServiceScopeFactory scopes) : ISourceProvider
{
    public string Id => "telegram";
    public string Name => "Telegram";
    public bool Enabled => tg.Configured;
    public bool CanListEpisodes => false;
    public string? Hint => "Cerca tra i video dei canali Telegram selezionati.";

    public async Task<IReadOnlyList<SourceSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var channels = await db.TelegramChannels.Where(c => c.Enabled).ToListAsync(ct);
        var client = await tg.ReadyAsync();
        var results = new List<SourceSearchResult>();

        foreach (var ch in channels)
        {
            var channel = await tg.ChannelAsync(ch.Id);
            var found = await client.Messages_Search<InputMessagesFilterVideo>(channel, query, limit: 50);
            foreach (var m in found.Messages.OfType<Message>())
            {
                if (m.media is not MessageMediaDocument { document: Document doc }) continue;
                var fileName = doc.attributes.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name;
                var title = fileName ?? (string.IsNullOrWhiteSpace(m.message) ? $"Video {m.id}" : m.message.Split('\n')[0]);
                results.Add(new SourceSearchResult($"{ch.Id}:{m.id}", title, null,
                    $"{ch.Title} · {doc.size / 1048576} MB · {m.date:dd/MM/yyyy}",
                    Extra: new() { ["caption"] = m.message ?? "" }));
            }
        }
        return results;
    }

    public Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string seriesRef, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceEpisode>>([]);

    public async Task<IReadOnlyList<string>> DownloadAsync(DownloadContext ctx, CancellationToken ct)
    {
        var parts = ctx.Job.SourceRef.Split(':');
        var channel = await tg.ChannelAsync(long.Parse(parts[0]));
        var client = await tg.ReadyAsync();
        var msgs = await client.Channels_GetMessages(channel, new InputMessageID { id = int.Parse(parts[1]) });
        if (msgs.Messages.FirstOrDefault() is not Message { media: MessageMediaDocument { document: Document doc } })
            throw new InvalidOperationException("Messaggio senza video.");

        var name = doc.attributes.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name ?? $"telegram_{parts[1]}.mp4";
        var path = Path.Combine(ctx.WorkDir, string.Concat(name.Split(Path.GetInvalidFileNameChars())));
        await using var fs = File.Create(path);
        var last = DateTime.MinValue;
        await client.DownloadFileAsync(doc, fs, progress: (done, total) =>
        {
            if (DateTime.UtcNow - last < TimeSpan.FromSeconds(1)) return;
            last = DateTime.UtcNow;
            _ = ctx.ReportAsync(total > 0 ? (double)done / total : 0, $"{done / 1048576} / {total / 1048576} MB");
        });
        return [path];
    }
}

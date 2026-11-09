namespace AnimeRoll.Server.Api.Options;

/// All settings come from appsettings.json or environment variables
/// (e.g. AnimeRoll__Jwt__Key, AnimeRoll__AniList__ClientId) set in docker-compose.
public sealed class AnimeRollOptions
{
    public const string Section = "AnimeRoll";

    /// Public base URL the clients use to reach the server (e.g. https://anime.mionas.it).
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";

    /// SQLite database, Telegram session, temp files.
    public string DataPath { get; set; } = "/data";

    /// Final library: /media/{Series}/{Series} - E01.mp4
    public string MediaPath { get; set; } = "/media";

    /// Folder shared with the qBittorrent container (same path inside both containers).
    public string TorrentDownloadPath { get; set; } = "/downloads/torrents";

    public string FfmpegPath { get; set; } = "ffmpeg";

    /// Free space always left on the server (GB): downloads, uploads and imports that would go below it are refused.
    public int MinFreeSpaceGb { get; set; } = 2;

    /// Allow creating the first account through the API. Turn off after setup.
    public bool AllowRegistration { get; set; } = true;

    public JwtOptions Jwt { get; set; } = new();
    public PasskeyOptions Passkey { get; set; } = new();
    public AniListOptions AniList { get; set; } = new();
    public MalOptions Mal { get; set; } = new();
    public QBittorrentOptions QBittorrent { get; set; } = new();
    public TelegramOptions Telegram { get; set; } = new();
    public PluginOptions Plugins { get; set; } = new();
    public AppleOptions Apple { get; set; } = new();
    public AndroidOptions Android { get; set; } = new();
}

public sealed class JwtOptions
{
    /// At least 32 random characters. Required.
    public string Key { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 60 * 24 * 30;
    public int StreamTokenHours { get; set; } = 12;
}

public sealed class PasskeyOptions
{
    /// Relying party id: the server domain without scheme/port (e.g. anime.mionas.it).
    public string ServerDomain { get; set; } = "localhost";
    public string ServerName { get; set; } = "AnimeRoll";
    /// Allowed origins, e.g. https://anime.mionas.it. Apple apps use the same https origin.
    public List<string> Origins { get; set; } = ["http://localhost:8080"];
}

public sealed class AniListOptions
{
    /// Create a client at https://anilist.co/settings/developer
    /// with redirect URL {PublicBaseUrl}/api/anilist/callback
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public sealed class MalOptions
{
    /// Create an app at https://myanimelist.net/apiconfig (type "web") with redirect URL
    /// {PublicBaseUrl}/api/mal/callback. MAL is used only to track the list; metadata stays on AniList.
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public sealed class QBittorrentOptions
{
    public string Url { get; set; } = "http://qbittorrent:8081";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
    public string Category { get; set; } = "animeroll";
    /// How long to wait for a magnet's metadata before calling the torrent dead.
    public int MetadataTimeoutSeconds { get; set; } = 90;
}

public sealed class TelegramOptions
{
    /// From https://my.telegram.org/apps
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
}

public sealed class PluginOptions
{
    /// One sub-folder per plugin (.NET DLLs or a script with plugin.json). See PLUGINS.md.
    public string Directory { get; set; } = "/plugins";
    /// Per-plugin settings: Settings[pluginId][key]. Env: AnimeRoll__Plugins__Settings__<id>__<key>.
    public Dictionary<string, Dictionary<string, string>> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AppleOptions
{
    /// TEAMID.bundle.id list for apple-app-site-association (passkeys in the iOS/tvOS apps).
    public List<string> AppIds { get; set; } = [];
}

public sealed class AndroidOptions
{
    /// For passkeys in the Android app (Digital Asset Links).
    public string PackageName { get; set; } = "com.kurozetsubou.animeroll";
    /// SHA-256 of the signing certificate, "AB:CD:…" (keytool -list -v).
    public List<string> Sha256CertFingerprints { get; set; } = [];
}

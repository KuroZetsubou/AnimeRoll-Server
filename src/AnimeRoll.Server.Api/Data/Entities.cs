namespace AnimeRoll.Server.Api.Data;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? AniListToken { get; set; }
    public int? AniListUserId { get; set; }
    public string? AniListUserName { get; set; }

    /// MyAnimeList (OAuth2 PKCE): tracking only, metadata always comes from AniList.
    public string? MalAccessToken { get; set; }
    public string? MalRefreshToken { get; set; }
    public DateTime? MalExpiresAt { get; set; }
    public string? MalUserName { get; set; }

    /// Which list this profile syncs with: "anilist", "mal" or null (the one that is connected).
    public string? Tracker { get; set; }

    /// Profile picture: index of one of the client's built-in avatars.
    public int Avatar { get; set; }
    /// Hash of the 4-12 character passcode that locks this profile when switching to it; null = open.
    public string? PasscodeHash { get; set; }

    public List<PasskeyCredential> Passkeys { get; set; } = [];
}

public sealed class PasskeyCredential
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public required byte[] CredentialId { get; set; }
    public required byte[] PublicKey { get; set; }
    public uint SignCount { get; set; }
    public string Name { get; set; } = "Passkey";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}

/// A show in the server library, matched to AniList when possible.
public sealed class Series
{
    public int Id { get; set; }
    public int? AniListId { get; set; }
    public required string Title { get; set; }
    public string? TitleRomaji { get; set; }
    public string? Description { get; set; }
    public string? PosterUrl { get; set; }
    public string? BannerUrl { get; set; }
    public int? TotalEpisodes { get; set; }
    public string? Genres { get; set; }
    public int? Year { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<Episode> Episodes { get; set; } = [];
}

public sealed class Episode
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public Series? Series { get; set; }
    public double Number { get; set; }
    /// Bonus content ("EX1"): never counted for AniList progress.
    public bool IsExtra { get; set; }
    public string? Title { get; set; }
    /// Path relative to MediaPath.
    public required string FilePath { get; set; }
    public long SizeBytes { get; set; }
    public double? DurationSeconds { get; set; }
    public required string Source { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public sealed class WatchProgress
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public int EpisodeId { get; set; }
    public Episode? Episode { get; set; }
    public double PositionSeconds { get; set; }
    public double? DurationSeconds { get; set; }
    public bool Completed { get; set; }
    public bool SyncedToAniList { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// Stored as a number: new values only ever go at the end.
public enum DownloadStatus { Queued, Running, PostProcessing, Completed, Failed, Cancelled, Paused }

public sealed class DownloadJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestedBy { get; set; }
    /// Provider id: telegram, local or a plugin id.
    public required string Source { get; set; }
    /// Provider-specific reference (episode short id, magnet, message id, url...).
    public required string SourceRef { get; set; }
    public required string SeriesTitle { get; set; }
    public int? AniListId { get; set; }
    public double? EpisodeNumber { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    /// Higher runs first: -1 bassa, 0 normale, 1 alta, 10 "avvia subito".
    public int Priority { get; set; }
    public double Progress { get; set; }
    public string? Message { get; set; }
    public int? EpisodeId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

public sealed class TelegramChannel
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public string? Username { get; set; }
    public bool Enabled { get; set; } = true;
}

using Microsoft.EntityFrameworkCore;

namespace AnimeRoll.Server.Api.Data;

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<PasskeyCredential> Passkeys => Set<PasskeyCredential>();
    public DbSet<Series> Series => Set<Series>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<WatchProgress> WatchProgress => Set<WatchProgress>();
    public DbSet<DownloadJob> Downloads => Set<DownloadJob>();
    public DbSet<TelegramChannel> TelegramChannels => Set<TelegramChannel>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        b.Entity<PasskeyCredential>().HasIndex(p => p.CredentialId).IsUnique();
        b.Entity<Series>().HasIndex(s => s.AniListId);
        b.Entity<Episode>().HasIndex(e => new { e.SeriesId, e.Number });
        b.Entity<WatchProgress>().HasIndex(w => new { w.UserId, w.EpisodeId }).IsUnique();
        b.Entity<TelegramChannel>().Property(c => c.Id).ValueGeneratedNever();
    }
}

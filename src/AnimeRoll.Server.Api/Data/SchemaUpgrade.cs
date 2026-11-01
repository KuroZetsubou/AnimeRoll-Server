using AnimeRoll.Server.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace AnimeRoll.Server.Api.Data;

/// EnsureCreated() only builds a brand-new database. Columns added later are added here,
/// once, to databases created by older versions (e.g. the one already on the NAS).
public static class SchemaUpgrade
{
    private static readonly (string Table, string Column, string Definition)[] Columns =
    [
        ("Downloads", "Priority", "INTEGER NOT NULL DEFAULT 0"),
    ];

    public static void Apply(AppDb db, string mediaPath)
    {
        var conn = db.Database.GetDbConnection();
        conn.Open();
        try
        {
            foreach (var (table, column, definition) in Columns)
            {
                using var check = conn.CreateCommand();
                check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
                if (Convert.ToInt64(check.ExecuteScalar()) > 0) continue;
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
                alter.ExecuteNonQuery();
            }
        }
        finally { conn.Close(); }

        MergeDuplicateSeries(db, mediaPath);
        using var idx = db.Database.GetDbConnection().CreateCommand();
        db.Database.OpenConnection();
        try
        {
            idx.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Series_AniListId_Unique ON Series(AniListId) WHERE AniListId IS NOT NULL";
            idx.ExecuteNonQuery();
        }
        finally { db.Database.CloseConnection(); }
    }

    /// Two jobs of the same title started together used to create the series twice (same AniList id).
    /// Folds every duplicate into the oldest one: its episodes move over; an episode that exists in both
    /// keeps the older copy (and the file of the other one is removed if it is a different file).
    public static void MergeDuplicateSeries(AppDb db, string mediaPath)
    {
        var groups = db.Series.Where(s => s.AniListId != null).AsEnumerable()
            .GroupBy(s => s.AniListId).Where(g => g.Count() > 1).ToList();
        foreach (var g in groups)
        {
            var keep = g.OrderBy(s => s.Id).First();
            var existing = db.Episodes.Where(e => e.SeriesId == keep.Id).ToList();
            foreach (var dup in g.Where(s => s.Id != keep.Id))
            {
                foreach (var ep in db.Episodes.Where(e => e.SeriesId == dup.Id).ToList())
                {
                    var clash = existing.FirstOrDefault(e => e.Number == ep.Number);
                    if (clash is null) { ep.SeriesId = keep.Id; existing.Add(ep); continue; }
                    if (!string.Equals(ep.FilePath, clash.FilePath, StringComparison.Ordinal))
                    {
                        var path = Path.Combine(mediaPath, ep.FilePath);
                        SubtitleFiles.DeleteFor(path);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    db.WatchProgress.RemoveRange(db.WatchProgress.Where(w => w.EpisodeId == ep.Id));
                    db.Episodes.Remove(ep);
                }
                db.Series.Remove(dup);
            }
            keep.UpdatedAt = DateTime.UtcNow;
        }
        db.SaveChanges();
    }
}

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

    public static void Apply(AppDb db)
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
    }
}

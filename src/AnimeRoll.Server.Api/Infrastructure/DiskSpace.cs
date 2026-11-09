using AnimeRoll.Server.Api.Options;

namespace AnimeRoll.Server.Api.Infrastructure;

/// Raised when the server doesn't have room for what is about to be written.
public sealed class InsufficientSpaceException(string message) : InvalidOperationException(message);

public sealed record StorageInfo(long TotalBytes, long FreeBytes, long LibraryBytes, long ReserveBytes);

/// Free space on the disks the server writes to (library, work folder, torrents).
/// Every download, upload and import calls `EnsureRoom` first so a full server fails early with a clear message.
public static class DiskSpace
{
    /// Always kept free (AnimeRoll__MinFreeSpaceGb), so the server itself never fills up.
    public static long ReserveBytes { get; private set; } = 2L * 1024 * 1024 * 1024;

    public static void Configure(AnimeRollOptions o) => ReserveBytes = Math.Max(0, o.MinFreeSpaceGb) * 1024L * 1024 * 1024;

    /// The mounted drive that contains `path` (the longest matching mount point). Never throws:
    /// some systems list odd mount points, and a failed space check must not stop a download.
    private static DriveInfo? DriveOf(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var best = default(DriveInfo);
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || !full.StartsWith(d.Name, StringComparison.Ordinal)) continue;
                    if (best is null || d.Name.Length > best.Name.Length) best = d;
                }
                catch (Exception) { /* unreadable mount: skip it */ }
            }
            return best;
        }
        catch (Exception)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)) ?? "/"); } catch (Exception) { return null; }
        }
    }

    public static (long Total, long Free)? Read(string path)
    {
        try { return DriveOf(path) is { } d ? (d.TotalSize, d.AvailableFreeSpace) : null; }
        catch (Exception) { return null; }
    }

    /// Throws when writing `neededBytes` to each of `paths` would leave less than the reserve.
    /// Paths on the same disk add up (a download in the work folder plus the file in the library).
    public static void EnsureRoom(long neededBytes, params string[] paths)
    {
        foreach (var group in paths.Select(p => (Path: p, Drive: DriveOf(p))).Where(x => x.Drive != null).GroupBy(x => x.Drive!.Name))
        {
            var free = group.First().Drive!.AvailableFreeSpace;
            var needed = neededBytes * group.Count() + ReserveBytes;
            if (free < needed)
                throw new InsufficientSpaceException(neededBytes == 0
                    ? $"Spazio insufficiente sul server: liberi {Format(free)}, meno della riserva di {Format(ReserveBytes)}. Libera spazio eliminando qualche episodio."
                    : $"Spazio insufficiente sul server: servono {Format(neededBytes * group.Count())} (più {Format(ReserveBytes)} di riserva), liberi {Format(free)}. Libera spazio eliminando qualche episodio.");
        }
    }

    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024} KB",
    };
}

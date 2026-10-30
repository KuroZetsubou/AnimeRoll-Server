using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AnimeRoll.Server.Api.AniList;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Downloads;

/// Turns downloaded files into library episodes:
/// remux to MP4 (so AVPlayer/tvOS can play them), probe duration,
/// move to /media/{Series}/ and register Series + Episode.
public sealed partial class MediaPipeline(AppDb db, AniListClient aniList, IOptions<AnimeRollOptions> options, ILogger<MediaPipeline> log)
{
    private readonly AnimeRollOptions _o = options.Value;
    private static readonly string[] AppleFriendly = [".mp4", ".m4v", ".mov"];

    public async Task<Series> EnsureSeriesAsync(string title, int? aniListId, CancellationToken ct)
    {
        var series = aniListId != null
            ? await db.Series.FirstOrDefaultAsync(s => s.AniListId == aniListId, ct)
            : await db.Series.FirstOrDefaultAsync(s => s.Title == title, ct);
        if (series != null) return series;

        series = new Series { Title = title, AniListId = aniListId };
        if (aniListId != null)
        {
            try
            {
                if (await aniList.MediaAsync(aniListId.Value, ct) is { } m)
                {
                    series.Title = m.Title; series.TitleRomaji = m.TitleRomaji; series.Description = m.Description;
                    series.PosterUrl = m.CoverUrl; series.BannerUrl = m.BannerUrl; series.TotalEpisodes = m.Episodes;
                    series.Genres = string.Join(", ", m.Genres); series.Year = m.Year;
                }
            }
            catch (Exception e) { log.LogWarning(e, "AniList metadata non disponibili per {Id}", aniListId); }
        }
        db.Series.Add(series);
        await db.SaveChangesAsync(ct);
        return series;
    }

    public async Task<List<Episode>> ImportAsync(IReadOnlyList<string> files, string seriesTitle, int? aniListId,
        double? episodeNumber, string source, CancellationToken ct)
    {
        var series = await EnsureSeriesAsync(seriesTitle, aniListId, ct);
        var folder = Path.Combine(_o.MediaPath, Safe(series.Title));
        Directory.CreateDirectory(folder);
        var imported = new List<Episode>();

        foreach (var file in files)
        {
            var number = files.Count == 1 && episodeNumber != null ? episodeNumber.Value : GuessEpisodeNumber(file) ?? episodeNumber ?? 0;
            var mp4 = await EnsureMp4Async(file, ct);
            var target = Path.Combine(folder, $"{Safe(series.Title)} - E{number.ToString("00.##", CultureInfo.InvariantCulture)}.mp4");
            if (File.Exists(target)) File.Delete(target);
            File.Move(mp4, target);
            if (mp4 != file && File.Exists(file)) File.Delete(file);

            var rel = Path.GetRelativePath(_o.MediaPath, target);
            var ep = await db.Episodes.FirstOrDefaultAsync(e => e.SeriesId == series.Id && e.Number == number, ct);
            if (ep == null) { ep = new Episode { SeriesId = series.Id, FilePath = rel, Source = source, Number = number }; db.Episodes.Add(ep); }
            ep.FilePath = rel; ep.Source = source;
            ep.SizeBytes = new FileInfo(target).Length;
            ep.DurationSeconds = await ProbeDurationAsync(target, ct);
            ep.Title ??= $"Episodio {number.ToString(CultureInfo.InvariantCulture)}";
            imported.Add(ep);
        }
        series.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return imported;
    }

    /// MKV/AVI → MP4 copying streams (fast). Falls back to AAC audio when the codec isn't MP4-compatible.
    /// Subtitles become mov_text (soft subs that AVPlayer shows).
    public async Task<string> EnsureMp4Async(string file, CancellationToken ct)
    {
        // "h264,yuv420p10le" → codec + pixel format of the first video stream.
        var probe = (await ProbeAsync(file, "stream=codec_name,pix_fmt", "v:0", ct) ?? "").Split(',');
        var codec = probe.ElementAtOrDefault(0) ?? "";
        var pixFmt = probe.ElementAtOrDefault(1) ?? "";
        // Apple devices can't decode H.264 10-bit (Hi10P, common in fansub releases) or 4:2:2/4:4:4:
        // only those get re-encoded (slow); everything else is copied as is (fast).
        var mustReencode = codec == "h264" && pixFmt is not ("yuv420p" or "yuvj420p" or "");
        if (!mustReencode && AppleFriendly.Contains(Path.GetExtension(file).ToLowerInvariant())) return file;

        var output = Path.ChangeExtension(file, ".remux.mp4");
        string[] video = mustReencode
            ? ["-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-pix_fmt", "yuv420p"]
            // Apple players need the hvc1 tag for HEVC; other codecs keep their default tag.
            : codec == "hevc" ? ["-c:v", "copy", "-tag:v", "hvc1"] : ["-c:v", "copy"];
        var ok = await RunAsync(_o.FfmpegPath, ["-y", "-i", file, "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?",
            .. video, "-c:a", "copy", "-c:s", "mov_text", "-movflags", "+faststart", output], ct);
        if (!ok)
            ok = await RunAsync(_o.FfmpegPath, ["-y", "-i", file, "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?",
                .. video, "-c:a", "aac", "-b:a", "192k", "-c:s", "mov_text", "-movflags", "+faststart", output], ct);
        if (!ok)
            ok = await RunAsync(_o.FfmpegPath, ["-y", "-i", file, "-map", "0:v:0", "-map", "0:a?",
                .. video, "-c:a", "aac", "-movflags", "+faststart", output], ct);
        if (!ok) throw new InvalidOperationException("ffmpeg non è riuscito a convertire il file in MP4.");
        return output;
    }

    private async Task<double?> ProbeDurationAsync(string file, CancellationToken ct) =>
        double.TryParse(await ProbeAsync(file, "format=duration", null, ct), CultureInfo.InvariantCulture, out var d) ? d : null;

    private async Task<string?> ProbeAsync(string file, string entries, string? stream, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_o.FfmpegPath.Replace("ffmpeg", "ffprobe")) { RedirectStandardOutput = true, RedirectStandardError = true };
        var args = new List<string> { "-v", "error" };
        if (stream != null) args.AddRange(["-select_streams", stream]);
        args.AddRange(["-show_entries", entries, "-of", "csv=p=0", file]);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return output.Trim();
        }
        catch { return null; }
    }

    private async Task<bool> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(ct);
        await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) log.LogWarning("ffmpeg fallito: {Err}", (await err)[^Math.Min(500, (await err).Length)..]);
        return p.ExitCode == 0;
    }

    public static string Safe(string name) =>
        string.Concat(name.Split(Path.GetInvalidFileNameChars().Concat([':', '?', '*', '"', '<', '>', '|']).ToArray())).Trim();

    /// "[Group] Show - 05 [1080p].mkv", "Show S01E05", "Episode 5", "Ep.05"
    public static double? GuessEpisodeNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        name = BracketRegex().Replace(name, " ");
        foreach (var r in new[] { SxxExxRegex(), EpRegex(), DashRegex(), TrailingRegex() })
            if (r.Match(name) is { Success: true } m && double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var n))
                return n;
        return null;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)")] private static partial Regex BracketRegex();
    [GeneratedRegex(@"S\d{1,2}E(\d{1,4}(?:\.\d)?)", RegexOptions.IgnoreCase)] private static partial Regex SxxExxRegex();
    [GeneratedRegex(@"\b(?:Ep(?:isodio|isode)?\.?)\s*(\d{1,4}(?:\.\d)?)\b", RegexOptions.IgnoreCase)] private static partial Regex EpRegex();
    [GeneratedRegex(@"\s-\s(\d{1,4}(?:\.\d)?)(?:v\d)?\b")] private static partial Regex DashRegex();
    [GeneratedRegex(@"(\d{1,4})\s*$")] private static partial Regex TrailingRegex();
}

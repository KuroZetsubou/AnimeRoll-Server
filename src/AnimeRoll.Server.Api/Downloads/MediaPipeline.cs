using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AnimeRoll.Server.Api.AniList;
using System.Text.Json.Nodes;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Library;
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
            // Converting writes a second copy next to the original, then the file moves into the library.
            AnimeRoll.Server.Api.Infrastructure.DiskSpace.EnsureRoom(new FileInfo(file).Length, Path.GetDirectoryName(file)!, _o.MediaPath);
            var number = files.Count == 1 && episodeNumber != null ? episodeNumber.Value : GuessEpisodeNumber(file) ?? episodeNumber ?? 0;
            // Subtitles first, from the original file: WebVTT keeps overlapping lines (dialogue + signs)
            // that mov_text inside an MP4 can't, and the apps render them in sync themselves.
            var subsDir = Path.Combine(Path.GetDirectoryName(file)!, "subs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(subsDir);
            List<(SubtitleTrack Track, string TempPath)> subs;
            string mp4;
            try
            {
                subs = await ExtractSubtitlesAsync(file, subsDir, ct);
                mp4 = await EnsureMp4Async(file, ct);
            }
            catch
            {
                Directory.Delete(subsDir, true);   // rejected or failed file: leave nothing behind
                throw;
            }
            var target = Path.Combine(folder, $"{Safe(series.Title)} - E{number.ToString("00.##", CultureInfo.InvariantCulture)}.mp4");
            if (File.Exists(target)) File.Delete(target);
            File.Move(mp4, target);
            if (mp4 != file && File.Exists(file)) File.Delete(file);
            SubtitleFiles.MoveNextTo(target, subs);
            Directory.Delete(subsDir, true);

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
    /// Video the Apple players can't decode (H.264 10-bit, 4:2:2/4:4:4, VP9, AV1…) is re-encoded to 8-bit H.264.
    /// Subtitles are NOT put in the MP4: they are extracted to WebVTT sidecars (ExtractSubtitlesAsync).
    /// <param name="force">Always rewrite (used by "Ripara episodio"), even for an MP4 that looks fine.</param>
    /// <param name="outputDir">Where to write the converted file (default: next to the input).</param>
    private static readonly string[] AppleVideoCodecs = ["h264", "hevc", "mpeg4"];

    public async Task<string> EnsureMp4Async(string file, CancellationToken ct, bool force = false, string? outputDir = null)
    {
        // "h264,yuv420p10le" → codec + pixel format of the first video stream.
        var probe = (await ProbeAsync(file, "stream=codec_name,pix_fmt", "v:0", ct) ?? "").Split(',');
        var codec = probe.ElementAtOrDefault(0) ?? "";
        var pixFmt = probe.ElementAtOrDefault(1) ?? "";
        // Apple devices can't decode H.264 10-bit (Hi10P, common in fansub releases) or 4:2:2/4:4:4:
        // only those get re-encoded (slow); everything else is copied as is (fast).
        // Video codecs the Apple players (AVPlayer, Apple TV) decode: anything else (VP9, AV1, VP8, WMV, Theora…)
        // plays as a black picture with working audio, so it is converted to H.264 too.
        var mustReencode = (codec == "h264" && pixFmt is not ("yuv420p" or "yuvj420p" or ""))
            || (codec != "" && !AppleVideoCodecs.Contains(codec));
        // An MP4 is kept as is only when its index (moov) comes first: otherwise every open and seek
        // in the apps first has to fetch the end of the file. A stream copy with +faststart fixes it.
        // Embedded subtitles are stripped too: the apps show the WebVTT sidecars, never both.
        var hasEmbeddedSubs = !string.IsNullOrWhiteSpace(await ProbeAsync(file, "stream=index", "s", ct));
        if (!force && !mustReencode && !hasEmbeddedSubs && AppleFriendly.Contains(Path.GetExtension(file).ToLowerInvariant()) && IsFastStart(file))
        {
            var asIs = await ProbeSyncAsync(file, ct);
            if (asIs.Ok) return file;
            log.LogWarning("Audio/video non allineati in {File}: {Detail}. Riallineo.", Path.GetFileName(file), asIs.Detail);
        }

        var output = Path.Combine(outputDir ?? Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".remux.mp4");
        string[] video = mustReencode
            ? ["-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-pix_fmt", "yuv420p"]
            // Apple players need the hvc1 tag for HEVC; other codecs keep their default tag.
            : codec == "hevc" ? ["-c:v", "copy", "-tag:v", "hvc1"] : ["-c:v", "copy"];
        // Timestamps rebuilt on read (+genpts, missing/broken PTS from badly muxed files) and shifted so
        // both streams start at zero together (make_zero): fixes most desync while still copying streams.
        string[] input = ["-y", "-fflags", "+genpts", "-i", file, "-map", "0:v:0", "-map", "0:a?"];
        string[] tail = ["-sn", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", output];

        var ok = await RunAsync(_o.FfmpegPath, [.. input, .. video, "-c:a", "copy", .. tail], ct);
        if (!ok)
            ok = await RunAsync(_o.FfmpegPath, [.. input, .. video, "-c:a", "aac", "-b:a", "192k", .. tail], ct);
        if (!ok) throw new InvalidOperationException("ffmpeg non è riuscito a convertire il file in MP4.");

        var sync = await ProbeSyncAsync(output, ct);
        if (!sync.Ok)
        {
            // Copying wasn't enough: re-encode only the audio, resampled onto the video clock
            // (fills gaps, drops overlaps). Video stays untouched.
            log.LogWarning("Audio/video ancora non allineati in {File}: {Detail}. Ricodifico l'audio.", Path.GetFileName(file), sync.Detail);
            ok = await RunAsync(_o.FfmpegPath, [.. input, .. video,
                "-af", "aresample=async=1000:first_pts=0", "-c:a", "aac", "-b:a", "192k", .. tail], ct);
            sync = ok ? await ProbeSyncAsync(output, ct) : sync;
        }
        if (!sync.Ok)
        {
            if (File.Exists(output)) File.Delete(output);
            throw new InvalidOperationException(
                $"Audio e video non sono allineati ({sync.Detail}): file scartato. Riscarica l'episodio da un'altra fonte.");
        }
        return output;
    }

    /// "Ripara episodio": runs a library file through the import pipeline again (timestamps rebuilt,
    /// A/V sync checked and fixed, index moved to the front, embedded subtitles turned into sidecars).
    /// The original is replaced only when the result passes; otherwise it stays untouched.
    public async Task<string> RepairAsync(int episodeId, string workDir, CancellationToken ct)
    {
        var ep = await db.Episodes.FirstOrDefaultAsync(e => e.Id == episodeId, ct)
                 ?? throw new InvalidOperationException("Episodio non trovato.");
        var mp4 = Path.Combine(_o.MediaPath, ep.FilePath);
        if (!File.Exists(mp4)) throw new InvalidOperationException("Il file dell'episodio non esiste più sul NAS.");

        var before = await ProbeSyncAsync(mp4, ct);
        var embedded = await ExtractSubtitlesAsync(mp4, workDir, ct);
        var repaired = await EnsureMp4Async(mp4, ct, force: true, outputDir: workDir);   // throws if sync can't be fixed

        // Same-folder temp + rename: the library never sees a half-copied file.
        var temp = mp4 + ".repair.tmp";
        File.Move(repaired, temp, overwrite: true);
        File.Move(temp, mp4, overwrite: true);
        if (embedded.Count > 0) SubtitleFiles.MoveNextTo(mp4, embedded);

        ep.SizeBytes = new FileInfo(mp4).Length;
        ep.DurationSeconds = await ProbeDurationAsync(mp4, ct);
        await db.SaveChangesAsync(ct);

        var after = await ProbeSyncAsync(mp4, ct);
        return FormattableString.Invariant($"Riparato: scarto audio/video {before.Delta:0.00} s → {after.Delta:0.00} s");
    }

    /// Largest tolerated gap between audio and video, in seconds (start or total length).
    public const double MaxAvDrift = 0.5;

    public sealed record SyncCheck(bool Ok, double Delta, string Detail);

    /// Compares start time and duration of the first video and audio streams.
    /// No audio track counts as in sync (nothing to drift); a broken probe does not.
    public async Task<SyncCheck> ProbeSyncAsync(string file, CancellationToken ct)
    {
        var json = await ProbeAsync(file, "stream=codec_type,start_time,duration:format=duration", null, ct, "json");
        var root = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
        var streams = root?["streams"]?.AsArray() ?? [];
        var v = streams.FirstOrDefault(s => s?["codec_type"]?.GetValue<string>() == "video");
        var a = streams.FirstOrDefault(s => s?["codec_type"]?.GetValue<string>() == "audio");
        if (v is null) return new SyncCheck(false, double.PositiveInfinity, "nessuna traccia video leggibile");
        if (a is null) return new SyncCheck(true, 0, "nessuna traccia audio");

        static double? Num(JsonNode? n) =>
            double.TryParse(n?.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        var format = Num(root?["format"]?["duration"]);
        var vDur = Num(v["duration"]) ?? format;
        var aDur = Num(a["duration"]) ?? format;
        var startDelta = Math.Abs((Num(v["start_time"]) ?? 0) - (Num(a["start_time"]) ?? 0));
        var durDelta = vDur is { } vd && aDur is { } ad ? Math.Abs(vd - ad) : 0;
        var delta = Math.Max(startDelta, durDelta);
        var detail = FormattableString.Invariant(
            $"inizio sfasato di {startDelta:0.00} s, durate video {vDur:0.00} s / audio {aDur:0.00} s");
        return new SyncCheck(delta <= MaxAvDrift, delta, detail);
    }

    private static readonly HashSet<string> TextSubtitleCodecs =
        ["ass", "ssa", "subrip", "srt", "mov_text", "webvtt", "text"];

    /// Every text subtitle track of the file → one WebVTT file each in outDir.
    /// Same input timeline as the MP4 conversion, so cues line up with the video.
    /// Picture-based tracks (PGS, VobSub) can't become text and are skipped.
    public async Task<List<(SubtitleTrack Track, string TempPath)>> ExtractSubtitlesAsync(string file, string outDir, CancellationToken ct)
    {
        var result = new List<(SubtitleTrack, string)>();
        var json = await ProbeAsync(file, "stream=index,codec_name:stream_tags=language,title:stream_disposition=default,forced", "s", ct, "json");
        if (string.IsNullOrWhiteSpace(json)) return result;
        var streams = JsonNode.Parse(json)?["streams"]?.AsArray() ?? [];
        foreach (var st in streams)
        {
            var codec = st?["codec_name"]?.GetValue<string>() ?? "";
            if (!TextSubtitleCodecs.Contains(codec)) { log.LogInformation("Sottotitolo {Codec} ignorato (immagine)", codec); continue; }
            var index = st!["index"]!.GetValue<int>();
            var lang = st["tags"]?["language"]?.GetValue<string>();
            var title = st["tags"]?["title"]?.GetValue<string>();
            var isDefault = st["disposition"]?["default"]?.GetValue<int>() == 1;
            var isForced = st["disposition"]?["forced"]?.GetValue<int>() == 1;
            var path = Path.Combine(outDir, $"s{index}.vtt");
            if (await RunAsync(_o.FfmpegPath, ["-y", "-i", file, "-map", $"0:{index}", "-c:s", "webvtt", path], ct) && File.Exists(path))
                result.Add((new SubtitleTrack(index, Path.GetFileName(path), lang is "und" ? null : lang, title, isDefault, isForced), path));
        }
        return result;
    }

    /// True when the top-level 'moov' box comes before 'mdat' (reads only box headers).
    public static bool IsFastStart(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            Span<byte> header = stackalloc byte[16];
            while (fs.Position + 8 <= fs.Length)
            {
                var start = fs.Position;
                if (fs.Read(header[..8]) < 8) return false;
                long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = System.Text.Encoding.ASCII.GetString(header.Slice(4, 4));
                if (size == 1)
                {
                    if (fs.Read(header.Slice(8, 8)) < 8) return false;
                    size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
                }
                else if (size == 0) size = fs.Length - start;   // box runs to the end of the file
                if (type == "moov") return true;
                if (type == "mdat") return false;
                if (size < 8) return false;                     // corrupt: let ffmpeg rewrite it
                fs.Position = start + size;
            }
            return false;
        }
        catch (IOException) { return false; }
    }

    private async Task<double?> ProbeDurationAsync(string file, CancellationToken ct) =>
        double.TryParse(await ProbeAsync(file, "format=duration", null, ct), CultureInfo.InvariantCulture, out var d) ? d : null;

    private async Task<string?> ProbeAsync(string file, string entries, string? stream, CancellationToken ct, string format = "csv=p=0")
    {
        var psi = new ProcessStartInfo(_o.FfmpegPath.Replace("ffmpeg", "ffprobe")) { RedirectStandardOutput = true, RedirectStandardError = true };
        var args = new List<string> { "-v", "error" };
        if (stream != null) args.AddRange(["-select_streams", stream]);
        args.AddRange(["-show_entries", entries, "-of", format, file]);
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

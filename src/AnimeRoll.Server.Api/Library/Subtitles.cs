using System.Text.Json;
using AnimeRoll.Server.Api.Data;
using AnimeRoll.Server.Api.Downloads;
using AnimeRoll.Server.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnimeRoll.Server.Api.Library;

/// One subtitle track stored as a WebVTT file next to the episode (plus the original ASS when the source had one:
/// the apps with a libass renderer use it for the full look).
/// <param name="File">File name only, in the same folder as the MP4.</param>
/// <param name="AssFile">Original ASS/SSA, same folder; null when the track was not ASS.</param>
public sealed record SubtitleTrack(int Index, string File, string? Language, string? Title, bool IsDefault, bool IsForced, string? AssFile = null)
{
    /// "Signs & Songs" style tracks only carry on-screen text: never auto-select them over a full track.
    public bool IsSignsOnly =>
        IsForced || (Title?.Contains("sign", StringComparison.OrdinalIgnoreCase) ?? false)
                 || (Title?.Contains("song", StringComparison.OrdinalIgnoreCase) ?? false)
                 || (Title?.Contains("cartell", StringComparison.OrdinalIgnoreCase) ?? false);

    public string Label
    {
        get
        {
            var lang = Language is { } l && Names.TryGetValue(l, out var n) ? n : Language?.ToUpperInvariant() ?? "Sottotitoli";
            return string.IsNullOrWhiteSpace(Title) || Title.Equals(Language, StringComparison.OrdinalIgnoreCase) ? lang : $"{lang} – {Title}";
        }
    }

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ita"] = "Italiano", ["it"] = "Italiano", ["eng"] = "English", ["en"] = "English",
        ["jpn"] = "日本語", ["ja"] = "日本語", ["spa"] = "Español", ["es"] = "Español",
        ["fre"] = "Français", ["fra"] = "Français", ["fr"] = "Français", ["ger"] = "Deutsch", ["deu"] = "Deutsch",
        ["de"] = "Deutsch", ["por"] = "Português", ["pt"] = "Português", ["ara"] = "العربية", ["rus"] = "Русский",
    };
}

/// Sidecar files: "<episode>.mp4" → "<episode>.subs.json" (track list) + "<episode>.s<N>.<lang>.vtt".
/// Kept as files (not DB rows) so existing databases need no migration and the media folder stays self-describing.
public static class SubtitleFiles
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string IndexPath(string mp4) => Path.ChangeExtension(mp4, ".subs.json");

    public static List<SubtitleTrack>? Load(string mp4)
    {
        var path = IndexPath(mp4);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<List<SubtitleTrack>>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    public static void Save(string mp4, IReadOnlyList<SubtitleTrack> tracks) =>
        File.WriteAllText(IndexPath(mp4), JsonSerializer.Serialize(tracks, Json));

    /// Moves extracted VTT files next to the final MP4 with stable names and writes the index.
    public static List<SubtitleTrack> MoveNextTo(string mp4, IReadOnlyList<(SubtitleTrack Track, string TempPath)> extracted)
    {
        DeleteFor(mp4);
        var baseName = Path.GetFileNameWithoutExtension(mp4);
        var folder = Path.GetDirectoryName(mp4)!;
        var result = new List<SubtitleTrack>();
        foreach (var (track, temp) in extracted)
        {
            var name = $"{baseName}.s{track.Index}.{track.Language ?? "und"}.vtt";
            File.Move(temp, Path.Combine(folder, name), overwrite: true);
            string? assName = null;
            var tempAss = Path.ChangeExtension(temp, ".ass");
            if (File.Exists(tempAss))
            {
                assName = Path.ChangeExtension(name, ".ass");
                File.Move(tempAss, Path.Combine(folder, assName), overwrite: true);
            }
            result.Add(track with { File = name, AssFile = assName });
        }
        // Fonts embedded in the source (MKV attachments) travel with the ASS tracks.
        var tempFonts = Path.Combine(Path.GetDirectoryName(extracted.FirstOrDefault().TempPath ?? "") ?? "", "fonts");
        if (result.Any(t => t.AssFile != null) && Directory.Exists(tempFonts))
        {
            var dest = FontsDir(mp4);
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(tempFonts)) File.Move(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        }
        Save(mp4, result);
        return result;
    }

    /// "<episode>.fonts/" next to the MP4.
    public static string FontsDir(string mp4) => Path.Combine(Path.GetDirectoryName(mp4)!, Path.GetFileNameWithoutExtension(mp4) + ".fonts");

    public static void DeleteFor(string mp4)
    {
        foreach (var t in Load(mp4) ?? [])
        {
            var p = Path.Combine(Path.GetDirectoryName(mp4)!, t.File);
            if (File.Exists(p)) File.Delete(p);
            if (t.AssFile is { } a && File.Exists(Path.Combine(Path.GetDirectoryName(mp4)!, a))) File.Delete(Path.Combine(Path.GetDirectoryName(mp4)!, a));
        }
        if (Directory.Exists(FontsDir(mp4))) Directory.Delete(FontsDir(mp4), true);
        if (File.Exists(IndexPath(mp4))) File.Delete(IndexPath(mp4));
    }
}

/// Extracts subtitles for episodes imported before sidecar subtitles existed (their MP4 holds mov_text tracks).
/// Runs once per episode in the background after start-up; episodes already indexed are skipped.
public sealed class SubtitleBackfill(IServiceScopeFactory scopes, IOptions<AnimeRollOptions> options, ILogger<SubtitleBackfill> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);     // let the server finish starting
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var pipeline = scope.ServiceProvider.GetRequiredService<MediaPipeline>();
        var episodes = await db.Episodes.AsNoTracking().Select(e => e.FilePath).ToListAsync(stoppingToken);
        var done = 0;
        foreach (var rel in episodes)
        {
            var mp4 = Path.Combine(options.Value.MediaPath, rel);
            if (!File.Exists(mp4) || File.Exists(SubtitleFiles.IndexPath(mp4))) continue;
            try
            {
                var tmp = Path.Combine(options.Value.DataPath, "work", "subs-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);
                var extracted = await pipeline.ExtractSubtitlesAsync(mp4, tmp, stoppingToken);
                SubtitleFiles.MoveNextTo(mp4, extracted);
                Directory.Delete(tmp, true);
                done++;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning(e, "Sottotitoli non estratti per {File}", rel); }
        }
        if (done > 0) log.LogInformation("Sottotitoli estratti per {Count} episodi già in libreria", done);
    }
}

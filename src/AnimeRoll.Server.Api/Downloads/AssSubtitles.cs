using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeRoll.Server.Api.Downloads;

/// ASS/SSA → WebVTT keeping what a player can draw: position (\an, \pos, margins), italic / bold / underline
/// and colors. ffmpeg's own conversion drops all of it. Animations, drawings, karaoke and fades are ignored.
///
/// Colors become the cue class "c_rrggbb" (WebVTT has no free-form color), read by the AnimeRoll players.
public static partial class AssSubtitles
{
    /// The ASS with the given font files added as a [Fonts] section (the "UU" encoding libass reads), so a player that
    /// only gets the one file still has the fonts the styles name. Any [Fonts] section already in the file is kept.
    public static string WithFonts(string ass, IEnumerable<string> fontFiles)
    {
        var sb = new StringBuilder(ass.TrimEnd());
        var any = false;
        foreach (var path in fontFiles)
        {
            if (!any) { sb.Append(ass.Contains("[Fonts]", StringComparison.OrdinalIgnoreCase) ? "\n" : "\n\n[Fonts]\n"); any = true; }
            sb.Append("fontname: ").Append(Path.GetFileName(path)).Append('\n');
            var data = File.ReadAllBytes(path);
            var line = 0;
            for (var i = 0; i < data.Length; i += 3)
            {
                var n = Math.Min(3, data.Length - i);
                var b0 = data[i]; var b1 = n > 1 ? data[i + 1] : (byte)0; var b2 = n > 2 ? data[i + 2] : (byte)0;
                var v = new[] { b0 >> 2, ((b0 & 3) << 4) | (b1 >> 4), ((b1 & 15) << 2) | (b2 >> 6), b2 & 63 };
                for (var k = 0; k < n + 1; k++) { sb.Append((char)(v[k] + 33)); if (++line == 80) { sb.Append('\n'); line = 0; } }
            }
            if (line != 0) sb.Append('\n');
        }
        return sb.ToString();
    }

    private sealed record Style(string Name, int Alignment, string Color, bool Bold, bool Italic, bool Underline,
        int MarginL, int MarginR, int MarginV);

    public static string ToWebVtt(string ass)
    {
        var lines = ass.Replace("\r\n", "\n").Split('\n');
        double resX = 384, resY = 288;
        var styles = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);
        string[]? styleFormat = null, eventFormat = null;
        var section = "";
        var vtt = new StringBuilder("WEBVTT\n\n");
        var cues = new List<(double Start, double End, string Settings, string Text, int Layer)>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line.ToLowerInvariant(); continue; }
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].TrimStart();

            if (section == "[script info]")
            {
                if (key.Equals("PlayResX", StringComparison.OrdinalIgnoreCase) && Num(value) is { } x && x > 0) resX = x;
                if (key.Equals("PlayResY", StringComparison.OrdinalIgnoreCase) && Num(value) is { } y && y > 0) resY = y;
            }
            else if (section.Contains("styles"))
            {
                if (key == "Format") styleFormat = Split(value, int.MaxValue);
                else if (key == "Style" && styleFormat is not null)
                {
                    var f = Split(value, styleFormat.Length);
                    string Get(string n) { var i = Array.FindIndex(styleFormat, c => c.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i < f.Length ? f[i] : ""; }
                    styles[Get("Name")] = new Style(Get("Name"), (int)(Num(Get("Alignment")) ?? 2), ColorOf(Get("PrimaryColour")),
                        Num(Get("Bold")) is not (null or 0), Num(Get("Italic")) is not (null or 0), Num(Get("Underline")) is not (null or 0),
                        (int)(Num(Get("MarginL")) ?? 0), (int)(Num(Get("MarginR")) ?? 0), (int)(Num(Get("MarginV")) ?? 0));
                }
            }
            else if (section == "[events]")
            {
                if (key == "Format") eventFormat = Split(value, int.MaxValue);
                else if (key == "Dialogue" && eventFormat is not null)
                {
                    var f = Split(value, eventFormat.Length);
                    string Get(string n) { var i = Array.FindIndex(eventFormat, c => c.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i < f.Length ? f[i] : ""; }
                    var start = Time(Get("Start")); var end = Time(Get("End"));
                    if (start is null || end is null || end <= start) continue;
                    styles.TryGetValue(Get("Style").TrimStart('*'), out var style);
                    style ??= styles.GetValueOrDefault("Default") ?? new Style("Default", 2, "ffffff", false, false, false, 0, 0, 0);
                    var cue = Convert(Get("Text"), style, resX, resY, Get("MarginV"));
                    if (cue is { } c) cues.Add((start.Value, end.Value, c.Settings, c.Text, (int)(Num(Get("Layer")) ?? 0)));
                }
            }
        }

        // Players that read CSS (ExoPlayer, browsers) get the colors from a STYLE block; AnimeRoll's own reads the class name.
        var colors = cues.SelectMany(c => ColorClasses().Matches(c.Text).Select(m => m.Groups[1].Value)).Distinct().ToList();
        if (colors.Count > 0)
        {
            vtt.Append("STYLE\n");
            foreach (var col in colors) vtt.Append("::cue(.c_").Append(col).Append(") { color: #").Append(col).Append(" }\n");
            vtt.Append('\n');
        }
        foreach (var c in cues.OrderBy(c => c.Start).ThenBy(c => c.Layer))
            vtt.Append(Stamp(c.Start)).Append(" --> ").Append(Stamp(c.End)).Append(c.Settings).Append('\n').Append(c.Text).Append("\n\n");
        return vtt.ToString();
    }

    private static (string Settings, string Text)? Convert(string text, Style style, double resX, double resY, string marginV)
    {
        int alignment = style.Alignment;
        string color = style.Color;
        bool bold = style.Bold, italic = style.Italic, underline = style.Underline;
        double? px = null, py = null;
        var body = new StringBuilder();

        // Blocks "{...}" hold override tags; text between them is drawn with the current state.
        int pos = 0;
        bool drawing = false;
        string Wrap(string t)
        {
            if (t.Length == 0) return "";
            var s = Escape(t);
            if (underline) s = $"<u>{s}</u>";
            if (italic) s = $"<i>{s}</i>";
            if (bold) s = $"<b>{s}</b>";
            if (color != "ffffff") s = $"<c.c_{color}>{s}</c>";
            return s;
        }
        foreach (Match m in Blocks().Matches(text))
        {
            if (!drawing) body.Append(Wrap(text[pos..m.Index]));
            pos = m.Index + m.Length;
            foreach (Match t in Tags().Matches(m.Value))
            {
                var name = t.Groups[1].Value.ToLowerInvariant();
                var arg = t.Groups[2].Value.Trim();
                switch (name)
                {
                    case "an": alignment = int.TryParse(arg, out var an) && an is >= 1 and <= 9 ? an : alignment; break;
                    case "a": // legacy SSA alignment
                        if (int.TryParse(arg, out var a) && a is >= 1 and <= 11)
                            alignment = a <= 3 ? a : a <= 7 ? 7 + (a - 5) : 4 + (a - 9);
                        break;
                    case "pos":
                    {
                        var p = arg.Trim('(', ')').Split(',');
                        if (p.Length == 2 && Num(p[0]) is { } x && Num(p[1]) is { } y) { px = x; py = y; }
                        break;
                    }
                    case "i": italic = arg == "1" || (arg == "" && style.Italic); break;
                    case "b": bold = arg is "1" or "700" || (arg == "" && style.Bold); break;
                    case "u": underline = arg == "1" || (arg == "" && style.Underline); break;
                    case "c": case "1c": color = arg == "" ? style.Color : ColorOf(arg); break;
                    case "p": drawing = Num(arg) is > 0; break;
                    case "r": alignment = style.Alignment; color = style.Color; bold = style.Bold; italic = style.Italic; underline = style.Underline; break;
                }
            }
        }
        if (!drawing) body.Append(Wrap(text[pos..]));
        var result = body.ToString().Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", "\u00A0").Trim('\n', ' ');
        if (result.Length == 0) return null;
        result = string.Join('\n', result.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        if (result.Length == 0) return null;

        var col = (alignment - 1) % 3;     // 0 left, 1 center, 2 right
        var row = (alignment - 1) / 3;     // 0 bottom, 1 middle, 2 top
        var align = col switch { 0 => "left", 2 => "right", _ => "center" };
        var settings = new StringBuilder();
        if (px is { } x0 && py is { } y0)
        {
            settings.Append(Inv($" line:{Pct(y0 / resY)},{(row == 2 ? "start" : row == 1 ? "center" : "end")}"));
            settings.Append(Inv($" position:{Pct(x0 / resX)}"));
        }
        else
        {
            var mv = Num(marginV) is > 0 and var m ? m : style.MarginV;
            if (row == 2) settings.Append(Inv($" line:{Pct(mv / resY)},start"));
            else if (row == 1) settings.Append(" line:50%,center");
            if (col == 0) settings.Append(Inv($" position:{Pct(style.MarginL / resX)}"));
            else if (col == 2) settings.Append(Inv($" position:{Pct(1 - style.MarginR / resX)}"));
        }
        // Plain bottom-center cues keep no settings: the players lay them out as before.
        if (settings.Length > 0 || col != 1) settings.Append(" align:").Append(align);
        return (settings.ToString(), result);
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Pct(double v) => Inv($"{Math.Clamp(v, 0, 1) * 100:0.##}%");
    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    private static double? Num(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    private static string[] Split(string value, int count) => count == int.MaxValue ? value.Split(',').Select(s => s.Trim()).ToArray() : value.Split(',', count).Select((s, i) => i < count - 1 ? s.Trim() : s).ToArray();

    /// ASS "&HAABBGGRR" / "&HBBGGRR" → "rrggbb".
    private static string ColorOf(string raw)
    {
        var h = raw.Trim().TrimStart('&').TrimStart('H', 'h').TrimEnd('&');
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        if (!uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return "ffffff";
        var r = v & 0xFF; var g = (v >> 8) & 0xFF; var b = (v >> 16) & 0xFF;
        return $"{r:x2}{g:x2}{b:x2}";
    }

    /// "H:MM:SS.cc" → seconds.
    private static double? Time(string s)
    {
        var p = s.Trim().Split(':');
        if (p.Length != 3 || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)
            || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) return null;
        return h * 3600 + m * 60 + sec;
    }

    private static string Stamp(double t)
    {
        var ms = (long)Math.Round(t * 1000);
        return $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}";
    }

    [GeneratedRegex(@"<c\.c_([0-9a-f]{6})>")] private static partial Regex ColorClasses();
    [GeneratedRegex(@"\{[^}]*\}")] private static partial Regex Blocks();
    [GeneratedRegex(@"\\(an|pos|1c|c|a|i|b|u|p|r)(?![a-zA-Z])(\([^)]*\)|[^\\}]*)")] private static partial Regex Tags();
}

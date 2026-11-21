using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Kiosk3;

/// <summary>Eine fertige Tafel (1920 × 1080) mit dem Ergebnis der Einpassung.</summary>
public sealed class Board : IDisposable
{
    public required string Name { get; init; }
    public required Bitmap Picture { get; init; }
    public required Fit Fit { get; init; }
    public void Dispose() => Picture.Dispose();
}

/// <summary>Ergebnis der Einpassung: gewählte Schriftgrössen, stärkste Verengung, passt oder nicht.</summary>
public sealed record Fit(bool Fits, int TitleSize, int BodySize, int Width, int Lines, string Problem, bool Joined = false)
{
    public string Info(bool hasTitle) =>
        (hasTitle && TitleSize > 0 ? $"Title {TitleSize} px · " : "") + $"Text {BodySize} px · Width {Width} % · {Lines} lines" + (Joined ? " · own line breaks joined" : "");
}

/// <summary>
/// Passt Meldungen in den Textbereich. Ablauf pro Schriftgrösse (von gross nach klein):
/// 1. Zeilen, die zu breit sind, werden horizontal verengt – bis «Verengen bis %».
/// 2. Reicht das nicht, wird (wenn erlaubt) umbrochen; die Zeilen werden dabei ausgeglichen.
/// 3. Ist der Text dann zu hoch, wird die Schrift kleiner – bis zur Mindestgrösse.
/// Die erste Grösse, bei der alles passt, gewinnt.
/// </summary>
public static class Fitter
{
    const int W = 1920, H = 1080;
    static readonly Bitmap Scratch = new(1, 1);
    static readonly Graphics G = MakeScratch();
    static Graphics MakeScratch() { var g = Graphics.FromImage(Scratch); g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; return g; }
    static readonly StringFormat Fmt = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };
    static readonly Dictionary<(string, float, FontStyle), Font> Fonts = new();

    static Font FontFor(Design d, float size, bool underline)
    {
        var style = (d.Bold ? FontStyle.Bold : FontStyle.Regular) | (underline ? FontStyle.Underline : 0);
        var key = (d.Font, size, style);
        if (!Fonts.TryGetValue(key, out var f))
        {
            if (Fonts.Count > 400) { foreach (var x in Fonts.Values) x.Dispose(); Fonts.Clear(); }
            Fonts[key] = f = new Font(d.Font, size, style, GraphicsUnit.Pixel);
        }
        return f;
    }
    static float Measure(string s, Font f) => s.Length == 0 ? 0 : G.MeasureString(s, f, PointF.Empty, Fmt).Width;

    // ----- Textblock setzen -------------------------------------------------------------

    /// <summary>Ein gesetzter Block: Zeilen, gemeinsame Verengung, Höhe.</summary>
    sealed record Piece(List<string> Lines, Font Font, float Scale, float LineHeight, bool Ok)
    {
        public float Height => Lines.Count * LineHeight;
    }

    static List<string> Wrap(string paragraph, Font f, float max)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Measure(next, f) > max) { lines.Add(line); line = word; }
            else line = next;
        }
        lines.Add(line);
        return lines;
    }

    /// <summary>Absatz mit gleich vielen Zeilen, aber möglichst gleich langen (kein einzelnes Wort allein in der letzten Zeile).</summary>
    static List<string> Balanced(string paragraph, Font f, float max)
    {
        var best = Wrap(paragraph, f, max);
        if (best.Count < 2) return best;
        float lo = Measure(paragraph, f) / best.Count, hi = max;
        for (int i = 0; i < 14 && hi - lo > 2; i++)
        {
            var mid = (lo + hi) / 2;
            var tryLines = Wrap(paragraph, f, mid);
            if (tryLines.Count <= best.Count) { hi = mid; best = tryLines; } else lo = mid;
        }
        return best;
    }

    static Piece Set(string text, Design d, float size, bool underline, int minWidth, int spacing, float width)
    {
        var f = FontFor(d, size, underline);
        float min = Math.Clamp(minWidth, 30, 100) / 100f;
        var lines = new List<string>();
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var para = string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (para.Length == 0) { lines.Add(""); continue; }
            if (!d.AutoWrap || Measure(para, f) * min <= width) { lines.Add(para); continue; }
            // Umbrechen: Breite so wählen, dass die Verengung möglichst eine Zeile spart.
            var normal = Wrap(para, f, width);
            var narrow = Wrap(para, f, width / min);
            var limit = narrow.Count < normal.Count ? width / min : width;
            lines.AddRange(d.Balance ? Balanced(para, f, limit) : Wrap(para, f, limit));
        }
        float widest = lines.Count == 0 ? 0 : lines.Max(l => Measure(l, f));
        float scale = widest <= width ? 1 : width / widest;
        return new Piece(lines, f, scale, (float)Math.Ceiling(f.GetHeight(G) * spacing / 100f), scale >= min - 0.001f);
    }

    // ----- Einpassen --------------------------------------------------------------------

    sealed record Layout(Fit Fit, List<(Piece Piece, float Gap)> Pieces);

    /// <summary>Eine Tafel pro Sprache: Titel (unterstrichen) und Meldung.</summary>
    static Layout FitSingle(Design d, SingleRule r, Words t, int? cap)
    {
        var kept = FitSingleOnce(d, r, t, cap);
        if (kept.Fit.Fits || !d.JoinBreaks) return kept;
        var joined = FitSingleOnce(d, r, new Words { Title = t.Title, Body = Join(t.Body) }, cap);
        return joined.Fit.Fits ? joined with { Fit = joined.Fit with { Joined = true } } : kept;
    }

    static Layout FitSingleOnce(Design d, SingleRule r, Words t, int? cap)
    {
        bool hasTitle = t.Title.Trim().Length > 0;
        int max = Math.Max(r.BodyMin, Math.Min(r.BodyMax, cap ?? int.MaxValue));
        Layout? last = null;
        for (int body = max; body >= r.BodyMin; body--)
        {
            // Titel schrumpft im gleichen Verhältnis wie der Text, nie unter sein Minimum.
            int title = Math.Clamp((int)Math.Round(r.TitleMax * (double)body / Math.Max(1, r.BodyMax)), r.TitleMin, r.TitleMax);
            var pieces = new List<(Piece, float)>();
            Piece? tp = null;
            if (hasTitle) { tp = Set(t.Title, d, title, r.Underline, r.MinWidth, r.LineSpacing, d.AreaWidth); pieces.Add((tp, 0)); }
            var bp = Set(t.Body, d, body, false, r.MinWidth, r.LineSpacing, d.AreaWidth);
            pieces.Add((bp, hasTitle ? r.Gap : 0));
            float height = pieces.Sum(x => x.Item1.Height + x.Item2);
            bool ok = pieces.All(x => x.Item1.Ok) && height <= d.AreaHeight;
            int width = (int)Math.Floor(pieces.Min(x => x.Item1.Scale) * 100);
            var problem = ok ? "" : height > d.AreaHeight ? "Text too tall – even at the smallest size." : "Line too wide – even fully condensed.";
            last = new Layout(new Fit(ok, hasTitle ? title : 0, body, width, pieces.Sum(x => x.Item1.Lines.Count), problem), pieces);
            if (ok) return last;
        }
        return last!;
    }

    /// <summary>Mehrsprachige Tafel: alle Sprachblöcke in gleicher Schriftgrösse untereinander.</summary>
    static Layout FitMulti(Design d, MultiRule r, IReadOnlyList<(string Title, string Body)> blocks, int? cap)
    {
        var kept = FitMultiOnce(d, r, blocks, cap, false);
        if (kept.Fit.Fits || !d.JoinBreaks) return kept;
        var joined = FitMultiOnce(d, r, blocks, cap, true);
        return joined.Fit.Fits ? joined with { Fit = joined.Fit with { Joined = true } } : kept;
    }

    static Layout FitMultiOnce(Design d, MultiRule r, IReadOnlyList<(string Title, string Body)> blocks, int? cap, bool join)
    {
        int max = Math.Max(r.Min, Math.Min(r.Max, cap ?? int.MaxValue));
        var texts = blocks.Select(b => (b.Title.Length > 0 ? b.Title + "\n" : "") + (join ? Join(b.Body) : b.Body)).ToList();
        Layout? last = null;
        for (int size = max; size >= r.Min; size--)
        {
            var pieces = texts.Select((b, i) => (Set(b, d, size, false, r.MinWidth, r.LineSpacing, d.AreaWidth), i == 0 ? 0f : r.Gap)).ToList();
            float height = pieces.Sum(x => x.Item1.Height + x.Item2);
            bool ok = pieces.All(x => x.Item1.Ok) && height <= d.AreaHeight;
            int width = (int)Math.Floor(pieces.Min(x => x.Item1.Scale) * 100);
            var problem = ok ? "" : height > d.AreaHeight ? "Texts too tall – even at the smallest size." : "Line too wide – even fully condensed.";
            last = new Layout(new Fit(ok, 0, size, width, pieces.Sum(x => x.Item1.Lines.Count), problem), pieces);
            if (ok) return last;
        }
        return last!;
    }

    static (string Title, string Body) MultiText(Notice n, string code)
    {
        var t = n.Get(code);
        return ("", t.Body); // mehrsprachig: kein Titel, der ganze Text steht im Textfeld
    }

    /// <summary>
    /// Eigene Umbrüche zusammenziehen: nur nach Zeilen, die mit einem Satzzeichen enden.
    /// Zeilen ohne Satzzeichen (Überschriften wie «WETTER ALARM») behalten ihren Umbruch.
    /// </summary>
    static string Join(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append(".,;:!?…".Contains(lines[i - 1][^1]) ? ' ' : '\n');
            sb.Append(lines[i]);
        }
        return sb.ToString();
    }

    static string[] MultiLanguages(Notice n) => n.Languages.Take(4).ToArray();

    // ----- Einheitliche Grösse über alle Meldungen --------------------------------------

    /// <summary>
    /// «Einheitlich»: die grösste Schrift, bei der jede Meldung dieser Art passt.
    /// So sehen alle Tafeln einer Art gleich aus; Meldungen, die auch einzeln nicht passen, zählen nicht mit.
    /// </summary>
    public static int? UniformSingle(Project p)
    {
        if (!p.Single.Uniform) return null;
        int? cap = null;
        foreach (var n in p.Notices.Where(n => n.Mode == BoardMode.ProSprache))
            foreach (var code in n.Languages)
            {
                var t = n.Get(code);
                if (t.Body.Trim().Length == 0) continue;
                var f = FitSingle(p.Design, p.Single, t, null).Fit;
                if (f.Fits) cap = Math.Min(cap ?? int.MaxValue, f.BodySize);
            }
        return cap;
    }

    public static int? UniformMulti(Project p, int count)
    {
        var r = p.MultiFor(count);
        if (!r.Uniform) return null;
        int? cap = null;
        foreach (var n in p.Notices.Where(n => n.Mode == BoardMode.Mehrsprachig && Math.Clamp(n.Languages.Count, 2, 4) == count && n.Languages.Count >= 2))
        {
            var langs = MultiLanguages(n);
            if (langs.Any(c => n.Get(c).Body.Trim().Length == 0)) continue;
            var f = FitMulti(p.Design, r, langs.Select(c => MultiText(n, c)).ToList(), null).Fit;
            if (f.Fits) cap = Math.Min(cap ?? int.MaxValue, f.BodySize);
        }
        return cap;
    }

    /// <summary>Obergrenzen für «einheitlich», einmal pro Durchgang berechnet.</summary>
    public sealed class Caps
    {
        public int? Single;
        public Dictionary<int, int?> Multi = new();
        public static Caps Of(Project p)
        {
            var c = new Caps { Single = UniformSingle(p) };
            for (int i = 2; i <= 4; i++) c.Multi[i] = UniformMulti(p, i);
            return c;
        }
    }

    // ----- Zeichnen ---------------------------------------------------------------------

    public static List<Board> Make(Project p, Notice n, Caps caps)
    {
        var d = p.Design;
        var boards = new List<Board>();
        try
        {
            if (n.Mode == BoardMode.Mehrsprachig && n.Languages.Count >= 2)
            {
                var langs = MultiLanguages(n);
                var r = p.MultiFor(langs.Length);
                var layout = FitMulti(d, r, langs.Select(c => MultiText(n, c)).ToList(), caps.Multi.GetValueOrDefault(langs.Length));
                boards.Add(Draw(p, layout, "01-" + string.Join("-", langs)));
            }
            else
            {
                int i = 0;
                foreach (var code in n.Languages)
                {
                    var layout = FitSingle(d, p.Single, n.Get(code), caps.Single);
                    boards.Add(Draw(p, layout, $"{++i:00}-{code}"));
                }
            }
            return boards;
        }
        catch { boards.ForEach(b => b.Dispose()); throw; }
    }

    static readonly Dictionary<string, (DateTime, Image)> Images = new(StringComparer.OrdinalIgnoreCase);
    static Image? LoadImage(Project p, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = p.Resolve(path);
        if (!File.Exists(full)) return null;
        var stamp = File.GetLastWriteTimeUtc(full);
        if (Images.TryGetValue(full, out var hit) && hit.Item1 == stamp) return hit.Item2;
        using var fs = File.OpenRead(full);
        using var ms = new MemoryStream(); fs.CopyTo(ms); ms.Position = 0;
        var img = Image.FromStream(ms, true, true);
        var copy = new Bitmap(img); img.Dispose();
        if (Images.TryGetValue(full, out var old)) old.Item2.Dispose();
        Images[full] = (stamp, copy);
        return copy;
    }

    static Board Draw(Project p, Layout layout, string name)
    {
        var d = p.Design;
        var bmp = new Bitmap(W, H);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(ParseColor(d.Background, Color.Yellow));
            if (LoadImage(p, d.BackgroundImage) is Image bg)
            {
                float s = Math.Max((float)W / bg.Width, (float)H / bg.Height);
                g.DrawImage(bg, (W - bg.Width * s) / 2, (H - bg.Height * s) / 2, bg.Width * s, bg.Height * s);
            }
            float total = layout.Pieces.Sum(x => x.Piece.Height + x.Gap);
            float y = d.CenterVertically ? d.AreaTop + Math.Max(0, (d.AreaHeight - total) / 2) : d.AreaTop;
            float cx = d.AreaLeft + d.AreaWidth / 2f;
            using var brush = new SolidBrush(ParseColor(d.TextColor, Color.Black));
            using var center = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            foreach (var (piece, gap) in layout.Pieces)
            {
                y += gap;
                foreach (var line in piece.Lines)
                {
                    var state = g.Save();
                    g.TranslateTransform(cx, y);
                    g.ScaleTransform(piece.Scale, 1);
                    g.DrawString(line, piece.Font, brush, 0, 0, center);
                    g.Restore(state);
                    y += piece.LineHeight;
                }
            }
            if (LoadImage(p, d.Logo) is Image logo && d.LogoWidth > 0)
                g.DrawImage(logo, d.LogoLeft, d.LogoTop, d.LogoWidth, d.LogoWidth * (float)logo.Height / logo.Width);
            return new Board { Name = name, Picture = bmp, Fit = layout.Fit };
        }
        catch { bmp.Dispose(); throw; }
    }

    public static Color ParseColor(string html, Color fallback)
    {
        try { return ColorTranslator.FromHtml(html.Trim()); } catch { return fallback; }
    }
}

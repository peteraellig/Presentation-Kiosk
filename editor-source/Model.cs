using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kiosk3;

/// <summary>Eine Sprache, über den dreistelligen ISO-Code (eng, deu, fra, bul …).</summary>
public class Language
{
    public string Code { get; set; } = "";
    public bool Active { get; set; } = true;
    public static readonly Dictionary<string, CultureInfo> Catalog = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Where(c => c.Name.Length > 0 && c.ThreeLetterISOLanguageName.Length == 3)
        .GroupBy(c => c.ThreeLetterISOLanguageName).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    public static string Name(string code) => Catalog.TryGetValue(code, out var c) ? $"{c.EnglishName} ({code})" : code;
    public override string ToString() => Name(Code);
}

public class Words
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Ursprungstext, aus dem diese Fassung übersetzt oder geprüft wurde. Weicht er ab, ist die Fassung veraltet.</summary>
    public string TitleSource { get; set; } = "";
    public string BodySource { get; set; } = "";
}

public enum BoardMode { ProSprache, Mehrsprachig }

public class Notice
{
    public int Number { get; set; }
    public string Label { get; set; } = "";
    public BoardMode Mode { get; set; } = BoardMode.ProSprache;
    /// <summary>Sprachen dieser Meldung in Anzeigereihenfolge (Mehrsprachig: 2–4).</summary>
    public List<string> Languages { get; set; } = new();
    public string SourceCode { get; set; } = "deu";
    /// <summary>Nur noch zum Lesen alter Projektdateien; mehrsprachige Tafeln haben keinen Titel.</summary>
    public bool TitleOnMulti { get; set; }

    /// <summary>
    /// Mehrsprachige Tafeln haben keinen Titel: vorhandene Titel werden als erste Zeile in den Text übernommen.
    /// Fassungen, die vorher aktuell waren, bleiben aktuell.
    /// </summary>
    public bool MergeTitlesIntoBody()
    {
        if (!Texts.Values.Any(t => t.Title.Trim().Length > 0)) return false;
        var wasCurrent = Texts.Keys.Where(c => c != SourceCode && !Stale(c)).ToList();
        foreach (var t in Texts.Values)
        {
            var title = t.Title.Trim();
            if (title.Length > 0) t.Body = t.Body.Trim().Length > 0 ? title + "\n" + t.Body : title;
            t.Title = "";
        }
        var s = Get(SourceCode);
        foreach (var c in wasCurrent) { Get(c).TitleSource = s.Title; Get(c).BodySource = s.Body; }
        return true;
    }
    public Dictionary<string, Words> Texts { get; set; } = new();
    public Words Get(string code)
    {
        if (!Texts.TryGetValue(code, out var t)) Texts[code] = t = new();
        return t;
    }
    /// <summary>Fassung ist veraltet, wenn der Ursprungstext seit der Übersetzung geändert wurde.</summary>
    public bool Stale(string code)
    {
        if (code == SourceCode) return false;
        var s = Get(SourceCode); var t = Get(code);
        return s.Title != t.TitleSource || s.Body != t.BodySource;
    }
    public override string ToString() => $"{Number:00}   {Label}";
}

/// <summary>Regeln für «eine Tafel pro Sprache, mit Titel».</summary>
public class SingleRule
{
    public int TitleMax { get; set; } = 110;
    public int TitleMin { get; set; } = 64;
    public int BodyMax { get; set; } = 96;
    public int BodyMin { get; set; } = 48;
    public int Gap { get; set; } = 70;
    public int MinWidth { get; set; } = 80;
    public int LineSpacing { get; set; } = 108;
    public bool Underline { get; set; } = true;
    public bool Uniform { get; set; } = true;
}

/// <summary>Regeln für eine mehrsprachige Tafel mit <see cref="Count"/> Sprachen.</summary>
public class MultiRule
{
    public int Count { get; set; }
    public int Max { get; set; }
    public int Min { get; set; }
    public int Gap { get; set; }
    public int MinWidth { get; set; } = 80;
    public int LineSpacing { get; set; } = 104;
    public bool Uniform { get; set; }
    public static MultiRule Default(int count) => count switch
    {
        2 => new() { Count = 2, Max = 92, Min = 44, Gap = 80 },
        3 => new() { Count = 3, Max = 78, Min = 38, Gap = 60 },
        _ => new() { Count = 4, Max = 66, Min = 32, Gap = 44 },
    };
}

public class Design
{
    public string Background { get; set; } = "#F3E600";
    public string BackgroundImage { get; set; } = "";
    public string TextColor { get; set; } = "#000000";
    public string Font { get; set; } = "Segoe UI";
    public bool Bold { get; set; } = true;
    public int AreaLeft { get; set; } = 80;
    public int AreaTop { get; set; } = 50;
    public int AreaWidth { get; set; } = 1760;
    public int AreaHeight { get; set; } = 820;
    public bool CenterVertically { get; set; } = true;
    public bool AutoWrap { get; set; } = true;
    public bool Balance { get; set; } = true;
    /// <summary>Passt eine Meldung mit den eigenen Zeilenumbrüchen nicht, werden diese zusammengezogen und neu umbrochen.</summary>
    public bool JoinBreaks { get; set; } = true;
    public string Logo { get; set; } = "Logo.png";
    public int LogoLeft { get; set; } = 1510;
    public int LogoTop { get; set; } = 885;
    public int LogoWidth { get; set; } = 365;
}

public class Project
{
    public int Version { get; set; } = 2;
    public string Kiosk { get; set; } = @"C:\kiosk3";
    public List<string> Languages { get; set; } = new() { "eng", "deu", "fra" };
    public List<Notice> Notices { get; set; } = new();
    public Design Design { get; set; } = new();
    public SingleRule Single { get; set; } = new();
    public List<MultiRule> Multi { get; set; } = new() { MultiRule.Default(2), MultiRule.Default(3), MultiRule.Default(4) };

    public MultiRule MultiFor(int count)
    {
        count = Math.Clamp(count, 2, 4);
        var r = Multi.FirstOrDefault(m => m.Count == count);
        if (r == null) Multi.Add(r = MultiRule.Default(count));
        return r;
    }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    /// <summary>Programmordner: Vorlage, Logo, Symbol (nur lesen).</summary>
    public static string Home => AppContext.BaseDirectory;

    /// <summary>
    /// Datenordner: Projekt.json, Uebersetzer.json, Editor.lock.
    /// Installiert (unter «Programme») ist der Programmordner schreibgeschützt → %APPDATA%\Kiosk3Editor.
    /// Die Entwicklungskopie (z. B. Z:\kiosk3\editor-source\out) speichert wie bisher neben der Exe.
    /// </summary>
    public static string Data { get; } = FindData();
    static string FindData()
    {
        var home = Path.GetFullPath(Home);
        var pf = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(x => x.Length > 0).Select(x => Path.GetFullPath(x).TrimEnd('\\') + "\\");
        if (!pf.Any(x => home.StartsWith(x, StringComparison.OrdinalIgnoreCase))) return home;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kiosk3Editor");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string FileName => Path.Combine(Data, "Projekt.json");

    /// <summary>Relative Bildpfade (z. B. «Logo.png»): zuerst im Datenordner, sonst im Programmordner.</summary>
    public string Resolve(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var inData = Path.GetFullPath(path, Data);
        return File.Exists(inData) ? inData : Path.GetFullPath(path, Home);
    }

    public static Project Load()
    {
        if (File.Exists(FileName))
        {
            var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(FileName), Json) ?? throw new Exception("Project file is empty.");
            p.Repair();
            return p;
        }
        if (LoadTemplate() is Project template) return template;
        var old = Path.Combine(Home, "Alt-Projekt.json");
        var fresh = File.Exists(old) ? FromOldEditor(File.ReadAllText(old)) : FromTemplates();
        fresh.Repair();
        return fresh;
    }

    public static string TemplateFile => Path.Combine(Home, "Vorlage-Projekt.json");

    /// <summary>Vorlage (Stand 2026-11-21 mit Peters fertigen Titeln und Texten). Grundlage bei einem Neustart ohne Projekt.json.</summary>
    public static Project? LoadTemplate()
    {
        if (!File.Exists(TemplateFile)) return null;
        var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(TemplateFile), Json);
        p?.Repair();
        return p;
    }

    /// <summary>Übernimmt Texte und Sprachen aus dem bisherigen Editor (Z:\kiosk3-editor\Projekt.json).</summary>
    static Project FromOldEditor(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var p = new Project();
        if (root["Kiosk"]?.GetValue<string>() is { Length: > 0 } k) p.Kiosk = k;
        var langs = root["Languages"]?.AsArray().OfType<JsonObject>()
            .Where(l => l["Active"]?.GetValue<bool>() != false).Select(l => l["Code"]!.GetValue<string>()).ToList();
        if (langs is { Count: > 0 }) p.Languages = langs;
        foreach (var n in root["Notices"]!.AsArray().OfType<JsonObject>())
        {
            var notice = new Notice
            {
                Number = n["Number"]!.GetValue<int>(),
                Label = n["Label"]?.GetValue<string>() ?? "",
                SourceCode = n["SourceCode"]?.GetValue<string>() ?? "deu",
                Mode = n["Separate"]?.GetValue<bool>() == true ? BoardMode.ProSprache : BoardMode.Mehrsprachig,
                Texts = n["Texts"].Deserialize<Dictionary<string, Words>>() ?? new(),
            };
            var outputs = n["OutputCodes"]?.AsArray().Select(x => x!.GetValue<string>()).ToList();
            notice.Languages = (outputs ?? p.Languages).ToList();
            // Bisher mehrsprachige Tafeln zeigten den Titel als erste Zeile: so übernehmen, damit nichts verloren geht.
            notice.TitleOnMulti = notice.Mode == BoardMode.Mehrsprachig;
            p.Notices.Add(notice);
        }
        return p;
    }

    static Project FromTemplates()
    {
        var p = new Project();
        var file = Path.Combine(Home, "Vorlagen.json");
        if (!File.Exists(file)) return p;
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsArray();
        foreach (var n in root.OfType<JsonObject>())
        {
            var notice = new Notice
            {
                Number = n["Number"]!.GetValue<int>(),
                Label = n["Label"]?.GetValue<string>() ?? "",
                Mode = n["Separate"]?.GetValue<bool>() == true ? BoardMode.ProSprache : BoardMode.Mehrsprachig,
                Texts = n["Texts"].Deserialize<Dictionary<string, Words>>() ?? new(),
                Languages = p.Languages.ToList(),
            };
            notice.TitleOnMulti = notice.Mode == BoardMode.Mehrsprachig;
            foreach (var t in notice.Texts.Values) { t.TitleSource = notice.Get("deu").Title; t.BodySource = notice.Get("deu").Body; }
            p.Notices.Add(notice);
        }
        return p;
    }

    /// <summary>Fehlende Teile ergänzen: Meldungen 1–10, Sprachlisten, Regeln für 2–4 Sprachen.</summary>
    void Repair()
    {
        // Mit dem Kiosk3-Setup liegt der Editor in <Kiosk3>\editor: dann ist der übergeordnete Ordner der Kiosk.
        var parent = Directory.GetParent(Path.GetFullPath(Home).TrimEnd('\\'));
        if (parent != null && File.Exists(Path.Combine(parent.FullName, "button_config.json")) && Directory.Exists(Path.Combine(parent.FullName, "static")))
            Kiosk = parent.FullName;
        if (Languages.Count == 0) Languages.Add("deu");
        for (int i = 1; i <= 10; i++)
            if (!Notices.Any(n => n.Number == i))
                Notices.Add(new Notice { Number = i, Label = $"Notice {i}", Languages = Languages.Take(1).ToList(), SourceCode = Languages[0] });
        Notices.Sort((a, b) => a.Number.CompareTo(b.Number));
        foreach (var n in Notices)
        {
            n.Languages = n.Languages.Where(Languages.Contains).Distinct().ToList();
            if (n.Languages.Count == 0) n.Languages.Add(Languages.Contains(n.SourceCode) ? n.SourceCode : Languages[0]);
            if (!Languages.Contains(n.SourceCode)) n.SourceCode = n.Languages[0];
            if (n.Mode == BoardMode.Mehrsprachig) n.MergeTitlesIntoBody();
            n.TitleOnMulti = false;
        }
        for (int c = 2; c <= 4; c++) MultiFor(c);
        Multi.Sort((a, b) => a.Count.CompareTo(b.Count));
    }

    public void Save()
    {
        var tmp = FileName + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        if (File.Exists(FileName)) File.Copy(FileName, FileName + ".bak", true);
        File.Move(tmp, FileName, true);
    }

    public string Snapshot() => JsonSerializer.Serialize(this, Json);
}

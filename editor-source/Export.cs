using System.Drawing.Imaging;
using System.Text.Json.Nodes;

namespace Kiosk3;

/// <summary>
/// Schreibt Tafeln nach &lt;Kiosk&gt;\static\bildN und die Tastenbeschriftung in button_config.json.
/// Alles wird zuerst vorbereitet; die alten Bilder kommen nach Editor-Sicherungen. Bei einem Fehler wird zurückgerollt.
/// </summary>
public static class Exporter
{
    static readonly HashSet<string> Media = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".mp4", ".webm", ".mov" };

    /// <summary>Fehlende Texte. Leer = alles da.</summary>
    public static List<string> Missing(Notice n)
    {
        var list = new List<string>();
        foreach (var code in n.Languages)
        {
            var t = n.Get(code);
            if (t.Body.Trim().Length == 0) list.Add($"{n.Number:00} {n.Label}: text {code} missing");
            if (n.Mode == BoardMode.ProSprache && n.Get(n.SourceCode).Title.Trim().Length > 0 && t.Title.Trim().Length == 0)
                list.Add($"{n.Number:00} {n.Label}: title {code} missing");
        }
        return list;
    }

    public static string Write(Project p, IReadOnlyList<Notice> notices, Fitter.Caps caps)
    {
        var root = Path.GetFullPath(p.Kiosk);
        var config = Path.Combine(root, "button_config.json");
        if (!File.Exists(config)) throw new Exception($"{config} not found. Check the Kiosk3 folder in tab «Project».");
        var original = File.ReadAllBytes(config);
        var data = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(original).TrimStart('﻿'))!.AsObject();
        var buttons = data["buttons"]!.AsArray();
        var staticDir = Path.Combine(root, "static");
        if (!Directory.Exists(staticDir)) throw new Exception("Kiosk3 static folder missing.");
        var work = Path.Combine(root, "Editor-Sicherungen", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var stage = Path.Combine(work, "bereit");
        Directory.CreateDirectory(stage);
        File.WriteAllBytes(Path.Combine(work, "button_config.json"), original);
        var added = new List<string>();
        var moved = new List<(string backup, string live)>();
        try
        {
            foreach (var n in notices)
            {
                if (n.Number is < 1 or > 10 || string.IsNullOrWhiteSpace(n.Label)) throw new Exception("A valid number and button label are required.");
                var folder = "bild" + n.Number;
                var match = buttons.OfType<JsonObject>().Where(b => b["type"]?.ToString() == "slideshow" &&
                    string.Equals(b["src"]?.ToString().Replace('\\', '/').Trim('/'), folder, StringComparison.OrdinalIgnoreCase)).ToList();
                if (match.Count != 1) throw new Exception($"{folder}: exactly one slideshow button in the Kiosk3 button config must point to this folder.");
                match[0]["label"] = n.Label;
                match[0]["enabled"] = true;
                var ready = Directory.CreateDirectory(Path.Combine(stage, folder)).FullName;
                var pages = Fitter.Make(p, n, caps);
                try { foreach (var b in pages) b.Picture.Save(Path.Combine(ready, b.Name + ".png"), ImageFormat.Png); }
                finally { pages.ForEach(b => b.Dispose()); }
            }
            var newConfig = Path.Combine(stage, "button_config.json");
            File.WriteAllText(newConfig, data.ToJsonString(Project.Json));
            foreach (var n in notices)
            {
                var name = "bild" + n.Number;
                var live = Directory.CreateDirectory(Path.Combine(staticDir, name)).FullName;
                var backup = Directory.CreateDirectory(Path.Combine(work, name)).FullName;
                foreach (var file in Directory.GetFiles(live).Where(f => Media.Contains(Path.GetExtension(f))))
                {
                    var dest = Path.Combine(backup, Path.GetFileName(file));
                    File.Move(file, dest);
                    moved.Add((dest, file));
                }
                foreach (var file in Directory.GetFiles(Path.Combine(stage, name)))
                {
                    var dest = Path.Combine(live, Path.GetFileName(file));
                    File.Move(file, dest);
                    added.Add(dest);
                }
            }
            if (!File.ReadAllBytes(config).SequenceEqual(original)) throw new Exception("The Kiosk3 buttons were changed meanwhile. Repeat the export.");
            File.Move(newConfig, config, true);
        }
        catch
        {
            foreach (var file in added) File.Delete(file);
            foreach (var (backup, live) in moved.AsEnumerable().Reverse()) File.Move(backup, live);
            throw;
        }
        Directory.Delete(stage, true);
        return work;
    }
}

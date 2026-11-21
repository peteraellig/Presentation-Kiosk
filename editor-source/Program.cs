namespace Kiosk3;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Prüfhilfe ohne Oberfläche: alle Tafeln in einen Ordner zeichnen (Kiosk3Editor.exe --render <Ordner>).
        // Prüfhilfe: Export in eine Kopie des Kiosk3-Ordners (Kiosk3Editor.exe --export-test <Ordner>). Speichert nichts.
        if (args.Length == 2 && args[0] == "--export-test")
        {
            var p = Project.Load();
            p.Kiosk = args[1];
            var list = p.Notices.Where(n => Exporter.Missing(n).Count == 0 && n.Languages.Any(c => n.Get(c).Body.Trim().Length > 0)).ToList();
            File.WriteAllText(Path.Combine(args[1], "export-test.txt"), Exporter.Write(p, list, Fitter.Caps.Of(p)) + "\n" + string.Join(",", list.Select(n => n.Number)));
            return;
        }
        if (args.Length == 2 && args[0] == "--render")
        {
            var p = Project.Load();
            var caps = Fitter.Caps.Of(p);
            Directory.CreateDirectory(args[1]);
            var log = new List<string> { $"Einheitlich: Einzel {caps.Single}, 2:{caps.Multi[2]}, 3:{caps.Multi[3]}, 4:{caps.Multi[4]}" };
            foreach (var n in p.Notices)
                foreach (var b in Fitter.Make(p, n, caps))
                    using (b)
                    {
                        b.Picture.Save(Path.Combine(args[1], $"bild{n.Number:00}-{b.Name}.png"));
                        log.Add($"bild{n.Number:00} {b.Name}: {(b.Fit.Fits ? "fits" : "DOES NOT FIT " + b.Fit.Problem)} · {b.Fit.Info(true)}");
                    }
            File.WriteAllLines(Path.Combine(args[1], "ergebnis.txt"), log);
            return;
        }
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        ApplicationConfiguration.Initialize();
        try
        {
            // Nur eine Instanz pro Ordner: sonst überschreiben sich zwei Editoren gegenseitig das Projekt.
            // Prüfhilfe: Fenster als Bild speichern (Kiosk3Editor.exe --shot <Datei> [Reiter] [Meldung] [uebersicht]).
            if (args.Length >= 2 && args[0] == "--shot")
            {
                var form = new MainForm(Project.Load());
                form.Shown += async (_, _) =>
                {
                    form.Prepare(args.Length > 2 ? int.Parse(args[2]) : 0, args.Length > 3 ? int.Parse(args[3]) : 0, args.Length > 4);
                    await Task.Delay(1500);
                    using var bmp = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(args[1]);
                    Environment.Exit(0);
                };
                Application.Run(form);
                return;
            }
            using var session =new FileStream(Path.Combine(Project.Data, "Editor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Application.Run(new MainForm(Project.Load()));
        }
        catch (IOException) when (File.Exists(Path.Combine(Project.Data, "Editor.lock")))
        {
            MessageBox.Show("The editor is already running.", "Kiosk3 Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "Kiosk3 · editor could not be opened", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

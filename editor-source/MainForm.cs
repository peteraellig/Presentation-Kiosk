using System.Drawing.Text;

namespace Kiosk3;

/// <summary>
/// Notfalltafel-Editor im Stil des Allocation Draw Editors: links Meldung wählen und Reiter mit Einstellungen,
/// rechts grosse Vorschau, unten Speichern / Export / CLOSE.
/// </summary>
public sealed class MainForm : Form
{
    public const string AppVersion = "3.24";
    readonly Project p;
    readonly ComboBox noticeChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 11f), Dock = DockStyle.Fill };
    readonly TabControl tabs = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9.5f) };
    readonly PreviewPanel preview = new() { Dock = DockStyle.Fill };
    readonly FlowLayoutPanel boardBar = new() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 6, 10, 0), WrapContents = false };
    readonly Label fitInfo = new() { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(14, 7, 0, 0), Font = new Font("Segoe UI", 10.5f), AutoEllipsis = true };
    readonly Label status = new() { AutoSize = false, Width = 600, Height = 36, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9.5f), AutoEllipsis = true };
    readonly CheckBox guides = new() { Text = "Guides", Checked = true, AutoSize = true, Margin = new Padding(14, 6, 0, 0) };
    readonly CheckBox overviewBox = new() { Text = "Overview of all notices", AutoSize = true, Margin = new Padding(14, 6, 0, 0) };
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 160 };
    readonly List<Button> busyButtons = new();
    readonly List<(string Code, Label Label)> textStates = new();

    List<Board> boards = new();
    int boardIndex;
    bool dirty, busy, loading;
    string saved;
    Notice Current => p.Notices[Math.Max(0, noticeChoice.SelectedIndex)];

    // Aufbau-Helfer im Stil des Allocation Draw Editors
    GroupBox? box;
    int y;
    FlowLayoutPanel? page;

    public MainForm(Project project)
    {
        p = project;
        saved = p.Snapshot();
        Text = $"Kiosk3 Emergency Board Editor {AppVersion}  © Peter Aellig";
        try { Icon = new Icon(Path.Combine(Project.Home, "kiosk.ico")); } catch { }
        Width = 1720; Height = 1000;
        MinimumSize = new Size(1250, 780);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56 };
        var save = DarkButton("Save", 130); save.Click += (_, _) => Guard(Save);
        var one = DarkButton("This notice → Kiosk3", 210); one.Click += (_, _) => Guard(() => Publish(new[] { Current }));
        var all = DarkButton("All notices → Kiosk3", 210); all.Click += (_, _) => Guard(() => Publish(p.Notices));
        var close = new Button { Text = "CLOSE", Width = 110, Height = 36, BackColor = Color.IndianRed, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();
        save.Location = new Point(12, 10); one.Location = new Point(150, 10); all.Location = new Point(368, 10);
        status.Location = new Point(590, 10);
        bottom.Controls.AddRange(new Control[] { save, one, all, status, close });
        bottom.Resize += (_, _) => { close.Location = new Point(bottom.Width - 122, 10); status.Width = Math.Max(100, bottom.Width - 722); };
        busyButtons.AddRange(new[] { save, one, all });

        var canvas = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(200, 222, 242) };
        canvas.Controls.Add(preview);
        canvas.Controls.Add(fitInfo);
        canvas.Controls.Add(boardBar);
        fitInfo.BackColor = Color.FromArgb(232, 240, 248);
        boardBar.BackColor = Color.FromArgb(232, 240, 248);

        var left = new Panel { Dock = DockStyle.Left, Width = 450 };
        var pick = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(6, 8, 6, 4) };
        pick.Controls.Add(noticeChoice);
        left.Controls.Add(tabs);
        left.Controls.Add(pick);

        Controls.Add(canvas);
        Controls.Add(left);
        Controls.Add(bottom);

        foreach (var n in p.Notices) noticeChoice.Items.Add(n);
        noticeChoice.SelectedIndexChanged += (_, _) => { if (!loading) { Rebuild(); } };
        guides.CheckedChanged += (_, _) => { preview.Guides = guides.Checked; preview.Invalidate(); };
        overviewBox.CheckedChanged += (_, _) => Render();
        preview.OverviewClicked += i => { overviewBox.Checked = false; noticeChoice.SelectedIndex = i; };
        refresh.Tick += (_, _) => { refresh.Stop(); Render(); };
        FormClosing += OnClosing;
        FormClosed += (_, _) => { refresh.Dispose(); ClearBoards(); };

        loading = true; noticeChoice.SelectedIndex = 0; loading = false;
        Rebuild();
    }

    /// <summary>Für die Prüfhilfe --shot: Reiter, Meldung und Übersicht vorwählen.</summary>
    public void Prepare(int tab, int notice, bool overview)
    {
        noticeChoice.SelectedIndex = Math.Clamp(notice, 0, p.Notices.Count - 1);
        tabs.SelectedIndex = Math.Clamp(tab, 0, tabs.TabCount - 1);
        overviewBox.Checked = overview;
    }

    // ----- Allgemeine Helfer ------------------------------------------------------------

    static Button DarkButton(string text, int width) => new()
    {
        Text = text, Width = width, Height = 36, BackColor = Color.FromArgb(52, 58, 64), ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), FlatAppearance = { BorderSize = 0 },
    };

    void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) { status.Text = ex.Message; MessageBox.Show(this, ex.Message, "Kiosk3 Editor", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    void Changed()
    {
        if (loading) return;
        dirty = true;
        refresh.Stop(); refresh.Start();
    }

    void Info(string text) { status.Text = text; }

    // ----- Aufbau der Reiter ------------------------------------------------------------

    void Rebuild()
    {
        int tab = Math.Max(0, tabs.SelectedIndex);
        loading = true;
        tabs.SuspendLayout();
        foreach (TabPage tp in tabs.TabPages) tp.Dispose();
        tabs.TabPages.Clear();
        textStates.Clear();
        BuildNotice();
        BuildRules();
        BuildDesign();
        BuildProject();
        tabs.ResumeLayout();
        tabs.SelectedIndex = Math.Min(tab, tabs.TabCount - 1);
        loading = false;
        UpdateTextStates();
        Render();
    }

    void Page(string name)
    {
        var tp = new TabPage(name) { BackColor = SystemColors.Control };
        page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(4) };
        tp.Controls.Add(page);
        tabs.TabPages.Add(tp);
    }

    void Group(string title)
    {
        box = new GroupBox { Text = title, Width = 408, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(4, 4, 4, 8) };
        page!.Controls.Add(box);
        y = 22;
        box.Height = y + 10;
    }

    void Row(Control c, int height = 0)
    {
        c.Location = new Point(10, y + 2);
        c.Width = 386;
        c.Font = new Font("Segoe UI", 9.5f);
        if (height > 0) c.Height = height;
        box!.Controls.Add(c);
        y += c.Height + 6;
        box.Height = y + 10;
    }

    void Row(string label, Control c)
    {
        box!.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(10, y + 7), Font = new Font("Segoe UI", 9.5f) });
        c.Location = new Point(232, y + 3);
        c.Width = 164;
        c.Font = new Font("Segoe UI", 10f);
        box.Controls.Add(c);
        y += 34;
        box.Height = y + 10;
    }

    void Hint(string text)
    {
        var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(386, 0), ForeColor = Color.DimGray };
        Row(l);
        l.Font = new Font("Segoe UI", 8.5f);
        y = l.Bottom + 6; box!.Height = y + 10;
    }

    NumericUpDown Number(string label, int min, int max, Func<int> get, Action<int> set, string unit = "")
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(get(), min, max) };
        n.ValueChanged += (_, _) => { set((int)n.Value); Changed(); };
        Row(unit.Length > 0 ? $"{label} ({unit})" : label, n);
        return n;
    }

    CheckBox Check(string text, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Text = text, Checked = get(), AutoSize = false, Height = 26 };
        c.CheckedChanged += (_, _) => { set(c.Checked); Changed(); };
        Row(c, 26);
        return c;
    }

    TextBox Field(string label, Func<string> get, Action<string> set)
    {
        var t = new TextBox { Text = get() };
        t.TextChanged += (_, _) => { set(t.Text); Changed(); };
        Row(label, t);
        return t;
    }

    Button SmallButton(string text, Action a)
    {
        var b = new Button { Text = text, AutoSize = false, Height = 30 };
        b.Click += (_, _) => Guard(a);
        Row(b, 30);
        return b;
    }

    // ----- Reiter «Meldung» -------------------------------------------------------------

    void BuildNotice()
    {
        var n = Current;
        Page("Notice");
        Group($"Button bild{n.Number}");
        Field("Button label", () => n.Label, v => { n.Label = v; noticeChoice.Items[noticeChoice.SelectedIndex] = n; });

        Group("Board type");
        var single = new RadioButton { Text = "One board per language, with title", Checked = n.Mode == BoardMode.ProSprache, Height = 26 };
        var multi = new RadioButton { Text = "Multilingual board (2–4 languages stacked)", Checked = n.Mode == BoardMode.Mehrsprachig, Height = 26 };
        Row(single, 26); Row(multi, 26);
        single.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            n.Mode = single.Checked ? BoardMode.ProSprache : BoardMode.Mehrsprachig;
            if (n.Mode == BoardMode.Mehrsprachig && n.Languages.Count > 4) n.Languages = n.Languages.Take(4).ToList();
            if (n.Mode == BoardMode.Mehrsprachig && n.MergeTitlesIntoBody())
                Info("Multilingual boards have no title: the titles are now the first line of the text.");
            dirty = true;
            BeginInvoke(Rebuild);
        };
        if (n.Mode == BoardMode.Mehrsprachig)
        {
            Hint("No title: per language the whole text goes into the text field, line breaks with Enter. Font sizes: tab «Rules», section for "
                 + Math.Clamp(n.Languages.Count, 2, 4) + " languages.");
        }

        Group(n.Mode == BoardMode.Mehrsprachig ? "Languages (2–4, order as in tab Project)" : "Languages (one board each)");
        var list = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        foreach (var code in p.Languages) list.Items.Add(new Language { Code = code }, n.Languages.Contains(code));
        Row(list, Math.Min(150, 22 + p.Languages.Count * 20));
        list.ItemCheck += (_, e) =>
        {
            if (loading) return;
            var chosen = Enumerable.Range(0, list.Items.Count)
                .Where(i => i == e.Index ? e.NewValue == CheckState.Checked : list.GetItemChecked(i))
                .Select(i => ((Language)list.Items[i]).Code).ToList();
            if (chosen.Count == 0 || (n.Mode == BoardMode.Mehrsprachig && chosen.Count > 4))
            {
                e.NewValue = e.CurrentValue;
                Info(chosen.Count == 0 ? "At least one language must be selected." : "A multilingual board has at most 4 languages.");
                return;
            }
            n.Languages = chosen;
            dirty = true;
            BeginInvoke(Rebuild);
        };
        var source = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var code in p.Languages) source.Items.Add(new Language { Code = code });
        source.SelectedIndex = Math.Max(0, p.Languages.IndexOf(n.SourceCode));
        source.SelectedIndexChanged += (_, _) => { if (!loading) { n.SourceCode = ((Language)source.SelectedItem!).Code; dirty = true; BeginInvoke(Rebuild); } };
        Row("Source language", source);
        if (n.Mode == BoardMode.Mehrsprachig && n.Languages.Count < 2)
            Hint("⚠ Only one language selected – this makes a single board. Select at least 2 for multilingual.");

        var order = n.Languages.OrderBy(c => c == n.SourceCode ? 0 : 1).ToList();
        foreach (var code in order)
        {
            var t = n.Get(code);
            Group(Language.Name(code) + (code == n.SourceCode ? "  · source" : ""));
            var state = new Label { AutoSize = false, Height = 20, ForeColor = Color.DimGray };
            if (code != n.SourceCode) { Row(state, 20); textStates.Add((code, state)); }
            bool showTitle = n.Mode == BoardMode.ProSprache || n.Languages.Count < 2;
            if (showTitle)
            {
                Row(new Label { Text = "Title (underlined)", AutoSize = false, Height = 18 }, 18);
                var title = new TextBox { Multiline = true, AcceptsReturn = true, Text = t.Title.ReplaceLineEndings(), ScrollBars = ScrollBars.Vertical };
                Row(title, 48);
                title.TextChanged += (_, _) => EditText(n, code, title.Text, null);
            }
            Row(new Label { Text = "Text (Enter = own line break)", AutoSize = false, Height = 18 }, 18);
            var body = new TextBox { Multiline = true, AcceptsReturn = true, Text = t.Body.ReplaceLineEndings(), ScrollBars = ScrollBars.Vertical };
            Row(body, 92);
            body.TextChanged += (_, _) => EditText(n, code, null, body.Text);
            var engine = Translation.Settings.UseDeepL ? "DeepL" : "MyMemory";
            busyButtons.RemoveAll(b => b.IsDisposed);
            if (code == n.SourceCode)
            {
                if (n.Languages.Count > 1)
                {
                    busyButtons.Add(SmallButton($"Translate into the other languages ({engine})", () => Translate(new[] { n })));
                    Hint("Translates empty and outdated versions. Versions edited by hand count as checked and are kept.");
                }
            }
            else
            {
                var (again, accept) = ButtonPair($"Translate again ({engine})", "Mark as checked");
                busyButtons.Add(again);
                again.Click += (_, _) =>
                {
                    var has = n.Get(code).Body.Trim().Length > 0;
                    if (has && MessageBox.Show(this, $"Translate {Language.Name(code)} again from {Language.Name(n.SourceCode)}? The current text will be replaced.",
                            "Translate again", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    Translate(new[] { n }, code);
                };
                accept.Click += (_, _) =>
                {
                    var s = n.Get(n.SourceCode); t.TitleSource = s.Title; t.BodySource = s.Body; dirty = true; UpdateTextStates();
                };
            }
        }
    }

    /// <summary>Zwei Knöpfe nebeneinander in einer Zeile.</summary>
    (Button, Button) ButtonPair(string left, string right)
    {
        var panel = new Panel { Height = 30 };
        var a = new Button { Text = left, Width = 190, Height = 30, Location = new Point(0, 0) };
        var b = new Button { Text = right, Width = 190, Height = 30, Location = new Point(196, 0) };
        panel.Controls.Add(a); panel.Controls.Add(b);
        Row(panel, 30);
        return (a, b);
    }

    void EditText(Notice n, string code, string? title, string? body)
    {
        if (loading) return;
        var t = n.Get(code); var s = n.Get(n.SourceCode);
        if (title != null) { t.Title = title.ReplaceLineEndings("\n"); if (code != n.SourceCode) t.TitleSource = s.Title; }
        if (body != null) { t.Body = body.ReplaceLineEndings("\n"); if (code != n.SourceCode) t.BodySource = s.Body; }
        UpdateTextStates();
        Changed();
    }

    void UpdateTextStates()
    {
        var n = Current;
        foreach (var (code, label) in textStates)
        {
            if (label.IsDisposed) continue;
            var t = n.Get(code);
            bool missing = t.Body.Trim().Length == 0;
            label.Text = missing ? "✗ Text missing – translate or type it in" : n.Stale(code) ? "⚠ Outdated – source text was changed" : "✓ Up to date";
            label.ForeColor = missing || n.Stale(code) ? Color.FromArgb(170, 0, 0) : Color.DimGray;
            label.Font = new Font("Segoe UI", 9f, missing || n.Stale(code) ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    // ----- Reiter «Regeln» --------------------------------------------------------------

    void BuildRules()
    {
        Page("Rules");
        Group("How text is fitted");
        Hint("For each font size, from large to small:\n"
           + "1. Lines that are too wide are condensed – at most down to «Condense to».\n"
           + "2. If that is not enough, lines are wrapped (balanced).\n"
           + "3. If it is then too tall, the font gets smaller – down to the minimum.\n"
           + "«Uniform»: all notices of this kind get the same size – the largest at which every one fits.");

        var s = p.Single;
        Group("One board per language (with title)");
        Number("Title largest size", 10, 400, () => s.TitleMax, v => s.TitleMax = v, "px");
        Number("Title smallest size", 10, 400, () => s.TitleMin, v => s.TitleMin = v, "px");
        Number("Text largest size", 10, 400, () => s.BodyMax, v => s.BodyMax = v, "px");
        Number("Text smallest size", 10, 400, () => s.BodyMin, v => s.BodyMin = v, "px");
        Number("Gap title – text", 0, 400, () => s.Gap, v => s.Gap = v, "px");
        Number("Condense to", 50, 100, () => s.MinWidth, v => s.MinWidth = v, "%");
        Number("Line spacing", 80, 200, () => s.LineSpacing, v => s.LineSpacing = v, "%");
        Check("Underline title", () => s.Underline, v => s.Underline = v);
        Check("Uniform for all notices of this kind", () => s.Uniform, v => s.Uniform = v);
        SmallButton("Reset to default", () => { p.Single = DefaultSingle(); AfterReset("board per language"); });

        foreach (var rule in p.Multi.OrderBy(m => m.Count).ToList())
        {
            var r = rule;
            Group($"Multilingual – {r.Count} languages");
            Number("Largest size", 10, 400, () => r.Max, v => r.Max = v, "px");
            Number("Smallest size", 10, 400, () => r.Min, v => r.Min = v, "px");
            Number("Gap between languages", 0, 400, () => r.Gap, v => r.Gap = v, "px");
            Number("Condense to", 50, 100, () => r.MinWidth, v => r.MinWidth = v, "%");
            Number("Line spacing", 80, 200, () => r.LineSpacing, v => r.LineSpacing = v, "%");
            Check("Uniform for all notices of this kind", () => r.Uniform, v => r.Uniform = v);
            int count = r.Count;
            SmallButton("Reset to default", () =>
            {
                p.Multi[p.Multi.FindIndex(m => m.Count == count)] = DefaultMulti(count);
                AfterReset($"multilingual {count} languages");
            });
        }

        Group("Defaults");
        SmallButton("Reset all rules to default", SuggestRules);
        Hint("Sets all rules to values that suit the height of the text area (based on 820 px). Then check the overview.");
    }

    void SuggestRules()
    {
        if (MessageBox.Show(this, "Replace all rules (font sizes, gaps, condensing) with the default values?", "Kiosk3 Editor",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        p.Single = DefaultSingle();
        p.Multi = Enumerable.Range(2, 3).Select(DefaultMulti).ToList();
        AfterReset("all rules");
    }

    // Standardwerte, auf die Höhe des Textbereichs umgerechnet (Grundlage 820 px).
    int Scaled(int v) => Math.Max(10, (int)Math.Round(v * p.Design.AreaHeight / 820.0));

    SingleRule DefaultSingle()
    {
        var d = new SingleRule();
        d.TitleMax = Scaled(d.TitleMax); d.TitleMin = Scaled(d.TitleMin);
        d.BodyMax = Scaled(d.BodyMax); d.BodyMin = Scaled(d.BodyMin); d.Gap = Scaled(d.Gap);
        return d;
    }

    MultiRule DefaultMulti(int count)
    {
        var m = MultiRule.Default(count);
        m.Max = Scaled(m.Max); m.Min = Scaled(m.Min); m.Gap = Scaled(m.Gap);
        return m;
    }

    void AfterReset(string what)
    {
        dirty = true;
        BeginInvoke(Rebuild);
        Info($"Default restored: {what}.");
    }

    // ----- Reiter «Gestaltung» ----------------------------------------------------------

    void BuildDesign()
    {
        var d = p.Design;
        Page("Design");
        Group("Background");
        ColorRow("Background colour", () => d.Background, v => d.Background = v);
        FileRow("Background image (empty = colour)", () => d.BackgroundImage, v => d.BackgroundImage = v);

        Group("Font");
        var fonts = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        using (var installed = new InstalledFontCollection())
            foreach (var f in installed.Families) fonts.Items.Add(f.Name);
        if (!fonts.Items.Contains(d.Font)) fonts.Items.Insert(0, d.Font);
        fonts.SelectedItem = d.Font;
        fonts.SelectedIndexChanged += (_, _) => { d.Font = (string)fonts.SelectedItem!; Changed(); };
        Row("Typeface", fonts);
        Check("Bold", () => d.Bold, v => d.Bold = v);
        ColorRow("Text colour", () => d.TextColor, v => d.TextColor = v);

        Group("Text area (pixels on 1920 × 1080)");
        Number("Left", 0, 1900, () => d.AreaLeft, v => d.AreaLeft = v);
        Number("Top", 0, 1060, () => d.AreaTop, v => d.AreaTop = v);
        Number("Width", 100, 1920, () => d.AreaWidth, v => d.AreaWidth = v);
        Number("Height", 100, 1080, () => d.AreaHeight, v => d.AreaHeight = v);
        Check("Centre vertically (else top)", () => d.CenterVertically, v => d.CenterVertically = v);
        Check("Wrap automatically", () => d.AutoWrap, v => d.AutoWrap = v);
        Check("Balance wrapped lines", () => d.Balance, v => d.Balance = v);
        Check("Join own line breaks if it does not fit otherwise", () => d.JoinBreaks, v => d.JoinBreaks = v);
        Hint("The blue dashed frame in the preview shows the text area. It should not cover the logo.");

        Group("Logo");
        FileRow("Logo file (empty = no logo)", () => d.Logo, v => d.Logo = v);
        Number("Left", 0, 1920, () => d.LogoLeft, v => d.LogoLeft = v);
        Number("Top", 0, 1080, () => d.LogoTop, v => d.LogoTop = v);
        Number("Width", 0, 1920, () => d.LogoWidth, v => d.LogoWidth = v);
    }

    void ColorRow(string label, Func<string> get, Action<string> set)
    {
        var panel = new Panel { Height = 28 };
        var text = new TextBox { Text = get(), Width = 104, Location = new Point(0, 1) };
        var swatch = new Button { Width = 54, Height = 27, Location = new Point(110, 0), FlatStyle = FlatStyle.Flat, BackColor = Fitter.ParseColor(get(), Color.White) };
        text.TextChanged += (_, _) => { set(text.Text.Trim()); swatch.BackColor = Fitter.ParseColor(text.Text, swatch.BackColor); Changed(); };
        swatch.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = swatch.BackColor, FullOpen = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) text.Text = ColorTranslator.ToHtml(Color.FromArgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
        };
        panel.Controls.Add(text); panel.Controls.Add(swatch);
        Row(label, panel);
    }

    void FileRow(string label, Func<string> get, Action<string> set)
    {
        Row(new Label { Text = label, AutoSize = false, Height = 18 }, 18);
        var panel = new Panel { Height = 28 };
        var text = new TextBox { Text = get(), Width = 300, Location = new Point(0, 1) };
        var browse = new Button { Text = "…", Width = 40, Height = 27, Location = new Point(306, 0) };
        var clear = new Button { Text = "✕", Width = 36, Height = 27, Location = new Point(350, 0) };
        text.TextChanged += (_, _) => { set(text.Text.Trim()); Changed(); };
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" };
            if (dlg.ShowDialog(this) == DialogResult.OK) text.Text = dlg.FileName;
        };
        clear.Click += (_, _) => text.Text = "";
        panel.Controls.AddRange(new Control[] { text, browse, clear });
        Row(panel, 28);
    }

    // ----- Reiter «Projekt» -------------------------------------------------------------

    void BuildProject()
    {
        Page("Project");
        Group("Project languages (order as on board)");
        var list = new ListBox { IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        foreach (var code in p.Languages) list.Items.Add(new Language { Code = code });
        Row(list, Math.Min(170, 24 + p.Languages.Count * 20));
        var bar = new FlowLayoutPanel { Height = 34, WrapContents = false };
        void Btn(string text, Action a) { var b = new Button { Text = text, AutoSize = true, Height = 30 }; b.Click += (_, _) => Guard(a); bar.Controls.Add(b); }
        Btn("Add …", () =>
        {
            using var picker = new LanguagePicker();
            if (picker.ShowDialog(this) != DialogResult.OK || picker.Selection is not Language l) return;
            if (p.Languages.Contains(l.Code)) { Info($"{l} is already in the project."); return; }
            p.Languages.Add(l.Code); dirty = true; Rebuild();
        });
        Btn("▲", () => MoveLanguage(list.SelectedIndex, -1));
        Btn("▼", () => MoveLanguage(list.SelectedIndex, +1));
        Btn("Remove", () =>
        {
            if (list.SelectedItem is not Language l) return;
            if (p.Languages.Count == 1) throw new Exception("At least one language must remain.");
            if (MessageBox.Show(this, $"Remove {l} from the project? The texts stay saved but no longer appear on any board.", "Kiosk3 Editor",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            p.Languages.Remove(l.Code);
            foreach (var n in p.Notices)
            {
                n.Languages.Remove(l.Code);
                if (n.Languages.Count == 0) n.Languages.Add(p.Languages[0]);
                if (n.SourceCode == l.Code) n.SourceCode = n.Languages[0];
            }
            dirty = true; Rebuild();
        });
        Row(bar, 34);
        Hint("New languages are not added to notices automatically – tick them per notice in tab «Notice».");

        Group("Kiosk3");
        var panel = new Panel { Height = 28 };
        var path = new TextBox { Text = p.Kiosk, Width = 340, Location = new Point(0, 1) };
        var browse = new Button { Text = "…", Width = 40, Height = 27, Location = new Point(346, 0) };
        path.TextChanged += (_, _) => { p.Kiosk = path.Text.Trim(); dirty = true; };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = p.Kiosk };
            if (dlg.ShowDialog(this) == DialogResult.OK) path.Text = dlg.SelectedPath;
        };
        panel.Controls.Add(path); panel.Controls.Add(browse);
        Row(new Label { Text = "Kiosk3 folder (contains button_config.json and static)", AutoSize = false, Height = 18 }, 18);
        Row(panel, 28);
        Hint("On export the boards go to static\\bild1 … bild10, the button labels to button_config.json. The old images are moved to Editor-Sicherungen first.");

        BuildTranslator();

        Group("All notices");
        busyButtons.Add(SmallButton("Translate all missing and outdated versions", () => Translate(p.Notices)));

        Group("Template");
        SmallButton("Restore template (all notices, rules, design)", RestoreTemplate);
        Hint("The template is the finished set of notices with all titles and texts (Vorlage-Projekt.json). "
           + "It is also used automatically when the editor starts without a project. The Kiosk3 folder is kept.");
    }

    void RestoreTemplate()
    {
        var t = Project.LoadTemplate() ?? throw new Exception($"Template not found: {Project.TemplateFile}");
        if (MessageBox.Show(this, "Replace all notices, texts, rules and design with the template?\nChanges made since then are lost once you save.",
                "Restore template", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        p.Languages = t.Languages; p.Notices = t.Notices; p.Design = t.Design; p.Single = t.Single; p.Multi = t.Multi;
        int index = Math.Max(0, noticeChoice.SelectedIndex);
        loading = true;
        noticeChoice.Items.Clear();
        foreach (var n in p.Notices) noticeChoice.Items.Add(n);
        noticeChoice.SelectedIndex = Math.Min(index, p.Notices.Count - 1);
        loading = false;
        dirty = true;
        BeginInvoke(Rebuild);
        Info("Template restored. Click Save to keep it.");
    }

    /// <summary>Übersetzer wählen und DeepL-Schlüssel eintragen. Wird sofort lokal gespeichert (Uebersetzer.json), nicht im Projekt.</summary>
    void BuildTranslator()
    {
        var s = Translation.Settings;
        Group("Translator");
        var engine = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        engine.Items.AddRange(new object[] { "DeepL", "MyMemory" });
        engine.SelectedItem = s.Engine == "MyMemory" ? "MyMemory" : "DeepL";
        Row("Service", engine);

        Row(new Label { Text = "DeepL key (API key)", AutoSize = false, Height = 18 }, 18);
        var panel = new Panel { Height = 28 };
        var key = new TextBox { Text = s.DeepLKey, Width = 300, Location = new Point(0, 1), UseSystemPasswordChar = true };
        var show = new CheckBox { Text = "show", AutoSize = true, Location = new Point(308, 4) };
        panel.Controls.Add(key); panel.Controls.Add(show);
        Row(panel, 28);
        var result = new Label { AutoSize = true, MaximumSize = new Size(386, 0), ForeColor = Color.DimGray };
        var test = SmallButton("Check key", () => { });
        Row(result);

        void Persist()
        {
            try { s.Save(); }
            catch (Exception ex) { result.Text = "✗ Could not save Uebersetzer.json: " + ex.Message; }
        }
        engine.SelectedIndexChanged += (_, _) => { s.Engine = (string)engine.SelectedItem!; Persist(); };
        key.TextChanged += (_, _) => { s.DeepLKey = key.Text.Trim(); result.Text = ""; Persist(); };
        show.CheckedChanged += (_, _) => key.UseSystemPasswordChar = !show.Checked;
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            result.Text = "Checking …";
            try { result.Text = await Translation.DeepLUsage(s.DeepLKey); }
            catch (Exception ex) { result.Text = "✗ " + ex.Message; }
            finally { if (!test.IsDisposed) test.Enabled = true; }
        };
        Hint("Free account: deepl.com → «DeepL API Free». Enter the key here yourself. It stays on this PC only "
           + "(Uebersetzer.json next to the editor) and is never passed on with the project or the export. "
           + "Without a key, or if DeepL does not answer, MyMemory translates – the editor tells you."); 
    }

    void MoveLanguage(int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || to < 0 || to >= p.Languages.Count) return;
        (p.Languages[index], p.Languages[to]) = (p.Languages[to], p.Languages[index]);
        foreach (var n in p.Notices) n.Languages = p.Languages.Where(n.Languages.Contains).ToList();
        dirty = true;
        Rebuild();
    }

    // ----- Vorschau ---------------------------------------------------------------------

    void ClearBoards()
    {
        preview.ShowBoard(null, p.Design);
        boards.ForEach(b => b.Dispose());
        boards.Clear();
    }

    void Render()
    {
        if (loading || IsDisposed) return;
        try
        {
            var caps = Fitter.Caps.Of(p);
            if (overviewBox.Checked) { RenderOverview(caps); return; }
            var old = boards;
            boards = Fitter.Make(p, Current, caps);
            boardIndex = Math.Clamp(boardIndex, 0, boards.Count - 1);
            preview.ShowBoard(boards[boardIndex].Picture, p.Design);
            old.ForEach(b => b.Dispose());
            BuildBoardBar();
            ShowFit();
        }
        catch (Exception ex) { fitInfo.Text = "✗ " + ex.Message; }
    }

    void RenderOverview(Fitter.Caps caps)
    {
        var items = new List<(string, Bitmap, bool)>();
        foreach (var n in p.Notices)
        {
            var list = Fitter.Make(p, n, caps);
            try
            {
                var thumb = new Bitmap(480, 270);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(list[0].Picture, 0, 0, 480, 270);
                }
                bool textOk = Exporter.Missing(n).Count == 0, sizeOk = list.All(b => b.Fit.Fits);
                var fits = textOk && sizeOk;
                var prefix = !textOk ? "TEXT MISSING · " : !sizeOk ? "DOES NOT FIT · " : "";
                items.Add(($"{prefix}{n.Number:00} {n.Label} · {(n.Mode == BoardMode.Mehrsprachig && n.Languages.Count > 1 ? n.Languages.Count + " lang." : list.Count + " boards")}", thumb, fits));
            }
            finally { list.ForEach(b => b.Dispose()); }
        }
        ClearBoards();
        boardBar.Controls.Clear();
        boardBar.Controls.Add(guides); boardBar.Controls.Add(overviewBox);
        preview.ShowOverview(items);
        fitInfo.Text = "Overview: first board of each notice. Click a notice to open it.";
        fitInfo.ForeColor = Color.Black;
    }

    void BuildBoardBar()
    {
        boardBar.SuspendLayout();
        boardBar.Controls.Clear();
        for (int i = 0; i < boards.Count; i++)
        {
            int index = i;
            var b = boards[i];
            var name = b.Name[3..].ToUpperInvariant().Replace("-", " · ");
            var btn = new Button
            {
                Text = (b.Fit.Fits ? "" : "✗ ") + name, AutoSize = true, Height = 28, FlatStyle = FlatStyle.Flat,
                BackColor = i == boardIndex ? Color.FromArgb(52, 58, 64) : Color.White,
                ForeColor = i == boardIndex ? Color.White : (b.Fit.Fits ? Color.Black : Color.FromArgb(170, 0, 0)),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 0, 6, 0),
            };
            btn.Click += (_, _) => { boardIndex = index; preview.ShowBoard(boards[index].Picture, p.Design); BuildBoardBar(); ShowFit(); };
            boardBar.Controls.Add(btn);
        }
        boardBar.Controls.Add(guides);
        boardBar.Controls.Add(overviewBox);
        boardBar.ResumeLayout();
    }

    void ShowFit()
    {
        if (boards.Count == 0) return;
        var b = boards[boardIndex];
        var n = Current;
        bool hasTitle = n.Mode == BoardMode.ProSprache || boards.Count == 1 && n.Languages.Count == 1;
        var missing = Exporter.Missing(n);
        var uniform = n.Mode == BoardMode.Mehrsprachig && n.Languages.Count > 1 ? p.MultiFor(n.Languages.Count).Uniform : p.Single.Uniform;
        var text = b.Fit.Fits ? "✓ Fits · " + b.Fit.Info(hasTitle) : "✗ DOES NOT FIT · " + b.Fit.Problem + " · " + b.Fit.Info(hasTitle);
        if (uniform) text += " · uniform";
        if (missing.Count > 0) text += " · ⚠ " + missing[0];
        fitInfo.Text = text;
        fitInfo.ForeColor = b.Fit.Fits && missing.Count == 0 ? Color.Black : Color.FromArgb(170, 0, 0);
        fitInfo.Font = new Font("Segoe UI", 10.5f, b.Fit.Fits && missing.Count == 0 ? FontStyle.Regular : FontStyle.Bold);
    }

    // ----- Speichern, Übersetzen, Export ------------------------------------------------

    void Save()
    {
        p.Save();
        saved = p.Snapshot();
        dirty = false;
        Info($"Saved: {Project.FileName}");
    }

    void SetBusy(bool on)
    {
        busy = on;
        foreach (var b in busyButtons.Where(b => !b.IsDisposed)) b.Enabled = !on;
        UseWaitCursor = on;
    }

    /// <summary>Übersetzt leere und veraltete Fassungen; mit <paramref name="only"/> genau diese Sprache, auch wenn sie aktuell ist.</summary>
    async void Translate(IEnumerable<Notice> notices, string? only = null)
    {
        if (busy) return;
        SetBusy(true);
        int count = 0;
        var engines = new HashSet<string>();
        Translation.FallbackReason = null;
        try
        {
            foreach (var n in notices.ToList())
            {
                var s = n.Get(n.SourceCode);
                if (s.Body.Trim().Length == 0) continue;
                foreach (var code in n.Languages.Where(c => c != n.SourceCode && (only == null || c == only)))
                {
                    var t = n.Get(code);
                    bool force = only != null;
                    bool needTitle = s.Title.Length > 0 && (force || t.Title.Trim().Length == 0 || t.TitleSource != s.Title);
                    bool needBody = force || t.Body.Trim().Length == 0 || t.BodySource != s.Body;
                    Info($"Translating {n.Label} → {code} …");
                    if (needTitle) { t.Title = await Translation.Get(s.Title, n.SourceCode, code); t.TitleSource = s.Title; count++; engines.Add(Translation.LastEngine); }
                    if (needBody) { t.Body = await Translation.Get(s.Body, n.SourceCode, code); t.BodySource = s.Body; count++; engines.Add(Translation.LastEngine); }
                }
            }
            dirty |= count > 0;
            var msg = count == 0 ? "Nothing to translate – all versions are up to date."
                : $"{count} texts translated with {string.Join(" and ", engines)}. Please read them and adjust by hand if needed.";
            if (count > 0 && Translation.FallbackReason != null)
            {
                msg += $"  ⚠ MyMemory was used instead: {Translation.FallbackReason}";
                MessageBox.Show(this, $"DeepL could not be used:\n{Translation.FallbackReason}\n\nMyMemory was used instead.",
                    "Translate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Info(msg);
        }
        catch (Exception ex) { Info(ex.Message); MessageBox.Show(this, ex.Message, "Translate", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { SetBusy(false); if (!IsDisposed) Rebuild(); }
    }

    void Publish(IEnumerable<Notice> chosen)
    {
        if (busy) return;
        var list = chosen.Where(n => n.Languages.Any(c => n.Get(c).Body.Trim().Length > 0)).ToList();
        if (list.Count == 0) throw new Exception("No notice with text found.");
        var missing = list.SelectMany(Exporter.Missing).ToList();
        if (missing.Count > 0) throw new Exception("Texts missing:\n" + string.Join("\n", missing.Take(12)));
        var caps = Fitter.Caps.Of(p);
        var problems = new List<string>();
        var stale = list.SelectMany(n => n.Languages.Where(n.Stale).Select(c => $"{n.Number:00} {n.Label}: {c} outdated")).ToList();
        foreach (var n in list)
        {
            var b = Fitter.Make(p, n, caps);
            problems.AddRange(b.Where(x => !x.Fit.Fits).Select(x => $"{n.Number:00} {n.Label} · {x.Name[3..]}: {x.Fit.Problem}"));
            b.ForEach(x => x.Dispose());
        }
        var question = $"Write {list.Count} notice(s) to {Path.Combine(p.Kiosk, "static")}?\n" +
                       string.Join(", ", list.Select(n => "bild" + n.Number)) + "\n\nThe current images in these folders are moved to Editor-Sicherungen.";
        if (problems.Count > 0) question += "\n\n✗ DOES NOT FIT (will be cut off):\n" + string.Join("\n", problems.Take(10));
        if (stale.Count > 0) question += "\n\n⚠ Translation outdated:\n" + string.Join("\n", stale.Take(10));
        if (MessageBox.Show(this, question, "Send to Kiosk3", MessageBoxButtons.YesNo,
                problems.Count + stale.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return;
        SetBusy(true);
        try
        {
            var backup = Exporter.Write(p, list, caps);
            Save();
            Info($"{list.Count} notice(s) sent to Kiosk3. Backup: {backup}");
        }
        finally { SetBusy(false); }
    }

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (busy) { e.Cancel = true; return; }
        if (!dirty && p.Snapshot() == saved) return;
        var answer = MessageBox.Show(this, "Save changes?", "Kiosk3 Editor", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) e.Cancel = true;
        if (answer == DialogResult.Yes) try { Save(); } catch (Exception ex) { MessageBox.Show(this, ex.Message); e.Cancel = true; }
    }
}

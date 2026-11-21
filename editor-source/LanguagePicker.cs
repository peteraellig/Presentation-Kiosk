using System.Globalization;

namespace Kiosk3;

sealed class LanguagePicker : Form
{
    readonly TextBox search = new() { Dock = DockStyle.Top, PlaceholderText = "Type a language, language code or country code" };
    readonly ListBox results = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly Button choose = new() { Text = "Select", AutoSize = true, DialogResult = DialogResult.OK };
    readonly Label count = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 4) };
    static readonly Dictionary<string, HashSet<string>> Countries = CountryIndex();
    public Language? Selection => results.SelectedItem as Language;

    static Dictionary<string, HashSet<string>> CountryIndex()
    {
        var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            RegionInfo region;
            try { region = new RegionInfo(culture.Name); }
            catch (ArgumentException) { continue; }
            var key = region.ThreeLetterISORegionName;
            if (!index.TryGetValue(key, out var codes)) index[key] = codes = new(StringComparer.OrdinalIgnoreCase);
            codes.Add(culture.ThreeLetterISOLanguageName);
        }
        return index;
    }

    public LanguagePicker()
    {
        Text = "Choose language";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(540, 500);
        MinimumSize = new Size(450, 320);
        Padding = new Padding(14);
        Font = new Font("Segoe UI", 10);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(cancel);
        actions.Controls.Add(choose);
        count.Dock = DockStyle.Top;
        Controls.Add(results);
        Controls.Add(count);
        Controls.Add(search);
        Controls.Add(actions);
        AcceptButton = choose;
        CancelButton = cancel;
        results.DoubleClick += (_, _) => { if (Selection != null) DialogResult = DialogResult.OK; };
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, e) => {
            if (e.KeyCode == Keys.Down && results.Items.Count > 0) { results.Focus(); e.SuppressKeyPress = true; }
        };
        Shown += (_, _) => search.Focus();
        Filter();
    }

    void Filter()
    {
        var query = search.Text.Trim();
        Countries.TryGetValue(query, out var regional);
        var matches = Language.Catalog.Where(entry => {
            var c = entry.Value;
            return c.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || c.NativeName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || c.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Key.Equals(query, StringComparison.OrdinalIgnoreCase)
                || c.TwoLetterISOLanguageName.Equals(query, StringComparison.OrdinalIgnoreCase)
                || regional?.Contains(entry.Key) == true;
        }).OrderByDescending(entry => entry.Key.Equals(query, StringComparison.OrdinalIgnoreCase))
          .ThenBy(entry => entry.Value.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        results.BeginUpdate();
        results.Items.Clear();
        foreach (var item in matches) results.Items.Add(new Language { Code = item.Key });
        results.EndUpdate();
        choose.Enabled = results.Items.Count > 0;
        if (choose.Enabled) results.SelectedIndex = 0;
        count.Text = results.Items.Count switch { 0 => "No matching language found", 1 => "1 language", _ => $"{results.Items.Count} languages" };
    }
}

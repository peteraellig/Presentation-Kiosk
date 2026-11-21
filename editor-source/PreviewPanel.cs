using System.Drawing.Drawing2D;

namespace Kiosk3;

/// <summary>Vorschau der 1920 × 1080-Tafel auf hellblauem Grund, wahlweise mit Hilfslinien oder als Übersicht aller Meldungen.</summary>
public sealed class PreviewPanel : Panel
{
    Bitmap? board;
    Design? design;
    List<(string Label, Bitmap Thumb, bool Fits)>? overview;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Guides { get; set; } = true;
    public event Action<int>? OverviewClicked;

    public PreviewPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(200, 222, 242);
        ResizeRedraw = true;
    }

    public void ShowBoard(Bitmap? picture, Design d)
    {
        board = picture; design = d; ClearOverview(); Invalidate();
    }

    public void ShowOverview(List<(string, Bitmap, bool)> items)
    {
        ClearOverview(); overview = items; Invalidate();
    }

    void ClearOverview()
    {
        if (overview == null) return;
        foreach (var o in overview) o.Thumb.Dispose();
        overview = null;
    }

    protected override void Dispose(bool disposing) { if (disposing) ClearOverview(); base.Dispose(disposing); }

    (int cols, int rows, float w, float h) Grid()
    {
        int n = overview!.Count, cols = Width > Height * 1.4 ? 4 : 3, rows = (n + cols - 1) / cols;
        float w = (Width - 16f) / cols, h = (Height - 16f) / Math.Max(1, rows);
        return (cols, rows, w, h);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (overview == null) return;
        var (cols, _, w, h) = Grid();
        int i = (int)((e.Y - 8) / h) * cols + (int)((e.X - 8) / w);
        if (i >= 0 && i < overview.Count) OverviewClicked?.Invoke(i);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (overview != null) { PaintOverview(g); return; }
        if (board == null || design == null) return;
        float s = Math.Min((Width - 28f) / 1920, (Height - 28f) / 1080);
        if (s <= 0) return;
        float ox = (Width - 1920 * s) / 2, oy = (Height - 1080 * s) / 2;
        g.DrawImage(board, ox, oy, 1920 * s, 1080 * s);
        using (var frame = new Pen(Color.FromArgb(90, 90, 90))) g.DrawRectangle(frame, ox - 1, oy - 1, 1920 * s + 1, 1080 * s + 1);
        if (!Guides) return;
        using var pen = new Pen(Color.FromArgb(160, 0, 70, 200), 1.5f) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(pen, ox + design.AreaLeft * s, oy + design.AreaTop * s, design.AreaWidth * s, design.AreaHeight * s);
        using var logoPen = new Pen(Color.FromArgb(140, 120, 120, 120), 1f) { DashStyle = DashStyle.Dot };
        g.DrawRectangle(logoPen, ox + design.LogoLeft * s, oy + design.LogoTop * s, design.LogoWidth * s, 1080 * s - design.LogoTop * s - 2);
    }

    void PaintOverview(Graphics g)
    {
        var (cols, _, w, h) = Grid();
        using var font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        for (int i = 0; i < overview!.Count; i++)
        {
            var (label, thumb, fits) = overview[i];
            float x = 8 + (i % cols) * w, y = 8 + (i / cols) * h;
            float tw = w - 12, th = tw * 9 / 16;
            if (th > h - 30) { th = h - 30; tw = th * 16 / 9; }
            if (tw <= 0 || th <= 0) continue;
            float tx = x + (w - tw) / 2;
            g.DrawImage(thumb, tx, y + 22, tw, th);
            // Nicht nur Farbe: Zeichen ✓/✗ und bei Problemen ein dicker Rahmen.
            using var pen = new Pen(fits ? Color.FromArgb(90, 90, 90) : Color.FromArgb(190, 20, 20), fits ? 1 : 4);
            g.DrawRectangle(pen, tx, y + 22, tw, th);
            TextRenderer.DrawText(g, (fits ? "✓ " : "✗ ") + label, font, new Rectangle((int)tx, (int)y + 2, (int)tw, 20),
                fits ? Color.Black : Color.FromArgb(160, 0, 0), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }
}

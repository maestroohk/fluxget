using System.Drawing.Drawing2D;

namespace FluxGet.Desktop;

internal sealed class Splash : Control
{
    private readonly Image? _logo;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly bool _dark;
    private float _phase;

    public Splash(bool dark)
    {
        _dark = dark;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = dark ? Color.FromArgb(12, 13, 15) : Color.FromArgb(245, 244, 240);
        Dock = DockStyle.Fill;
        using var stream = typeof(Splash).Assembly.GetManifestResourceStream("FluxGet.Desktop.fluxget.png");
        _logo = stream is null ? null : Image.FromStream(stream);
        _timer.Tick += (_, _) =>
        {
            _phase = (_phase + 0.012f) % 1f;
            Invalidate(BarBounds());
        };
        _timer.Start();
    }

    private float Dpi => DeviceDpi / 96f;

    private Rectangle BarBounds()
    {
        var w = (int)(140 * Dpi);
        return new Rectangle((Width - w) / 2, Height / 2 + (int)(64 * Dpi), w, (int)(3 * Dpi) + 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var s = Dpi;
        var size = (int)(56 * s);
        if (_logo is not null) g.DrawImage(_logo, (Width - size) / 2, Height / 2 - size + (int)(4 * s), size, size);
        using var font = new Font("Segoe UI Semibold", 15f);
        var text = "FluxGet";
        var ink = _dark ? Color.FromArgb(237, 236, 232) : Color.FromArgb(23, 24, 28);
        var measured = g.MeasureString(text, font);
        using (var brush = new SolidBrush(ink)) g.DrawString(text, font, brush, (Width - measured.Width) / 2, Height / 2 + 14 * s);

        var bar = BarBounds();
        using (var track = new SolidBrush(_dark ? Color.FromArgb(36, 38, 43) : Color.FromArgb(226, 224, 218)))
            g.FillRectangle(track, bar);
        var seg = bar.Width / 3;
        var x = bar.Left + (int)((bar.Width + seg) * _phase) - seg;
        var visible = Rectangle.Intersect(bar, new Rectangle(x, bar.Top, seg, bar.Height));
        using (var accent = new SolidBrush(_dark ? Color.FromArgb(255, 110, 67) : Color.FromArgb(226, 82, 42)))
            if (!visible.IsEmpty) g.FillRectangle(accent, visible);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _logo?.Dispose();
        }
        base.Dispose(disposing);
    }
}

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FluxGet.Desktop.Player;

internal static class Native
{
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void RoundCorners(IntPtr hwnd, bool small = false)
    {
        var pref = small ? 3 : 2;
        DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
    }

    public static void DarkTitle(IntPtr hwnd)
    {
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }
}

internal sealed class TransparentHit : NativeWindow
{
    public TransparentHit(Control control)
    {
        if (control.IsHandleCreated) AssignHandle(control.Handle);
        control.HandleCreated += (_, _) => AssignHandle(control.Handle);
    }

    public bool Enabled { get; set; }

    protected override void WndProc(ref Message m)
    {
        if (Enabled && m.Msg == 0x0084)
        {
            m.Result = -1;
            return;
        }
        base.WndProc(ref m);
    }
}

internal sealed class OverlayForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(20, 21, 24) };
    private readonly PlayerController _player;
    private readonly string _url;
    private readonly CoreWebView2Environment _env;
    private readonly string _cookieKey;
    private readonly Action<string> _sink;

    public OverlayForm(PlayerController player, CoreWebView2Environment env, string url, string cookieKey)
    {
        _player = player;
        _env = env;
        _url = url;
        _cookieKey = cookieKey;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(20, 21, 24);
        Size = new Size(900, 104);
        Controls.Add(_web);
        _sink = json => { if (IsHandleCreated && _web.CoreWebView2 is not null) BeginInvoke(() => _web.CoreWebView2?.PostWebMessageAsJson(json)); };
        Load += async (_, _) => await Init();
    }

    public int DesiredHeight { get; private set; } = 104;
    public event Action? HeightChanged;
    public event Action<JsonElement>? Command;
    public bool PointerInside => Bounds.Contains(Cursor.Position);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
    }

    private async Task Init()
    {
        await _web.EnsureCoreWebView2Async(_env);
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        var cookie = core.CookieManager.CreateCookie(Host.AccessKeys.CookieName, _cookieKey, "127.0.0.1", "/");
        cookie.IsHttpOnly = true;
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
        core.CookieManager.AddOrUpdateCookie(cookie);
        core.WebMessageReceived += (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "overlaySize" && root.TryGetProperty("height", out var h))
            {
                var dpi = DeviceDpi / 96.0;
                DesiredHeight = (int)Math.Ceiling(h.GetDouble() * dpi);
                HeightChanged?.Invoke();
            }
            else if (type == "player") Command?.Invoke(root.Clone());
        };
        core.NavigationCompleted += (_, _) => _player.Push();
        _player.AddSink(_sink);
        core.Navigate(_url);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _player.RemoveSink(_sink);
        _web.Dispose();
        base.OnFormClosed(e);
    }
}

internal sealed class FullscreenForm : Form
{
    private readonly PlayerController _player;
    private readonly OverlayForm _overlay;
    private readonly System.Windows.Forms.Timer _idle = new() { Interval = 250 };
    private readonly TransparentHit _hit;
    private Point _lastCursor;
    private long _lastActivity = Environment.TickCount64;
    private bool _cursorHidden;

    public FullscreenForm(PlayerController player, OverlayForm overlay, Screen screen)
    {
        _player = player;
        _overlay = overlay;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        BackColor = Color.Black;
        KeyPreview = true;
        Text = "FluxGet";
        Icon = Owner?.Icon;
        _hit = new TransparentHit(player.View);
        _idle.Tick += (_, _) => OnIdleTick();
        _overlay.HeightChanged += PlaceOverlay;
        _overlay.Command += msg => BeginInvoke(() => { Wake(); Commands.Invoke(msg); });
        Shown += (_, _) =>
        {
            _overlay.Show(this);
            PlaceOverlay();
            _idle.Start();
            Activate();
        };
    }

    public event Action<JsonElement> Commands = _ => { };
    public event Action? ExitRequested;

    public void Adopt()
    {
        _ = Handle;
        _player.View.Parent = this;
        _player.View.Dock = DockStyle.Fill;
        _player.View.Visible = true;
        _player.View.DoubleClick += OnViewDoubleClick;
        _player.View.MouseClick += OnViewClick;
    }

    public void Release()
    {
        _player.View.DoubleClick -= OnViewDoubleClick;
        _player.View.MouseClick -= OnViewClick;
        _player.View.Dock = DockStyle.None;
        if (_cursorHidden) { Cursor.Show(); _cursorHidden = false; }
    }

    private void OnViewDoubleClick(object? s, EventArgs e) => ExitRequested?.Invoke();
    private void OnViewClick(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) _player.Toggle(); Wake(); }

    private void PlaceOverlay()
    {
        var width = Math.Min(980, Width - 64);
        var height = Math.Max(72, _overlay.DesiredHeight);
        _overlay.Bounds = new Rectangle(Left + (Width - width) / 2, Bottom - height - 28, width, height);
    }

    private void Wake()
    {
        _lastActivity = Environment.TickCount64;
        if (!_overlay.Visible) { _overlay.Show(this); PlaceOverlay(); }
        if (_cursorHidden) { Cursor.Show(); _cursorHidden = false; }
    }

    private void OnIdleTick()
    {
        var pos = Cursor.Position;
        if (pos != _lastCursor) { _lastCursor = pos; Wake(); return; }
        var paused = _player.Engine.State == LibVLCSharp.Shared.VLCState.Paused;
        if (!paused && !_overlay.PointerInside && Environment.TickCount64 - _lastActivity > 2600)
        {
            if (_overlay.Visible) _overlay.Hide();
            if (!_cursorHidden && Bounds.Contains(pos)) { Cursor.Hide(); _cursorHidden = true; }
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Wake();
        if (Shortcuts.Handle(_player, keyData, out var mode))
        {
            if (mode is PlayerMode.Embedded) ExitRequested?.Invoke();
            return true;
        }
        if (keyData is Keys.Escape or Keys.F) { ExitRequested?.Invoke(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _idle.Dispose();
        _overlay.Close();
        if (_cursorHidden) Cursor.Show();
        base.OnFormClosed(e);
    }
}

internal sealed class MiniForm : Form
{
    private readonly PlayerController _player;
    private readonly Panel _strip = new() { Dock = DockStyle.Bottom, Height = 40, BackColor = Color.FromArgb(230, 20, 21, 24), Visible = false };
    private readonly Button _play;
    private readonly Button _pin;
    private readonly TransparentHit _hit;
    private readonly System.Windows.Forms.Timer _hover = new() { Interval = 200 };

    public MiniForm(PlayerController player, Icon? icon)
    {
        _player = player;
        Text = "FluxGet mini player";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        MinimumSize = new Size(260, 150);
        TopMost = true;
        ShowInTaskbar = true;
        KeyPreview = true;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var scale = DeviceDpi / 96.0;
        Size = new Size((int)(420 * scale), (int)(236 * scale));
        Location = new Point(area.Right - Width - (int)(24 * scale), area.Bottom - Height - (int)(24 * scale));

        _play = Glyph("", "Play / pause", (_, _) => { _player.Toggle(); UpdateGlyphs(); });
        var back = Glyph("", "Back to FluxGet", (_, _) => ReturnRequested?.Invoke());
        _pin = Glyph("", "Keep on top", (_, _) => { TopMost = !TopMost; UpdateGlyphs(); });
        var close = Glyph("", "Close mini player", (_, _) => ReturnRequested?.Invoke());
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6, 4, 6, 4), BackColor = Color.Transparent };
        flow.Controls.AddRange([_play, back, _pin]);
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 48, Padding = new Padding(0, 4, 6, 4), BackColor = Color.Transparent };
        right.Controls.Add(close);
        _strip.Controls.Add(flow);
        _strip.Controls.Add(right);
        Controls.Add(_strip);
        _hit = new TransparentHit(player.View);
        _hover.Tick += (_, _) =>
        {
            var inside = Bounds.Contains(Cursor.Position);
            if (_strip.Visible != inside) _strip.Visible = inside;
            if (inside) UpdateGlyphs();
        };
        _hover.Start();
    }

    public event Action? ReturnRequested;

    private Button Glyph(string glyph, string tip, EventHandler onClick)
    {
        var b = new Button
        {
            Text = glyph,
            Font = new Font("Segoe Fluent Icons", 11f),
            ForeColor = Color.FromArgb(237, 236, 232),
            FlatStyle = FlatStyle.Flat,
            Size = new Size(36, 30),
            Margin = new Padding(2, 0, 2, 0),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            TabStop = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 50, 56);
        b.Click += onClick;
        new ToolTip().SetToolTip(b, tip);
        return b;
    }

    private void UpdateGlyphs()
    {
        _play.Text = _player.Engine.IsPlaying ? "" : "";
        _pin.ForeColor = TopMost ? Color.FromArgb(255, 110, 67) : Color.FromArgb(237, 236, 232);
    }

    public void Adopt()
    {
        _ = Handle;
        _player.View.Parent = this;
        _player.View.Dock = DockStyle.Fill;
        _player.View.Visible = true;
        _player.View.SendToBack();
        _hit.Enabled = true;
    }

    public void Release()
    {
        _hit.Enabled = false;
        _player.View.Dock = DockStyle.None;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDBLCLK = 0xA3;
        if (m.Msg == WM_NCHITTEST)
        {
            var p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
            const int edge = 8;
            var left = p.X < edge; var right = p.X >= Width - edge; var top = p.Y < edge; var bottom = p.Y >= Height - edge;
            m.Result = (top, bottom, left, right) switch
            {
                (true, _, true, _) => 13,
                (true, _, _, true) => 14,
                (_, true, true, _) => 16,
                (_, true, _, true) => 17,
                (true, _, _, _) => 12,
                (_, true, _, _) => 15,
                (_, _, true, _) => 10,
                (_, _, _, true) => 11,
                _ => 2
            };
            return;
        }
        if (m.Msg == WM_NCLBUTTONDBLCLK)
        {
            ReturnRequested?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { ReturnRequested?.Invoke(); return true; }
        if (Shortcuts.Handle(_player, keyData, out _)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _hover.Dispose();
        base.OnFormClosed(e);
    }
}

internal static class Shortcuts
{
    public static bool Handle(PlayerController p, Keys key, out PlayerMode? mode)
    {
        mode = null;
        switch (key)
        {
            case Keys.Space: case Keys.K: p.Toggle(); break;
            case Keys.Left: p.SeekBy(-5000); break;
            case Keys.Right: p.SeekBy(5000); break;
            case Keys.Left | Keys.Shift: p.SeekBy(-30000); break;
            case Keys.Right | Keys.Shift: p.SeekBy(30000); break;
            case Keys.Up: p.Engine.Volume = Math.Min(200, p.Engine.Volume + 5); break;
            case Keys.Down: p.Engine.Volume = Math.Max(0, p.Engine.Volume - 5); break;
            case Keys.M: p.Engine.Mute = !p.Engine.Mute; break;
            case Keys.OemPeriod: p.Engine.NextFrame(); break;
            case Keys.N: p.Handle(JsonDocument.Parse("{\"cmd\":\"next\"}").RootElement); break;
            case Keys.P: p.Handle(JsonDocument.Parse("{\"cmd\":\"prev\"}").RootElement); break;
            case Keys.OemOpenBrackets: p.Handle(JsonDocument.Parse("{\"cmd\":\"rateStep\",\"value\":-1}").RootElement); break;
            case Keys.OemCloseBrackets: p.Handle(JsonDocument.Parse("{\"cmd\":\"rateStep\",\"value\":1}").RootElement); break;
            default: return false;
        }
        p.Push();
        return true;
    }
}

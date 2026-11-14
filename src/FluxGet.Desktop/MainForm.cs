using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluxGet.Engine;
using FluxGet.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FluxGet.Desktop;

internal sealed class MainForm : Form
{
    private readonly WebApplication _server;
    private readonly JobManager _manager;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(12, 13, 15) };
    private readonly NotifyIcon _tray;
    private readonly string _origin = $"http://127.0.0.1:{Program.Port}";
    private string? _pendingArgument;
    private bool _exiting;
    private bool _hintShown;

    public MainForm(WebApplication server, string? argument)
    {
        _server = server;
        _manager = server.Services.GetRequiredService<JobManager>();
        _pendingArgument = argument;
        Text = "FluxGet";
        Icon = LoadIcon();
        MinimumSize = new Size(900, 600);
        Size = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 13, 15);
        Controls.Add(_web);

        _tray = new NotifyIcon { Icon = Icon, Text = "FluxGet", Visible = true, ContextMenuStrip = BuildTrayMenu() };
        _tray.DoubleClick += (_, _) => ShowWindow();

        _manager.JobChanged += OnJobChanged;
        server.Services.GetRequiredService<AppSignals>().ActivateRequested += arg => BeginInvoke(() => Activate(arg));
        Load += async (_, _) => await InitializeWebView();
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("FluxGet.Desktop.fluxget.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open FluxGet", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pause all", null, (_, _) => { foreach (var j in _manager.Unfinished()) _manager.Pause(j.Id); });
        menu.Items.Add("Resume all", null, (_, _) => { foreach (var j in _manager.Unfinished().Where(j => j.Status == JobStatus.Paused)) _manager.Resume(j.Id); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit FluxGet", null, (_, _) => { _exiting = true; Close(); });
        return menu;
    }

    private async Task InitializeWebView()
    {
        var dataDir = Path.Combine(new FluxGetHostOptions().ResolveDataDirectory(), "WebView2");
        CoreWebView2Environment env;
        try
        {
            env = await CoreWebView2Environment.CreateAsync(null, dataDir);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("FluxGet needs the Microsoft Edge WebView2 Runtime, which is included with Windows 11. Install it from microsoft.com/edge/webview2 and start FluxGet again.", "FluxGet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _exiting = true;
            Close();
            return;
        }
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = Debugger.IsAttached;
        settings.AreDevToolsEnabled = Debugger.IsAttached || Environment.GetEnvironmentVariable("FLUXGET_DEVTOOLS") == "1";
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        var key = _server.Services.GetRequiredService<AccessKeys>().SessionKey;
        var cookie = core.CookieManager.CreateCookie(AccessKeys.CookieName, key, "127.0.0.1", "/");
        cookie.IsHttpOnly = true;
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
        core.CookieManager.AddOrUpdateCookie(cookie);

        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith(_origin + "/", StringComparison.OrdinalIgnoreCase) || e.Uri == _origin) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, _) =>
        {
            if (_pendingArgument is { } arg)
            {
                _pendingArgument = null;
                Activate(arg);
            }
        };
        core.Navigate(_origin + "/");
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true });
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(_origin, StringComparison.OrdinalIgnoreCase)) return;
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "pickFolder":
                using (var dialog = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Choose a download folder", ShowNewFolderButton = true })
                {
                    if (doc.RootElement.TryGetProperty("current", out var c) && Directory.Exists(c.GetString())) dialog.InitialDirectory = c.GetString()!;
                    var path = dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
                    _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "folder", path }));
                }
                break;
            case "theme":
                SetDarkTitleBar(doc.RootElement.TryGetProperty("dark", out var d) && d.GetBoolean());
                break;
        }
    }

    private void Activate(string? argument)
    {
        ShowWindow();
        if (string.IsNullOrWhiteSpace(argument) || _web.CoreWebView2 is null) return;
        string? url = null;
        if (argument.StartsWith("fluxget:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(argument, UriKind.Absolute, out var u))
        {
            var query = System.Web.HttpUtility.ParseQueryString(u.Query);
            url = query["url"];
        }
        else if (argument.StartsWith("http", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            url = argument;
        }
        if (url is not null) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "add", url }));
    }

    private void ShowWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void OnJobChanged(Job job)
    {
        if (!_manager.Settings.NotifyOnComplete) return;
        var name = job.FileName ?? "Download";
        if (job.Status == JobStatus.Completed && job.CompletedAt is { } done && DateTime.UtcNow - done < TimeSpan.FromSeconds(5))
            BeginInvoke(() => _tray.ShowBalloonTip(4000, "Download complete", name, ToolTipIcon.Info));
        else if (job.Status == JobStatus.NeedsAttention)
            BeginInvoke(() => _tray.ShowBalloonTip(5000, "Download needs attention", $"{name}: {job.Error}", ToolTipIcon.Warning));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = true;
        try { dark = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") is 0; } catch { }
        SetDarkTitleBar(dark);
    }

    private void SetDarkTitleBar(bool dark)
    {
        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref value, sizeof(int));
        _web.DefaultBackgroundColor = dark ? Color.FromArgb(12, 13, 15) : Color.FromArgb(245, 244, 240);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                _tray.ShowBalloonTip(3000, "FluxGet is still running", "Downloads continue in the background. Right-click the tray icon to quit.", ToolTipIcon.Info);
            }
            return;
        }
        _manager.JobChanged -= OnJobChanged;
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

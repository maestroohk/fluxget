using System.Runtime.InteropServices;

namespace FluxGet.Setup;

internal enum Page { Choose, Installing, Done, Failed, Updating }

internal static unsafe class Ui
{
    private const uint Bg = 0x0F0D0C, Surface = 0x1A1716, Line = 0x2D2826, Ink = 0xE8ECED, Muted = 0x98908D, Accent = 0x436EFF, AccentHover = 0x5C82FF, Dark = 0x181511;
    private const int W = 560, H = 360;

    public static Page Page = Page.Choose;
    public static volatile string? Error;
    public static string Version = "";
    private static string? _notice;
    private static nint _hwnd, _icon;
    private static float _scale = 1;
    private static int _hover = -1;
    private static float _phase;
    private static bool _picking;
    private static readonly List<(int Id, Rect Box)> Hits = [];
    private const int Close = 1, Primary = 2, Secondary = 3, Change = 4;

    public static int Run()
    {
        var instance = Native.GetModuleHandleW(null);
        fixed (char* cls = "FluxGetSetup")
        fixed (char* title = Page == Page.Updating ? "FluxGet" : "FluxGet Setup")
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                style = 0x0003 | 0x00020000,
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hCursor = Native.LoadCursorW(0, 32512),
                lpszClassName = cls,
                hIcon = Native.LoadIconW(instance, (char*)32512)
            };
            Native.RegisterClassExW(&wc);
            var dpi = Native.GetDpiForSystem();
            _scale = dpi / 96f;
            int w = S(W), h = S(H);
            var x = (Native.GetSystemMetrics(0) - w) / 2;
            var y = (Native.GetSystemMetrics(1) - h) / 2;
            _hwnd = Native.CreateWindowExW(0x00040000, cls, title, 0x80000000 | 0x00080000 | 0x00020000, x, y, w, h, 0, 0, instance, 0);
        }
        Program.SetWindow(_hwnd);
        int corner = 2, dark = 1;
        Native.DwmSetWindowAttribute(_hwnd, 33, &corner, 4);
        Native.DwmSetWindowAttribute(_hwnd, 20, &dark, 4);
        var margins = stackalloc int[] { 1, 1, 1, 1 };
        Native.DwmExtendFrameIntoClientArea(_hwnd, margins);
        nint large = 0;
        var size = S(44);
        Native.PrivateExtractIconsW(Environment.ProcessPath!, 0, size, size, &large, null, 1, 0);
        _icon = large;
        if (Page == Page.Updating) Native.SetTimer(_hwnd, 1, 16, 0);
        Native.ShowWindow(_hwnd, 5);
        Native.UpdateWindow(_hwnd);
        Native.MSG msg;
        while (Native.GetMessageW(&msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(&msg);
            Native.DispatchMessageW(&msg);
        }
        return Page == Page.Done ? 0 : 1;
    }

    private static int S(float v) => (int)Math.Round(v * _scale);

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case 0x0014:
                return 1;
            case 0x000F:
                Paint(hwnd);
                return 0;
            case 0x0084:
            {
                var r = Native.Client(hwnd);
                var pt = new Native.POINT { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
                Native.ScreenToClient(hwnd, &pt);
                if (pt.Y < S(64) && HitAt(pt.X, pt.Y) < 0) return 2;
                return 1;
            }
            case 0x0200:
            {
                var id = HitAt((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                if (id != _hover) { _hover = id; Native.InvalidateRect(hwnd, null, 0); }
                var track = new Native.TRACKMOUSEEVENT { cbSize = (uint)sizeof(Native.TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = hwnd };
                Native.TrackMouseEvent(&track);
                Native.SetCursor(Native.LoadCursorW(0, id >= 0 ? 32649 : 32512));
                return 0;
            }
            case 0x02A3:
                if (_hover >= 0) { _hover = -1; Native.InvalidateRect(hwnd, null, 0); }
                return 0;
            case 0x0202:
                Click(HitAt((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)));
                return 0;
            case 0x0100:
                if (wParam == 0x0D) Click(Primary);
                if (wParam == 0x1B) Click(Page == Page.Installing ? -1 : Close);
                return 0;
            case 0x0113:
                _phase = (_phase + 0.012f) % 1f;
                Native.InvalidateRect(hwnd, null, 0);
                return 0;
            case Native.WM_APP:
                Native.KillTimer(hwnd, 1);
                Page = Error is null ? Page.Done : Page.Failed;
                Native.InvalidateRect(hwnd, null, 0);
                return 0;
            case 0x0010:
                if (Page == Page.Installing) return 0;
                Native.DestroyWindow(hwnd);
                return 0;
            case 0x0002:
                Native.PostQuitMessage(0);
                return 0;
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static int HitAt(int x, int y)
    {
        foreach (var (id, box) in Hits) if (x >= box.L && x < box.R && y >= box.T && y < box.B) return id;
        return -1;
    }

    private static void Click(int id)
    {
        switch (id)
        {
            case Close:
                if (Page != Page.Installing) Native.PostMessageW(_hwnd, 0x0010, 0, 0);
                break;
            case Change when Page == Page.Choose && !Program.Updating && !_picking:
                _picking = true;
                _notice = Program.ChangeFolder();
                _picking = false;
                Native.InvalidateRect(_hwnd, null, 0);
                break;
            case Primary:
                if (Page == Page.Choose)
                {
                    Page = Page.Installing;
                    _notice = null;
                    Native.SetTimer(_hwnd, 1, 16, 0);
                    Program.Install();
                }
                else if (Page == Page.Done)
                {
                    Program.Launch();
                    Native.PostMessageW(_hwnd, 0x0010, 0, 0);
                }
                else if (Page == Page.Failed) Native.PostMessageW(_hwnd, 0x0010, 0, 0);
                Native.InvalidateRect(_hwnd, null, 0);
                break;
            case Secondary:
                Native.PostMessageW(_hwnd, 0x0010, 0, 0);
                break;
        }
    }

    private static void Paint(nint hwnd)
    {
        Native.PAINTSTRUCT ps;
        var dc = Native.BeginPaint(hwnd, &ps);
        var client = Native.Client(hwnd);
        var mem = Native.CreateCompatibleDC(dc);
        var bmp = Native.CreateCompatibleBitmap(dc, client.R, client.B);
        var old = Native.SelectObject(mem, bmp);
        Hits.Clear();
        Fill(mem, client, Bg);
        Native.SetBkMode(mem, 1);

        var pad = S(28);
        if (_icon != 0) Native.DrawIconEx(mem, pad, S(22), _icon, S(44), S(44), 0, 0, 3);
        Text(mem, "FluxGet", new Rect(pad + S(56), S(22), S(400), S(48)), 18, 600, Ink, 0x24);
        Text(mem, Page == Page.Done ? "Installed" : Page == Page.Installing ? "Installing" : Page == Page.Updating || Program.Updating ? "Update" : "Setup", new Rect(pad + S(56), S(46), S(400), S(68)), 12.5f, 400, Muted, 0x24);

        var close = new Rect(client.R - S(46), S(14), client.R - S(14), S(46));
        if (Page is not (Page.Installing or Page.Updating))
        {
            Hits.Add((Close, close));
            if (_hover == Close) Round(mem, close, Line, S(8));
            Text(mem, "✕", close, 12, 400, _hover == Close ? Ink : Muted, 0x25);
        }

        var top = S(96);
        switch (Page)
        {
            case Page.Choose:
                Text(mem, Program.Updating ? "Update FluxGet" : "Install FluxGet", new Rect(pad, top, client.R - pad, top + S(30)), 17, 600, Ink, 0x24);
                Text(mem, Program.Updating ? "FluxGet is already on this PC and will be updated where it is. Your downloads, settings and library stay as they are."
                    : "Downloads, video and photo saving, torrents, and a player with subtitles made on your own computer. Your downloads and settings are kept in a separate folder, so updating or uninstalling never removes them.",
                    new Rect(pad, top + S(32), client.R - pad, top + S(84)), 12.5f, 400, Muted, 0x10);
                Text(mem, "INSTALL LOCATION", new Rect(pad, top + S(98), client.R - pad, top + S(114)), 10, 600, Muted, 0x24);
                var box = new Rect(pad, top + S(118), client.R - pad - (Program.Updating ? 0 : S(108)), top + S(152));
                Round(mem, box, Surface, S(8), Line);
                Text(mem, Program.Target, new Rect(box.L + S(12), box.T, box.R - S(12), box.B), 12.5f, 400, Ink, 0x24 | 0x8000);
                if (!Program.Updating)
                {
                    var change = new Rect(box.R + S(8), box.T, client.R - pad, box.B);
                    Hits.Add((Change, change));
                    Round(mem, change, _hover == Change ? Line : Surface, S(8), Line);
                    Text(mem, "Change…", change, 12.5f, 600, Ink, 0x25);
                }
                Text(mem, _notice ?? Program.FreeSpaceText(), new Rect(pad, top + S(158), client.R - pad, top + S(196)), 11.5f, 400, _notice is null ? Muted : 0x4FB4F5, 0x10);
                Buttons(mem, client, pad, Program.Updating ? "Update" : "Install", "Cancel");
                break;
            case Page.Updating:
                Text(mem, Version.Length > 0 ? $"Updating FluxGet to {Version}…" : "Updating FluxGet…", new Rect(pad, top + S(20), client.R - pad, top + S(50)), 17, 600, Ink, 0x24);
                Text(mem, "FluxGet reopens by itself in a moment. Downloads, settings and your library stay as they are.", new Rect(pad, top + S(54), client.R - pad, top + S(96)), 12.5f, 400, Muted, 0x10);
                Bar(mem, new Rect(pad, top + S(116), client.R - pad, top + S(120)));
                break;
            case Page.Installing:
                Text(mem, Program.Updating ? "Updating FluxGet…" : "Installing FluxGet…", new Rect(pad, top + S(20), client.R - pad, top + S(50)), 17, 600, Ink, 0x24);
                Text(mem, "This takes about a minute. You can keep using your PC.", new Rect(pad, top + S(54), client.R - pad, top + S(76)), 12.5f, 400, Muted, 0x24);
                Bar(mem, new Rect(pad, top + S(104), client.R - pad, top + S(108)));
                Text(mem, Program.Target, new Rect(pad, top + S(122), client.R - pad, top + S(142)), 11.5f, 400, Muted, 0x24 | 0x8000);
                break;
            case Page.Done:
                Text(mem, "FluxGet is ready", new Rect(pad, top + S(20), client.R - pad, top + S(50)), 17, 600, Ink, 0x24);
                Text(mem, "It's in the Start menu and on your desktop. To download from your browser, open Settings › Use FluxGet in your browser.",
                    new Rect(pad, top + S(54), client.R - pad, top + S(100)), 12.5f, 400, Muted, 0x10);
                Buttons(mem, client, pad, "Start FluxGet", "Close");
                break;
            case Page.Failed:
                Text(mem, "Setup didn't finish", new Rect(pad, top + S(20), client.R - pad, top + S(50)), 17, 600, Ink, 0x24);
                Text(mem, Error ?? "", new Rect(pad, top + S(54), client.R - pad, top + S(120)), 12.5f, 400, Muted, 0x10);
                Buttons(mem, client, pad, "Close", null);
                break;
        }

        Native.BitBlt(dc, 0, 0, client.R, client.B, mem, 0, 0, 0x00CC0020);
        Native.SelectObject(mem, old);
        Native.DeleteObject(bmp);
        Native.DeleteDC(mem);
        Native.EndPaint(hwnd, &ps);
    }

    private static void Bar(nint dc, Rect track)
    {
        Round(dc, track, Line, S(2));
        var seg = (track.R - track.L) / 3;
        var x = track.L + (int)((track.R - track.L + seg) * _phase) - seg;
        var bar = new Rect(Math.Max(track.L, x), track.T, Math.Min(track.R, x + seg), track.B);
        if (bar.R > bar.L) Round(dc, bar, Accent, S(2));
    }

    private static void Buttons(nint dc, Rect client, int pad, string primary, string? secondary)
    {
        var y = client.B - S(64);
        var primaryBox = new Rect(client.R - pad - S(136), y, client.R - pad, y + S(38));
        Hits.Add((Primary, primaryBox));
        Round(dc, primaryBox, _hover == Primary ? AccentHover : Accent, S(10));
        Text(dc, primary, primaryBox, 13, 600, 0xFFFFFF, 0x25);
        if (secondary is null) return;
        var secondaryBox = new Rect(primaryBox.L - S(108), y, primaryBox.L - S(10), y + S(38));
        Hits.Add((Secondary, secondaryBox));
        Round(dc, secondaryBox, _hover == Secondary ? Line : Bg, S(10), Line);
        Text(dc, secondary, secondaryBox, 13, 500, Ink, 0x25);
    }

    private static void Fill(nint dc, Rect r, uint color)
    {
        var brush = Native.CreateSolidBrush(color);
        var rect = new Native.RECT { Left = r.L, Top = r.T, Right = r.R, Bottom = r.B };
        Native.FillRect(dc, &rect, brush);
        Native.DeleteObject(brush);
    }

    private static void Round(nint dc, Rect r, uint fill, int radius, uint? border = null)
    {
        var brush = Native.CreateSolidBrush(fill);
        var pen = border is { } b ? Native.CreatePen(0, 1, b) : Native.GetStockObject(8);
        var oldBrush = Native.SelectObject(dc, brush);
        var oldPen = Native.SelectObject(dc, pen);
        Native.RoundRect(dc, r.L, r.T, r.R, r.B, radius, radius);
        Native.SelectObject(dc, oldBrush);
        Native.SelectObject(dc, oldPen);
        Native.DeleteObject(brush);
        if (border is not null) Native.DeleteObject(pen);
    }

    private static void Text(nint dc, string text, Rect r, float size, int weight, uint color, uint format)
    {
        fixed (char* face = "Segoe UI Variable Text")
        fixed (char* s = text)
        {
            var font = Native.CreateFontW(-S(size), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, face);
            var oldFont = Native.SelectObject(dc, font);
            Native.SetTextColor(dc, color);
            var rect = new Native.RECT { Left = r.L, Top = r.T, Right = r.R, Bottom = r.B };
            Native.DrawTextW(dc, s, text.Length, &rect, format);
            Native.SelectObject(dc, oldFont);
            Native.DeleteObject(font);
        }
    }

    internal readonly record struct Rect(int L, int T, int R, int B);
}

internal static unsafe partial class Native
{
    public const uint WM_APP = 0x8000;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; public uint priv; }
    [StructLayout(LayoutKind.Sequential)] public struct PAINTSTRUCT { public nint hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; public fixed byte rgbReserved[32]; }
    [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    public static Ui.Rect Client(nint hwnd)
    {
        RECT r;
        GetClientRect(hwnd, &r);
        return new Ui.Rect(r.Left, r.Top, r.Right, r.Bottom);
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint GetModuleHandleW(string? name);
    [LibraryImport("user32.dll")] public static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll")] public static partial nint CreateWindowExW(uint exStyle, char* cls, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] public static partial nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [LibraryImport("user32.dll")] public static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] public static partial int TranslateMessage(MSG* msg);
    [LibraryImport("user32.dll")] public static partial nint DispatchMessageW(MSG* msg);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessageW(nint hwnd, uint msg, nint w, nint l);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UpdateWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool InvalidateRect(nint hwnd, RECT* rect, int erase);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetClientRect(nint hwnd, RECT* rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ScreenToClient(nint hwnd, POINT* pt);
    [LibraryImport("user32.dll")] public static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EndPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] public static partial int FillRect(nint dc, RECT* rect, nint brush);
    [LibraryImport("user32.dll")] public static partial int DrawTextW(nint dc, char* text, int count, RECT* rect, uint format);
    [LibraryImport("user32.dll")] public static partial nint LoadCursorW(nint instance, nint name);
    [LibraryImport("user32.dll")] public static partial nint LoadIconW(nint instance, char* name);
    [LibraryImport("user32.dll")] public static partial nint SetCursor(nint cursor);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForSystem();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TrackMouseEvent(TRACKMOUSEEVENT* e);
    [LibraryImport("user32.dll")] public static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool KillTimer(nint hwnd, nuint id);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DrawIconEx(nint dc, int x, int y, nint icon, int w, int h, uint step, nint brush, uint flags);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial uint PrivateExtractIconsW(string file, int index, int cx, int cy, nint* icons, uint* ids, uint count, uint flags);
    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleBitmap(nint dc, int w, int h);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool BitBlt(nint dc, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport("gdi32.dll")] public static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] public static partial nint CreatePen(int style, int width, uint color);
    [LibraryImport("gdi32.dll")] public static partial nint GetStockObject(int index);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RoundRect(nint dc, int l, int t, int r, int b, int w, int h);
    [LibraryImport("gdi32.dll")] public static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] public static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")] public static partial nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, char* face);
    [LibraryImport("dwmapi.dll")] public static partial int DwmSetWindowAttribute(nint hwnd, uint attr, void* value, uint size);
    [LibraryImport("dwmapi.dll")] public static partial int DwmExtendFrameIntoClientArea(nint hwnd, int* margins);
    [LibraryImport("ole32.dll")] public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint item);
}

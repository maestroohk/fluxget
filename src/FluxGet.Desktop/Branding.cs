using FluxGet.Host;
using Microsoft.Win32;

namespace FluxGet.Desktop;

internal static class Branding
{
    public const string AppId = "velopack.FluxGet";
    private const string Revision = "2";

    public static string? IconFile { get; private set; }
    public static string? LogoFile { get; private set; }

    public static void Ensure()
    {
        try
        {
            var dir = Path.Combine(new FluxGetHostOptions().ResolveDataDirectory(), "branding");
            Directory.CreateDirectory(dir);
            IconFile = Extract("FluxGet.Desktop.fluxget.ico", Path.Combine(dir, $"fluxget-{Revision}.ico"));
            LogoFile = Extract("FluxGet.Desktop.fluxget.png", Path.Combine(dir, $"fluxget-{Revision}.png"));
            if (IconFile is not null) FixShortcuts(IconFile);
            if (LogoFile is not null) RegisterNotificationIdentity(LogoFile);
        }
        catch (Exception)
        {
        }
    }

    private static string? Extract(string resource, string path)
    {
        if (File.Exists(path)) return path;
        using var stream = typeof(Branding).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;
        using var file = File.Create(path);
        stream.CopyTo(file);
        return path;
    }

    private static void FixShortcuts(string icon)
    {
        var exe = Environment.ProcessPath;
        var places = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "FluxGet.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "FluxGet.lnk")
        };
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        var changed = false;
        foreach (var lnk in places.Where(File.Exists))
        {
            dynamic shortcut = shell.CreateShortcut(lnk);
            string target = shortcut.TargetPath;
            if (exe is null || !Path.GetFileName(target).Equals("FluxGet.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals((string)shortcut.IconLocation, icon + ",0", StringComparison.OrdinalIgnoreCase))
            {
                shortcut.IconLocation = icon + ",0";
                shortcut.Save();
                changed = true;
            }
        }
        if (changed) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    private static void RegisterNotificationIdentity(string logo)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
        key.SetValue("DisplayName", "FluxGet");
        key.SetValue("IconUri", logo);
        key.SetValue("IconBackgroundColor", "00000000");
    }
}

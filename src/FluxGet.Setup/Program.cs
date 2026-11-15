using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;

namespace FluxGet.Setup;

internal static partial class Program
{
    private const string Magic = "FGSETUP1";
    private static Task<string?> _payload = Task.FromResult<string?>(null);
    private static string _product = "FluxGet";
    private static string? _existing;
    private static string _target = "";
    private static nint _hwnd;

    [STAThread]
    private static int Main(string[] args)
    {
        _payload = Task.Run(() => { try { return ExtractPayload(args); } catch (Exception) { return null; } });
        var at = Array.IndexOf(args, "--product");
        if (at >= 0 && at + 1 < args.Length) _product = args[at + 1];
        _existing = InstalledLocation(_product);
        _target = _existing ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _product);
        var preset = Array.IndexOf(args, "--target");
        if (preset >= 0 && preset + 1 < args.Length && Array.IndexOf(args, "--payload") >= 0) _target = args[preset + 1];
        try
        {
            return Ui.Run();
        }
        finally
        {
            if (_payload.Wait(TimeSpan.FromSeconds(30)) && _payload.Result is { } extracted) try { Directory.Delete(Path.GetDirectoryName(extracted)!, true); } catch (Exception) { }
        }
    }

    internal static bool Updating => _existing is not null;
    internal static string Target => _target;

    internal static string FreeSpaceText()
    {
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_target))!).AvailableFreeSpace;
            return $"Needs about 700 MB · {free / 1073741824.0:0.#} GB free on this drive";
        }
        catch (Exception)
        {
            return "Needs about 700 MB";
        }
    }

    internal static string? ChangeFolder()
    {
        var start = Directory.Exists(_target) ? _target : Path.GetDirectoryName(_target)!;
        if (PickFolder(start) is not { } picked) return null;
        var candidate = Path.GetFileName(picked.TrimEnd('\\')).Equals(_product, StringComparison.OrdinalIgnoreCase) ? picked : Path.Combine(picked, _product);
        if (!Writable(candidate)) return $"FluxGet can't be installed in {candidate} without administrator rights. Choose a folder in your user profile or on another drive, for example D:\\Apps.";
        _target = candidate;
        return null;
    }

    internal static void Install()
    {
        Task.Run(() =>
        {
            try
            {
                if (_payload.Result is not { } installer) throw new InvalidDataException("This installer is damaged. Download it again from the FluxGet releases page.");
                using var setup = Process.Start(new ProcessStartInfo(installer) { ArgumentList = { "--silent", "--installto", _target }, UseShellExecute = false, CreateNoWindow = true })!;
                setup.WaitForExit();
                if (setup.ExitCode != 0) Ui.Error = $"Setup could not finish (code {setup.ExitCode}). Close any running FluxGet window and try again.";
            }
            catch (InvalidDataException e)
            {
                Ui.Error = e.Message;
            }
            catch (Exception e)
            {
                Ui.Error = "Setup could not start: " + e.Message;
            }
            Native.PostMessageW(_hwnd, Native.WM_APP, 0, 0);
        });
    }

    internal static void Launch()
    {
        var exe = Path.Combine(_target, "current", "FluxGet.exe");
        if (!File.Exists(exe)) exe = Path.Combine(_target, "FluxGet.exe");
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! }); } catch (Exception) { }
    }

    internal static void SetWindow(nint hwnd) => _hwnd = hwnd;

    private static string? ExtractPayload(string[] args)
    {
        var dev = Array.IndexOf(args, "--payload");
        if (dev >= 0 && dev + 1 < args.Length) return Copy(File.OpenRead(args[dev + 1]), -1);
        using var self = File.OpenRead(Environment.ProcessPath!);
        var end = SignedEnd(self);
        if (end < 16) return null;
        var trailer = new byte[16];
        self.Position = end - 16;
        self.ReadExactly(trailer);
        if (Encoding.ASCII.GetString(trailer, 0, 8) != Magic) return null;
        var length = BitConverter.ToInt64(trailer, 8);
        if (length <= 0 || length > end - 16) return null;
        self.Position = end - 16 - length;
        return Copy(self, length);
    }

    private static string Copy(Stream source, long length)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FluxGet-Setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "FluxGet-win-Setup.exe");
        using (source)
        using (var output = File.Create(path))
        {
            var buffer = new byte[1 << 20];
            var left = length < 0 ? long.MaxValue : length;
            int read;
            while (left > 0 && (read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0)
            {
                output.Write(buffer, 0, read);
                left -= read;
            }
        }
        return path;
    }

    private static long SignedEnd(FileStream pe)
    {
        var header = new byte[4096];
        pe.Position = 0;
        var n = pe.Read(header, 0, header.Length);
        if (n < 0x40) return pe.Length;
        var nt = BitConverter.ToInt32(header, 0x3C);
        if (nt <= 0 || nt + 0x18 + 0x98 > n) return pe.Length;
        var optional = nt + 0x18;
        var magic = BitConverter.ToUInt16(header, optional);
        var directories = optional + (magic == 0x20B ? 0x70 : 0x60);
        var securityOffset = BitConverter.ToUInt32(header, directories + 4 * 8);
        return securityOffset > 0 && securityOffset < pe.Length ? securityOffset : pe.Length;
    }

    private static string? InstalledLocation(string product)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{product}");
            return key?.GetValue("InstallLocation") is string location && Directory.Exists(location) ? location : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Writable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".fluxget-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? PickFolder(string start)
    {
        var clsid = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        var iid = typeof(IFileOpenDialog).GUID;
        if (Native.CoCreateInstance(clsid, 0, 1, iid, out var raw) != 0) return null;
        var wrappers = new StrategyBasedComWrappers();
        var dialog = (IFileOpenDialog)wrappers.GetOrCreateObjectForComInstance(raw, CreateObjectFlags.UniqueInstance);
        Marshal.Release(raw);
        dialog.GetOptions(out var options);
        dialog.SetOptions(options | 0x20 | 0x40 | 0x800);
        dialog.SetTitle("Choose where to install FluxGet");
        dialog.SetOkButtonLabel("Install here");
        if (Directory.Exists(start) && Native.SHCreateItemFromParsingName(start, 0, typeof(IShellItem).GUID, out var folderRaw) == 0)
        {
            var folder = (IShellItem)wrappers.GetOrCreateObjectForComInstance(folderRaw, CreateObjectFlags.UniqueInstance);
            Marshal.Release(folderRaw);
            dialog.SetFolder(folder);
        }
        if (dialog.Show(_hwnd) != 0) return null;
        dialog.GetResult(out var item);
        item.GetDisplayName(0x80058000, out var name);
        var path = Marshal.PtrToStringUni(name);
        Marshal.FreeCoTaskMem(name);
        return path;
    }
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
internal partial interface IShellItem
{
    void BindToHandler(nint bindContext, in Guid handler, in Guid iid, out nint result);
    void GetParent(out IShellItem parent);
    void GetDisplayName(uint form, out nint name);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
internal partial interface IFileOpenDialog
{
    [PreserveSig] int Show(nint owner);
    void SetFileTypes(uint count, nint specs);
    void SetFileTypeIndex(uint index);
    void GetFileTypeIndex(out uint index);
    void Advise(nint events, out uint cookie);
    void Unadvise(uint cookie);
    void SetOptions(uint options);
    void GetOptions(out uint options);
    void SetDefaultFolder(IShellItem folder);
    void SetFolder(IShellItem folder);
    void GetFolder(out IShellItem folder);
    void GetCurrentSelection(out IShellItem item);
    void SetFileName(string name);
    void GetFileName(out nint name);
    void SetTitle(string title);
    void SetOkButtonLabel(string text);
    void SetFileNameLabel(string label);
    void GetResult(out IShellItem item);
}

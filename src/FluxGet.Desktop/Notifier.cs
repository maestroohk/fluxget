using System.Diagnostics;
using FluxGet.Engine;
using FluxGet.Host;
using Microsoft.Toolkit.Uwp.Notifications;

namespace FluxGet.Desktop;

internal sealed class Notifier
{
    private readonly string? _logo;
    private readonly Action _showWindow;
    private readonly Func<long, Job?> _find;
    private readonly Control _ui;
    private readonly Action _applyUpdate;

    public Notifier(Control ui, Action showWindow, Func<long, Job?> find, Action applyUpdate)
    {
        _ui = ui;
        _applyUpdate = applyUpdate;
        _showWindow = showWindow;
        _find = find;
        _logo = WriteLogo();
        ToastNotificationManagerCompat.OnActivated += e => _ui.BeginInvoke(() => OnActivated(e.Argument));
    }

    private static string? WriteLogo()
    {
        try
        {
            var path = Path.Combine(new FluxGetHostOptions().ResolveDataDirectory(), "fluxget-logo.png");
            if (!File.Exists(path))
            {
                using var stream = typeof(Notifier).Assembly.GetManifestResourceStream("FluxGet.Desktop.fluxget.png");
                if (stream is null) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var file = File.Create(path);
                stream.CopyTo(file);
            }
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Completed(Job job)
    {
        var builder = Base()
            .AddArgument("action", "show").AddArgument("id", job.Id)
            .AddText("Download complete")
            .AddText(job.FileName ?? "Your download finished");
        if (job.FilePath is not null)
        {
            builder.AddButton(new ToastButton().SetContent("Open").AddArgument("action", "open").AddArgument("id", job.Id));
            builder.AddButton(new ToastButton().SetContent("Show in folder").AddArgument("action", "reveal").AddArgument("id", job.Id));
        }
        Show(builder);
    }

    public void NeedsAttention(Job job) =>
        Show(Base()
            .AddArgument("action", "show").AddArgument("id", job.Id)
            .AddText("Download needs attention")
            .AddText(job.FileName ?? "Download")
            .AddText(job.Error ?? ""));

    public void UpdateReady(string version) =>
        Show(Base()
            .AddArgument("action", "show")
            .AddText($"FluxGet {version} is ready")
            .AddText("Restart FluxGet to finish updating. Active downloads resume automatically.")
            .AddButton(new ToastButton().SetContent("Restart now").AddArgument("action", "update"))
            .AddButton(new ToastButton().SetContent("Later").SetDismissActivation()));

    public void Info(string title, string text) => Show(Base().AddArgument("action", "show").AddText(title).AddText(text));

    private ToastContentBuilder Base()
    {
        var b = new ToastContentBuilder();
        if (_logo is not null) b.AddAppLogoOverride(new Uri(_logo), ToastGenericAppLogoCrop.Default);
        return b;
    }

    private static void Show(ToastContentBuilder builder)
    {
        try { builder.Show(); } catch (Exception) { }
    }

    private void OnActivated(string argument)
    {
        var args = ToastArguments.Parse(argument);
        var action = args.TryGetValue("action", out var a) ? a : "show";
        var job = args.TryGetValue("id", out var id) && long.TryParse(id, out var jobId) ? _find(jobId) : null;
        var path = job?.FilePath;
        switch (action)
        {
            case "open" when path is not null && File.Exists(path):
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                break;
            case "update":
                _applyUpdate();
                break;
            case "reveal" when path is not null && File.Exists(path):
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path } });
                break;
            default:
                _showWindow();
                break;
        }
    }

    public static void Uninstall()
    {
        try { ToastNotificationManagerCompat.Uninstall(); } catch (Exception) { }
    }
}

using Velopack;
using Velopack.Sources;

namespace FluxGet.Desktop;

internal sealed class Updates
{
    public const string Repository = "https://github.com/maestroohk/fluxget";
    private readonly UpdateManager? _manager;
    private UpdateInfo? _ready;

    public Updates()
    {
        try
        {
            var custom = Environment.GetEnvironmentVariable("FLUXGET_UPDATE_SOURCE");
            IUpdateSource source = string.IsNullOrWhiteSpace(custom)
                ? new GithubSource(Repository, null, false)
                : Directory.Exists(custom) ? new SimpleFileSource(new DirectoryInfo(custom)) : new SimpleWebSource(custom);
            var manager = new UpdateManager(source);
            _manager = manager.IsInstalled ? manager : null;
        }
        catch (Exception)
        {
            _manager = null;
        }
    }

    public bool Supported => _manager is not null;
    public string? CurrentVersion => _manager?.CurrentVersion?.ToString();
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    public async Task<(string State, string? Version, string? Error)> CheckAsync()
    {
        if (_manager is null) return ("unsupported", null, null);
        if (_ready is not null) return ("ready", ReadyVersion, null);
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null) return ("current", CurrentVersion, null);
            await _manager.DownloadUpdatesAsync(info);
            _ready = info;
            return ("ready", ReadyVersion, null);
        }
        catch (Exception e)
        {
            return ("error", null, e.Message);
        }
    }

    public void ApplyAndRestart()
    {
        if (_manager is not null && _ready is not null) _manager.ApplyUpdatesAndRestart(_ready);
    }
}

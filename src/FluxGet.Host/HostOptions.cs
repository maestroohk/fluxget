namespace FluxGet.Host;

public enum HostMode { Local, Desktop, Hosted }

public sealed class FluxGetHostOptions
{
    public int Port { get; set; } = 17890;
    public HostMode Mode { get; set; } = HostMode.Local;
    public string? DataDirectory { get; set; }
    public string? WebRoot { get; set; }
    public string? AccessToken { get; set; }
    public string[] AllowedHosts { get; set; } = [];
    public string[] DevOrigins { get; set; } = ["http://localhost:5173", "http://127.0.0.1:5173"];
    public bool DevDashboard { get; set; }
    public string? ToolsDirectory { get; set; }
    public string? DownloadDirectory { get; set; }

    public string ResolveDataDirectory() => Path.GetFullPath(DataDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxGetData"));

    public bool IsLoopbackOnly => Mode != HostMode.Hosted;
}

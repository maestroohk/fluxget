namespace FluxGet.Engine.Media;

public sealed class ToolLocator(string? toolsDirectory)
{
    private readonly Dictionary<string, string?> _cache = new();

    public string? Find(string name)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(name, out var cached)) return cached;
            var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
            var dirs = new List<string?> { toolsDirectory, Path.Combine(AppContext.BaseDirectory, "tools") };
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            if (OperatingSystem.IsWindows())
            {
                foreach (var scope in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
                    dirs.AddRange((Environment.GetEnvironmentVariable("PATH", scope) ?? "").Split(Path.PathSeparator).Select(Environment.ExpandEnvironmentVariables));
            }
            if (OperatingSystem.IsWindows())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
            }
            string? found = null;
            foreach (var d in dirs.Where(d => !string.IsNullOrWhiteSpace(d)))
            {
                try
                {
                    var candidate = Path.Combine(d!.Trim('"'), file);
                    if (File.Exists(candidate)) { found = candidate; break; }
                }
                catch (ArgumentException) { }
            }
            _cache[name] = found;
            return found;
        }
    }

    public string? JsRuntime(out string? kind)
    {
        if (Find("deno") is { } deno) { kind = "deno"; return deno; }
        if (Find("node") is { } node) { kind = "node"; return node; }
        kind = null;
        return null;
    }

    public void Invalidate()
    {
        lock (_cache) _cache.Clear();
    }
}

public sealed class ToolStatus(ToolLocator tools)
{
    public object Snapshot()
    {
        var js = tools.JsRuntime(out var kind);
        return new
        {
            ytDlp = tools.Find("yt-dlp"),
            ffmpeg = tools.Find("ffmpeg"),
            ffprobe = tools.Find("ffprobe"),
            jsRuntime = js is null ? null : $"{kind}: {js}"
        };
    }
}

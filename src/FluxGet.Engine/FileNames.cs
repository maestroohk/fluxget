using System.Net.Http.Headers;
using System.Text;

namespace FluxGet.Engine;

public static class FileNames
{
    public const string PartSuffix = ".fgpart";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly HashSet<char> Invalid =
        Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '|', '?', '*', '/', '\\' }).ToHashSet();

    public static string FromResponse(ContentDispositionHeaderValue? disposition, Uri url, string? mediaType)
    {
        var name = disposition?.FileNameStar ?? disposition?.FileName;
        name = name?.Trim('"', ' ');
        if (string.IsNullOrWhiteSpace(name))
        {
            var last = url.Segments.LastOrDefault()?.Trim('/');
            name = string.IsNullOrEmpty(last) ? null : Uri.UnescapeDataString(last);
        }
        if (string.IsNullOrWhiteSpace(name)) name = url.Host;
        name = Sanitize(name);
        if (!Path.HasExtension(name) && ExtensionFor(mediaType) is { } ext) name += ext;
        return name;
    }

    public static string Sanitize(string name)
    {
        var lastSep = name.LastIndexOfAny(['/', '\\']);
        if (lastSep >= 0) name = name[(lastSep + 1)..];
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(Invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0 || result.All(c => c == '.')) result = "download";
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(result))) result = "_" + result;
        if (result.Length > 180)
        {
            var ext = Path.GetExtension(result);
            if (ext.Length > 20) ext = "";
            result = result[..(180 - ext.Length)] + ext;
        }
        return result;
    }

    public static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
    }

    public static string EnsureInside(string directory, string fileName)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var full = Path.GetFullPath(Path.Combine(root, Sanitize(fileName)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new JobFailedException("Unsafe file name.");
        return full;
    }

    private static string? ExtensionFor(string? mediaType) => mediaType switch
    {
        "application/pdf" => ".pdf",
        "application/zip" => ".zip",
        "application/x-iso9660-image" => ".iso",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "audio/mpeg" => ".mp3",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "text/html" => ".html",
        "application/json" => ".json",
        _ => null
    };
}

using System.Net;
using System.Net.Sockets;

namespace FluxGet.Engine;

public static class UrlGuard
{
    public static Uri Parse(string? input)
    {
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Enter a valid http:// or https:// address.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Addresses with embedded credentials are not accepted.");
        return uri;
    }

    public static async Task EnsurePublicAsync(Uri uri, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) ? [ip] : await Dns.GetHostAddressesAsync(uri.Host, ct);
        if (addresses.Length == 0 || addresses.Any(IsPrivate))
            throw new ArgumentException("Addresses on private or local networks are not allowed on this server.");
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] switch
            {
                0 or 10 or 127 => true,
                100 => b[1] >= 64 && b[1] <= 127,
                169 => b[1] == 254,
                172 => b[1] >= 16 && b[1] <= 31,
                192 => b[1] == 168 || (b[1] == 0 && (b[2] == 0 || b[2] == 2)),
                198 => b[1] == 18 || b[1] == 19,
                _ => b[0] >= 224
            };
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC;
    }
}

public sealed class BlockedAddressException(string message) : IOException(message);

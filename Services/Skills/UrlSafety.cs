using System.Net;
using System.Net.Sockets;

namespace CLIo.Services.Skills;

/// <summary>
/// Stops a skill from being talked into reading the user's own network. The model writes the URL, and
/// web pages can contain text that tries to steer it, so a fetch of http://localhost:... or
/// http://192.168.x.x/... must be impossible. The check runs on the address actually connected to, so a
/// hostname that resolves to a private address, a redirect, or DNS rebinding cannot get around it.
/// </summary>
public static class UrlSafety
{
    public static bool IsAllowedScheme(Uri uri) => uri.Scheme is "http" or "https";

    /// <summary>A handler whose every connection (including redirect hops) is checked and refused if private.</summary>
    public static SocketsHttpHandler CreateSafeHandler() => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, cancellationToken);

            var allowed = addresses.Where(address => !IsPrivateOrReserved(address)).ToArray();
            if (allowed.Length == 0)
            {
                throw new HttpRequestException($"Blocked: '{host}' is a local or private address.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    public static bool IsPrivateOrReserved(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 0                                          // 0.0.0.0/8
                || bytes[0] == 10                                         // 10/8
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)      // 100.64/10 (carrier NAT, Tailscale)
                || bytes[0] == 127                                        // loopback
                || (bytes[0] == 169 && bytes[1] == 254)                   // link-local, cloud metadata
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)       // 172.16/12
                || (bytes[0] == 192 && bytes[1] == 168)                   // 192.168/16
                || bytes[0] >= 224;                                       // multicast and reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (bytes[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        }

        return true;
    }
}

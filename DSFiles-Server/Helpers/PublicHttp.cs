using System.Net;
using System.Net.Sockets;

namespace DSFiles_Server.Helpers
{
    internal static class PublicHttp
    {
        private static readonly HttpClient Client = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                // Connect to the addresses we checked, rather than resolving the hostname a second time.
                if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
                    throw new HttpRequestException("Private or reserved network destinations are not allowed.");
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); if (ct.IsCancellationRequested) throw; }
                }
                throw new HttpRequestException("Could not connect to the public destination.");
            }
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        internal static bool IsPublic(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            var b = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return (b[0] & 0xe0) == 0x20
                    && !(b[0] == 0x20 && b[1] == 0x02)
                    && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8)));
            return b[0] is not (0 or 10 or 127) && b[0] < 224
                && !(b[0] == 100 && b[1] is >= 64 and <= 127)
                && !(b[0] == 169 && b[1] == 254)
                && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || (b[1] == 88 && b[2] == 99)))
                && !(b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100)))
                && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        }

        public static async Task<byte[]> GetBytes(string url, int maximumBytes, CancellationToken ct)
        {
            if (url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
                || uri.Port != (uri.Scheme == "https" ? 443 : 80))
                throw new ArgumentException("Only public HTTP/HTTPS URLs on standard ports are allowed.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new HttpRequestException("Redirects are not allowed for image downloads.");
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(maximumBytes, deadline.Token);
            return await response.Content.ReadAsByteArrayAsync(deadline.Token);
        }
    }
}

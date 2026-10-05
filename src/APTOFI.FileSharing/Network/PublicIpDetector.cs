// Date: 2026-10-05
// Time: 09:20:00 +07:00
// File version: 1.1.36
// Description: Detects public IPv4 through independent providers with short bounded attempts so one bad route cannot block DNS maintenance.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace APTOFI.FileSharing.Network
{
    internal static class PublicIpDetector
    {
        private static readonly string[] Providers =
        {
            "https://checkip.amazonaws.com/",
            "https://api.ipify.org/",
            "https://ipv4.icanhazip.com/"
        };

        public static async Task<IPAddress> DetectIpv4Async(TimeSpan perProviderTimeout)
        {
            if (perProviderTimeout <= TimeSpan.Zero)
                perProviderTimeout = TimeSpan.FromSeconds(6);

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var errors = new List<string>();

            foreach (var url in Providers)
            {
                try
                {
                    using (var handler = new HttpClientHandler())
                    using (var client = new HttpClient(handler) { Timeout = perProviderTimeout })
                    {
                        client.DefaultRequestHeaders.ConnectionClose = true;
                        var text = (await client.GetStringAsync(url).ConfigureAwait(false)).Trim();
                        if (IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
                            return address;
                        errors.Add(new Uri(url).Host + ": invalid IPv4 response");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new Uri(url).Host + ": " + ShortError(ex));
                }
            }

            throw new InvalidOperationException(
                "Public IPv4 could not be detected through the available providers. " +
                "Check Internet access, firewall, proxy or routing. " + string.Join(" | ", errors));
        }

        public static async Task<string> DetectIpv4TextAsync(TimeSpan perProviderTimeout)
        {
            var address = await DetectIpv4Async(perProviderTimeout).ConfigureAwait(false);
            return address.ToString();
        }

        private static string ShortError(Exception ex)
        {
            if (ex == null)
                return "unknown error";
            while (ex.InnerException != null && (ex is AggregateException || ex is HttpRequestException))
                ex = ex.InnerException;
            var message = (ex.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return ex.GetType().Name + (message.Length == 0 ? string.Empty : ": " + message);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Storage;
using Microsoft.Win32;

namespace APTOFI.FileSharing.Network
{
    internal sealed class DiagnosticsService
    {
        private readonly Database _db;
        private readonly WindowsNetworkService _windows;
        private readonly Func<bool> _webRunning;
        private readonly Func<object> _vpsStatus;
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        public DiagnosticsService(Database db, WindowsNetworkService windows, Func<bool> webRunning, Func<object> vpsStatus)
        {
            _db = db;
            _windows = windows;
            _webRunning = webRunning;
            _vpsStatus = vpsStatus;
        }

        public async Task<DiagnosticReport> RunAsync(bool includeSpeedTest)
        {
            var report = new DiagnosticReport { StartedUtc = DateTime.UtcNow };
            var settings = _db.GetSettings();
            Add(report, "server", _webRunning(), _webRunning() ? "Built-in server is running." : "Built-in server is not running.", _webRunning() ? null : "Start or restart the APTOFI Windows service.");
            CheckServiceAutostart(report);
            CheckSettingsFile(report, settings);
            CheckDatabase(report);
            await CheckStorage(report, settings, includeSpeedTest).ConfigureAwait(false);
            CheckBindAddress(report, settings);
            await CheckLocalPort(report, settings?.HttpPort ?? 0, "http_port").ConfigureAwait(false);
            if (settings != null && settings.EnableHttps && !string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
                await CheckLocalPort(report, settings.HttpsPort, "https_port").ConfigureAwait(false);
            var publicIp = await CheckPublicIp(report).ConfigureAwait(false);
            CheckDomain(report, settings);
            if (DnsUpdateService.IsConfigured(settings))
                await CheckDnsUpdate(report, settings, publicIp).ConfigureAwait(false);
            CheckCertificate(report, settings);
            await CheckPublicUrl(report, settings).ConfigureAwait(false);
            await CheckPublicHttpRedirect(report, settings).ConfigureAwait(false);
            CheckVps(report, settings);
            report.FinishedUtc = DateTime.UtcNow;
            report.Summary = BuildSummary(report);
            return report;
        }

        public async Task<TransferTestResult> RunLocalTransferTestAsync()
        {
            var settings = _db.GetSettings();
            var location = StorageService.GetLocations(settings).FirstOrDefault(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Path));
            if (location == null)
                throw new InvalidOperationException("Storage is not configured.");
            Directory.CreateDirectory(location.Path);
            var path = Path.Combine(location.Path, ".afsharing-transfer-test-" + Guid.NewGuid().ToString("N"));
            const int total = 64 * 1024 * 1024;
            var buffer = new byte[256 * 1024];
            new Random().NextBytes(buffer);
            var sw = Stopwatch.StartNew();
            try
            {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var written = 0;
                    while (written < total)
                    {
                        var count = Math.Min(buffer.Length, total - written);
                        await file.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                        written += count;
                    }
                    await file.FlushAsync().ConfigureAwait(false);
                }
                sw.Stop();
                var write = total / 1024d / 1024d / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                sw.Restart();
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (await file.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0)
                    {
                    }
                }
                sw.Stop();
                var read = total / 1024d / 1024d / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                return new TransferTestResult { WriteMiBs = write, ReadMiBs = read };
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        public string ExportText(DiagnosticReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AppVersion.ProductName + " " + AppVersion.Version);
            sb.AppendLine("Generated UTC: " + DateTime.UtcNow.ToString("O"));
            sb.AppendLine("Summary: " + report.Summary);
            foreach (var item in report.Items)
            {
                sb.AppendLine((item.Ok ? "OK " : "FAIL ") + item.Key + ": " + item.Message);
                if (!string.IsNullOrWhiteSpace(item.Action))
                    sb.AppendLine("Action: " + item.Action);
                if (!string.IsNullOrWhiteSpace(item.Details))
                    sb.AppendLine("Details: " + item.Details);
            }
            return sb.ToString();
        }

        private static void CheckServiceAutostart(DiagnosticReport report)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + AppVersion.ServiceName, false))
                {
                    if (key == null)
                    {
                        Add(report, "service_autostart", false, "The APTOFI Windows service is not installed.", "Use Save and start in afsharing.exe.");
                        return;
                    }
                    var startValue = key.GetValue("Start") == null ? -1 : Convert.ToInt32(key.GetValue("Start"));
                    var delayedValue = key.GetValue("DelayedAutostart") == null ? 0 : Convert.ToInt32(key.GetValue("DelayedAutostart"));
                    var ok = startValue == 2 && delayedValue == 0;
                    Add(report, "service_autostart", ok, ok ? "The APTOFI service starts automatically at Windows boot before user sign-in." : "The APTOFI service startup mode is incorrect.", ok ? null : "Use Save and start to repair automatic startup.", "Start=" + startValue + "; DelayedAutostart=" + delayedValue);
                }
            }
            catch (Exception ex)
            {
                Add(report, "service_autostart", false, "Windows service startup settings could not be read.", "Run afsharing.exe as administrator.", ex.Message);
            }
        }

        private static void CheckSettingsFile(DiagnosticReport report, AppSettings settings)
        {
            try
            {
                string verifyError = null;
                var ok = settings != null && File.Exists(AppPaths.SettingsFilePath) && SettingsFileStore.Verify(settings, out verifyError);
                Add(report, "settings_file", ok, ok ? "The durable encrypted settings file is present and verified." : "The durable settings file is missing or invalid.", ok ? null : "Open afsharing.exe and press Save.", AppPaths.SettingsFilePath + (string.IsNullOrWhiteSpace(verifyError) ? string.Empty : "; " + verifyError));
            }
            catch (Exception ex)
            {
                Add(report, "settings_file", false, "The durable settings file could not be verified.", "Check write permission for the folder containing afsharing.exe and press Save again.", ex.Message);
            }
        }

        private void CheckDatabase(DiagnosticReport report)
        {
            try
            {
                var settings = _db.GetSettings();
                _db.Settings.FindById(settings?.Id ?? "main");
                Add(report, "database", settings != null, settings != null ? "Encrypted database opens and responds to reads." : "Encrypted database contains no server settings.", settings != null ? null : "Open afsharing.exe and save the configuration.");
            }
            catch (Exception ex)
            {
                Add(report, "database", false, "Encrypted database could not be read.", "Check the files and write permissions next to afsharing.exe.", ex.Message);
            }
        }

        private async Task CheckStorage(DiagnosticReport report, AppSettings settings, bool speed)
        {
            var locations = StorageService.GetLocations(settings).Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Path)).ToList();
            if (locations.Count == 0)
            {
                Add(report, "storage", false, "No storage folder is configured.", "Add at least one storage folder and press Save.");
                return;
            }
            var index = 0;
            foreach (var location in locations)
            {
                index++;
                var key = "storage_" + index;
                var test = string.Empty;
                try
                {
                    Directory.CreateDirectory(location.Path);
                    test = Path.Combine(location.Path, ".afsharing-storage-test-" + Guid.NewGuid().ToString("N"));
                    var bytes = Encoding.UTF8.GetBytes("APTOFI");
                    using (var stream = new FileStream(test, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                    }
                    var read = File.ReadAllBytes(test);
                    File.Delete(test);
                    test = string.Empty;
                    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(location.Path)));
                    Add(report, key, read.SequenceEqual(bytes), "Storage read, write and delete test completed for " + location.Path + ".", read.SequenceEqual(bytes) ? null : "Check the disk and filesystem permissions.", "Free bytes: " + drive.AvailableFreeSpace + "; APTOFI used bytes: " + location.UsedBytes);
                }
                catch (Exception ex)
                {
                    Add(report, key, false, "Storage test failed for " + location.Path + ".", "Check that the disk is online and the APTOFI service account can read, write and delete files.", ex.Message);
                }
                finally
                {
                    try { if (!string.IsNullOrWhiteSpace(test) && File.Exists(test)) File.Delete(test); } catch { }
                }
            }
            if (speed)
            {
                try
                {
                    var result = await RunLocalTransferTestAsync().ConfigureAwait(false);
                    Add(report, "storage_speed", true, "Local storage speed test completed.", null, "Write " + result.WriteMiBs.ToString("F1") + " MiB/s; Read " + result.ReadMiBs.ToString("F1") + " MiB/s");
                }
                catch (Exception ex)
                {
                    Add(report, "storage_speed", false, "Local storage speed test failed.", "Check the selected storage disk.", ex.Message);
                }
            }
        }

        private static void CheckBindAddress(DiagnosticReport report, AppSettings settings)
        {
            if (settings == null)
                return;
            if (string.IsNullOrWhiteSpace(settings.BindAddress) || settings.BindAddress == "0.0.0.0")
            {
                Add(report, "bind_ip", true, "Server listens on all local interfaces.", null);
                return;
            }
            try
            {
                var addresses = Dns.GetHostAddresses(Dns.GetHostName()).Select(x => x.ToString()).ToArray();
                var ok = addresses.Contains(settings.BindAddress, StringComparer.OrdinalIgnoreCase);
                Add(report, "bind_ip", ok, ok ? "Configured bind IP exists on this computer." : "Configured bind IP is not assigned to this computer.", ok ? null : "Select 0.0.0.0 or a currently assigned local IP.", "Configured: " + settings.BindAddress + "; Found: " + string.Join(", ", addresses));
            }
            catch (Exception ex)
            {
                Add(report, "bind_ip", false, "Local interface addresses could not be inspected.", null, ex.Message);
            }
        }

        private static async Task CheckLocalPort(DiagnosticReport report, int port, string key)
        {
            if (port < 1)
                return;
            try
            {
                using (var client = new TcpClient())
                {
                    var task = client.ConnectAsync(IPAddress.Loopback, port);
                    var completed = await Task.WhenAny(task, Task.Delay(2000)).ConfigureAwait(false);
                    var ok = completed == task && client.Connected;
                    Add(report, key, ok, ok ? "TCP " + port + " accepts local connections." : "TCP " + port + " does not accept local connections.", ok ? null : "Check that the APTOFI service is running and that this port is configured correctly.");
                }
            }
            catch (Exception ex)
            {
                Add(report, key, false, "TCP " + port + " does not accept local connections.", "Check the APTOFI service and port ownership.", ex.Message);
            }
        }

        private async Task<string> CheckPublicIp(DiagnosticReport report)
        {
            try
            {
                var ip = (await Client.GetStringAsync("https://api.ipify.org").ConfigureAwait(false)).Trim();
                var ok = IPAddress.TryParse(ip, out _);
                Add(report, "public_ip", ok, ok ? "Detected public IP: " + ip : "The public IP response is invalid.", ok ? null : "Check Internet connectivity.");
                return ok ? ip : null;
            }
            catch (Exception ex)
            {
                Add(report, "public_ip", false, "Public IP could not be detected.", "Check Internet connectivity and TLS support.", ex.Message);
                return null;
            }
        }

        private static void CheckDomain(DiagnosticReport report, AppSettings settings)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.Domain))
                return;
            try
            {
                var addresses = Dns.GetHostAddresses(settings.Domain).Select(x => x.ToString()).ToArray();
                Add(report, "dns", addresses.Length > 0, settings.Domain + " resolves to " + string.Join(", ", addresses), addresses.Length > 0 ? null : "Create the DNS record for the configured domain.");
            }
            catch (Exception ex)
            {
                Add(report, "dns", false, "The configured domain does not resolve.", "Check the A/AAAA record for " + settings.Domain + ".", ex.Message);
            }
        }

        private async Task CheckDnsUpdate(DiagnosticReport report, AppSettings settings, string publicIp)
        {
            try
            {
                var domain = DnsUpdateService.NormalizeZone(settings.Domain);
                var addresses = await Dns.GetHostAddressesAsync(domain).ConfigureAwait(false);
                var ipv4 = addresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork).Select(x => x.ToString()).ToArray();
                var ok = ipv4.Length > 0 && (string.IsNullOrWhiteSpace(publicIp) || ipv4.Contains(publicIp, StringComparer.OrdinalIgnoreCase) || !settings.DnsAutoUpdateAddress);
                Add(report, "dns_update", ok, ok ? "RFC2136/TSIG DNS configuration is available for DNS-01." : "The domain does not resolve to the expected public IPv4 address.", ok ? null : "Check the domain, DNS update server, zone, TSIG key name, algorithm and secret, then run the DNS test.", domain + " -> " + string.Join(", ", ipv4) + (string.IsNullOrWhiteSpace(settings.LastDnsError) ? string.Empty : "; Last error: " + settings.LastDnsError));
            }
            catch (Exception ex)
            {
                Add(report, "dns_update", false, "RFC2136/TSIG DNS configuration could not be verified.", "Check the generic Domain settings and run the DNS test.", ex.Message);
            }
        }

        private static void CheckCertificate(DiagnosticReport report, AppSettings settings)
        {
            if (settings == null || !settings.EnableHttps)
                return;
            if (string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
            {
                var action = DnsUpdateService.IsConfigured(settings)
                    ? "DNS-01 is configured. Verify DNS credentials and issue the certificate. Ports 80 and 443 are not required for DNS-01; users only need the configured HTTPS port " + settings.HttpsPort + "."
                    : "No certificate is installed. Configure RFC2136/TSIG DNS-01, VPS/reverse proxy, or another certificate method that matches your domain provider.";
                Add(report, "tls", false, "HTTPS is not listening because APTOFI has no certificate yet.", action, settings.LastCertificateError);
                return;
            }
            try
            {
                using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadOnly);
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint, settings.CertificateThumbprint, false);
                    if (found.Count == 0)
                    {
                        Add(report, "tls", false, "Configured certificate is missing from the LocalMachine certificate store.", "Issue the certificate again.");
                        return;
                    }
                    var cert = found[0];
                    var ok = cert.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddHours(12);
                    Add(report, "tls", ok, "Certificate expires UTC " + cert.NotAfter.ToUniversalTime().ToString("O"), ok ? null : "Certificate renewal is required.", "Thumbprint " + cert.Thumbprint);
                }
            }
            catch (Exception ex)
            {
                Add(report, "tls", false, "Certificate inspection failed.", "Check LocalMachine certificate store permissions.", ex.Message);
            }
        }

        private static void CheckVps(DiagnosticReport report, AppSettings settings, Func<object> statusProvider)
        {
            if (settings == null || !string.Equals(settings.PublicMode, "Vps", StringComparison.OrdinalIgnoreCase))
                return;
            var vps = statusProvider() as VpsRuntimeStatus;
            var ok = vps != null && vps.Connected;
            Add(report, "vps", ok, ok ? "VPS reverse tunnel is connected." : "VPS reverse tunnel is not connected.", ok ? null : "Check VPS SSH credentials, forwarding permissions, remote port and reverse proxy.", vps?.ToString());
        }

        private void CheckVps(DiagnosticReport report, AppSettings settings)
        {
            CheckVps(report, settings, _vpsStatus);
        }

        private static async Task CheckPublicUrl(DiagnosticReport report, AppSettings settings)
        {
            if (settings == null || string.Equals(settings.PublicMode, "Local", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(settings.PublicBaseUrl))
                return;
            if (settings.EnableHttps && string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
            {
                Add(report, "public_url", false, "Public HTTPS is not ready because the certificate has not been installed.", DnsUpdateService.IsConfigured(settings) ? "Complete DNS-01 certificate issuance, then the public user and administrator paths will open on port " + settings.HttpsPort + "." : "Configure a certificate method or VPS/reverse proxy.", settings.PublicBaseUrl);
                return;
            }
            try
            {
                var url = settings.PublicBaseUrl.TrimEnd('/') + "/favicon.ico";
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                using (var response = await Client.SendAsync(request).ConfigureAwait(false))
                {
                    var ok = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent;
                    Add(report, "public_url", ok, ok ? "The configured public URL answered an HTTPS request." : "The configured public URL returned HTTP " + (int)response.StatusCode + ".", ok ? null : "Check the router forwarding for the configured APTOFI HTTPS port, Windows Firewall, certificate binding and service state.", url);
                }
            }
            catch (Exception ex)
            {
                Add(report, "public_url", false, "The configured public URL could not be reached from this computer.", "Check the configured HTTPS port forwarding, Windows Firewall and certificate. Some routers without NAT loopback can make a public URL fail only from inside the same LAN; test from a mobile connection too.", ex.Message + " URL=" + settings.PublicBaseUrl);
            }
        }

        private static async Task CheckPublicHttpRedirect(DiagnosticReport report, AppSettings settings)
        {
            if (settings == null || string.Equals(settings.PublicMode, "Local", StringComparison.OrdinalIgnoreCase) || !settings.EnableHttps || string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
                return;
            string host = null;
            if (!string.IsNullOrWhiteSpace(settings.HttpsIdentifier))
                host = settings.HttpsIdentifier;
            else if (!string.IsNullOrWhiteSpace(settings.Domain))
                host = settings.Domain;
            else if (Uri.TryCreate(settings.PublicBaseUrl, UriKind.Absolute, out var publicUri))
                host = publicUri.Host;
            if (string.IsNullOrWhiteSpace(host))
                return;
            var userPath = HttpUtil.NormalizeSecretPath(settings.UserPath, "/user_login_disk");
            var source = new UriBuilder("http", host, settings.HttpPort, userPath).Uri.ToString();
            var expected = new UriBuilder("https", host, settings.HttpsPort, userPath).Uri.ToString();
            try
            {
                using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) })
                using (var request = new HttpRequestMessage(HttpMethod.Get, source))
                using (var response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    var location = response.Headers.Location == null ? null : response.Headers.Location.ToString();
                    var statusCode = (int)response.StatusCode;
                    var redirect = statusCode == 301 || statusCode == 302 || statusCode == 307 || statusCode == 308;
                    var ok = redirect && !string.IsNullOrWhiteSpace(location) && location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                    Add(report, "http_redirect", ok, ok ? "Public HTTP redirects to HTTPS." : "Public HTTP did not redirect to HTTPS.", ok ? null : "Use HTTP on port " + settings.HttpPort + " only as a redirect entry point. Port " + settings.HttpsPort + " is HTTPS-only and must be opened with https://.", "HTTP=" + source + "; Location=" + (location ?? "<none>") + "; Expected=" + expected);
                }
            }
            catch (Exception ex)
            {
                Add(report, "http_redirect", false, "The public HTTP redirect endpoint could not be reached.", "Forward public TCP " + settings.HttpPort + " to the APTOFI HTTP port if you want http:// links to redirect automatically. The HTTPS service remains on port " + settings.HttpsPort + ".", ex.Message + " URL=" + source);
            }
        }

        private static void Add(DiagnosticReport report, string key, bool ok, string message, string action, string details = null)
        {
            report.Items.Add(new DiagnosticItem { Key = key, Ok = ok, Message = message, Action = action, Details = details });
        }

        private static string BuildSummary(DiagnosticReport report)
        {
            var failed = report.Items.Where(x => !x.Ok).ToList();
            return failed.Count == 0 ? "All tested APTOFI components are working." : "Problems detected: " + string.Join(", ", failed.Select(x => x.Key));
        }
    }

    internal sealed class DiagnosticReport
    {
        public DateTime StartedUtc { get; set; }
        public DateTime FinishedUtc { get; set; }
        public string Summary { get; set; }
        public List<DiagnosticItem> Items { get; set; } = new List<DiagnosticItem>();
    }

    internal sealed class DiagnosticItem
    {
        public string Key { get; set; }
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string Action { get; set; }
        public string Details { get; set; }
    }

    internal sealed class TransferTestResult
    {
        public double WriteMiBs { get; set; }
        public double ReadMiBs { get; set; }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Logging;
using APTOFI.FileSharing.Security;
using Newtonsoft.Json.Linq;

namespace APTOFI.FileSharing.Network
{
    internal sealed class DnsUpdateService
    {
        private readonly Database _db;
        private readonly CryptoService _crypto;
        private readonly LogService _log;
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private string _lastSuccessfulAddress;

        public string LastPublicDnsError { get; private set; }

        public DnsUpdateService(Database db, CryptoService crypto, LogService log)
        {
            _db = db;
            _crypto = crypto;
            _log = log;
        }

        public static bool IsConfigured(AppSettings settings)
        {
            return settings != null && string.Equals(settings.DnsUpdateMode, "Rfc2136", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.DnsServer) && !string.IsNullOrWhiteSpace(settings.DnsZone) && !string.IsNullOrWhiteSpace(settings.DnsTsigKeyName) && !string.IsNullOrWhiteSpace(settings.DnsTsigSecretProtected);
        }

        public static string NormalizeZone(string value)
        {
            return NormalizeHost(value, "DNS zone");
        }

        public static string NormalizeKeyName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var text = value.Trim().ToLowerInvariant().Trim('.');
            if (text.Length < 1 || text.Length > 253)
                throw new InvalidOperationException("The DNS TSIG key name is invalid.");
            foreach (var part in text.Split('.'))
            {
                if (part.Length < 1 || part.Length > 63 || !Regex.IsMatch(part, "^[a-z0-9_](?:[a-z0-9_-]{0,61}[a-z0-9_])?$", RegexOptions.CultureInvariant))
                    throw new InvalidOperationException("The DNS TSIG key name contains an invalid DNS label.");
            }
            return text;
        }

        public static string NormalizeServer(string value)
        {
            return NormalizeHost(value, "DNS update server");
        }

        public static string NormalizeAlgorithm(string value)
        {
            var algorithm = (value ?? "hmac-sha256").Trim().ToLowerInvariant().TrimEnd('.');
            if (algorithm == "hmac-sha1" || algorithm == "hmac-sha256" || algorithm == "hmac-sha512")
                return algorithm;
            throw new InvalidOperationException("Unsupported TSIG algorithm. Use hmac-sha1, hmac-sha256 or hmac-sha512.");
        }

        public async Task<DnsUpdateResult> UpdateAddressAsync(AppSettings settings)
        {
            if (!IsConfigured(settings))
                return DnsUpdateResult.Fail("RFC2136 DNS update is not configured.");
            if (!settings.DnsAutoUpdateAddress)
                return DnsUpdateResult.Ok(NormalizeZone(settings.Domain ?? settings.DnsZone), "Address synchronization is disabled.");
            try
            {
                var zone = NormalizeZone(settings.DnsZone);
                var keyName = NormalizeKeyName(settings.DnsTsigKeyName);
                var secret = ReadSecret(settings);
                var publicIpText = (await _http.GetStringAsync("https://api.ipify.org").ConfigureAwait(false)).Trim();
                if (!IPAddress.TryParse(publicIpText, out var publicIp) || publicIp.AddressFamily != AddressFamily.InterNetwork)
                    throw new InvalidOperationException("The detected public IPv4 address is invalid: " + publicIpText);
                var domain = NormalizeZone(settings.Domain ?? settings.DnsZone);
                var client = new Rfc2136TsigClient(NormalizeServer(settings.DnsServer), zone, keyName, secret, settings.DnsTsigAlgorithm);
                await client.ReplaceAAsync(domain, publicIp).ConfigureAwait(false);
                settings.Domain = NormalizeZone(settings.Domain ?? zone);
                settings.HttpsIdentifier = settings.Domain;
                settings.LastDnsError = null;
                settings.LastDnsUpdateUtc = DateTime.UtcNow;
                _db.SaveSettings(settings);
                var currentAddress = publicIp.ToString();
                if (!string.Equals(_lastSuccessfulAddress, currentAddress, StringComparison.Ordinal))
                    _log.App("dns-address-updated domain=" + zone + " ip=" + currentAddress);
                _lastSuccessfulAddress = currentAddress;
                return DnsUpdateResult.Ok(settings.Domain, currentAddress);
            }
            catch (Exception ex)
            {
                settings.LastDnsError = ex.Message;
                _db.SaveSettings(settings);
                _log.App("dns-address-error " + ex);
                return DnsUpdateResult.Fail(ex.Message);
            }
        }

        public async Task SetTxtAsync(AppSettings settings, string value)
        {
            if (!IsConfigured(settings))
                throw new InvalidOperationException("RFC2136 DNS update is not configured.");
            if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 255)
                throw new InvalidOperationException("The DNS-01 TXT value is invalid.");
            var zone = NormalizeZone(settings.DnsZone);
            var domain = NormalizeZone(settings.Domain ?? settings.HttpsIdentifier);
            var keyName = NormalizeKeyName(settings.DnsTsigKeyName);
            var secret = ReadSecret(settings);
            var client = new Rfc2136TsigClient(NormalizeServer(settings.DnsServer), zone, keyName, secret, settings.DnsTsigAlgorithm);
            var recordName = "_acme-challenge." + domain;
            try
            {
                await client.AddTxtAsync(recordName, value, 60).ConfigureAwait(false);
            }
            catch (Exception firstError)
            {
                try
                {
                    await client.DeleteTxtAsync(recordName).ConfigureAwait(false);
                    await client.AddTxtAsync(recordName, value, 60).ConfigureAwait(false);
                }
                catch (Exception retryError)
                {
                    throw new InvalidOperationException("DNS-01 TXT update failed. First attempt: " + firstError.Message + " Retry after TXT cleanup: " + retryError.Message);
                }
            }
            _log.App("dns-txt-set domain=" + zone);
        }

        public async Task ClearTxtAsync(AppSettings settings)
        {
            if (!IsConfigured(settings))
                return;
            try
            {
                var zone = NormalizeZone(settings.DnsZone);
                var domain = NormalizeZone(settings.Domain ?? settings.HttpsIdentifier);
                var keyName = NormalizeKeyName(settings.DnsTsigKeyName);
                var secret = ReadSecret(settings);
                var client = new Rfc2136TsigClient(NormalizeServer(settings.DnsServer), zone, keyName, secret, settings.DnsTsigAlgorithm);
                await client.DeleteTxtAsync("_acme-challenge." + domain).ConfigureAwait(false);
                _log.App("dns-txt-cleared domain=" + zone);
            }
            catch (Exception ex)
            {
                _log.App("dns-txt-clear-error " + ex.Message);
            }
        }

        public async Task<bool> WaitForTxtAsync(string domain, string expectedValue, TimeSpan timeout)
        {
            LastPublicDnsError = null;
            var name = "_acme-challenge." + NormalizeZone(domain);
            var until = DateTime.UtcNow.Add(timeout);
            var consecutiveServfail = 0;
            while (DateTime.UtcNow < until)
            {
                try
                {
                    var url = "https://dns.google/resolve?name=" + Uri.EscapeDataString(name) + "&type=TXT";
                    var text = await _http.GetStringAsync(url).ConfigureAwait(false);
                    var json = JObject.Parse(text);
                    var status = (int?)json["Status"] ?? -1;
                    if (status == 2)
                    {
                        consecutiveServfail++;
                        LastPublicDnsError = "Public DNS returned SERVFAIL for " + name + ". The authoritative DNS provider or zone is currently failing.";
                        if (consecutiveServfail >= 3)
                            return false;
                    }
                    else
                    {
                        consecutiveServfail = 0;
                        if (status != 0 && status != 3)
                            LastPublicDnsError = "Public DNS returned status " + status + " for " + name + ".";
                    }
                    var answers = json["Answer"] as JArray;
                    if (answers != null)
                    {
                        foreach (var answer in answers.Children<JObject>())
                        {
                            var data = ((string)answer["data"] ?? string.Empty).Trim();
                            if (data.Length >= 2 && data[0] == '"' && data[data.Length - 1] == '"')
                                data = data.Substring(1, data.Length - 2);
                            data = data.Replace("\\\"", "\"");
                            if (string.Equals(data, expectedValue, StringComparison.Ordinal))
                            {
                                LastPublicDnsError = null;
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LastPublicDnsError = ex.Message;
                }
                await Task.Delay(2000).ConfigureAwait(false);
            }
            return false;
        }

        public async Task<DnsUpdateResult> TestAsync(AppSettings settings)
        {
            if (!IsConfigured(settings))
                return DnsUpdateResult.Fail("RFC2136 DNS update is not configured.");
            var update = await UpdateAddressAsync(settings).ConfigureAwait(false);
            if (!update.Success)
                return update;
            try
            {
                var domain = NormalizeZone(settings.Domain ?? settings.DnsZone);
                var zone = NormalizeZone(settings.DnsZone);
                if (!string.Equals(domain, zone, StringComparison.OrdinalIgnoreCase) && !domain.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
                    return DnsUpdateResult.Fail("The configured domain is not inside the configured RFC2136 DNS zone.");
                var addresses = await Dns.GetHostAddressesAsync(domain).ConfigureAwait(false);
                var ipv4 = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 == null)
                    return DnsUpdateResult.Fail("The configured domain does not currently resolve to an IPv4 address.");
                if (settings.DnsAutoUpdateAddress && !string.Equals(ipv4.ToString(), update.Details, StringComparison.OrdinalIgnoreCase))
                    return DnsUpdateResult.Fail("The configured domain still resolves to " + ipv4 + " instead of " + update.Details + ". DNS propagation may still be in progress.");
                settings.LastDnsError = null;
                settings.LastDnsUpdateUtc = DateTime.UtcNow;
                _db.SaveSettings(settings);
                return DnsUpdateResult.Ok(domain, ipv4.ToString());
            }
            catch (Exception ex)
            {
                settings.LastDnsError = ex.Message;
                _db.SaveSettings(settings);
                return DnsUpdateResult.Fail(ex.Message);
            }
        }

        private string ReadSecret(AppSettings settings)
        {
            var value = _crypto.UnprotectString(settings.DnsTsigSecretProtected);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("DNS TSIG secret is missing.");
            try
            {
                Convert.FromBase64String(value.Trim());
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("DNS TSIG secret is not valid Base64.");
            }
            return value.Trim();
        }

        private static string NormalizeRecordName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var text = value.Trim().ToLowerInvariant().Trim('.');
            if (text.Length < 1 || text.Length > 253)
                throw new InvalidOperationException("The DNS record name is invalid.");
            foreach (var part in text.Split('.'))
            {
                if (part.Length < 1 || part.Length > 63 || !Regex.IsMatch(part, "^[a-z0-9_](?:[a-z0-9_-]{0,61}[a-z0-9_])?$", RegexOptions.CultureInvariant))
                    throw new InvalidOperationException("The DNS record name contains an invalid DNS label.");
            }
            return text;
        }

        private static string NormalizeHost(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var text = value.Trim().ToLowerInvariant();
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
                text = uri.Host;
            var slash = text.IndexOf('/');
            if (slash >= 0)
                text = text.Substring(0, slash);
            var colon = text.IndexOf(':');
            if (colon >= 0)
                text = text.Substring(0, colon);
            text = text.Trim('.');
            if (text.Length < 1 || text.Length > 253)
                throw new InvalidOperationException("The " + label + " is invalid.");
            foreach (var part in text.Split('.'))
            {
                if (part.Length < 1 || part.Length > 63 || !Regex.IsMatch(part, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant))
                    throw new InvalidOperationException("The " + label + " contains an invalid DNS label.");
            }
            return text;
        }

        private sealed class Rfc2136TsigClient
        {
            private const ushort TypeA = 1;
            private const ushort TypeSoa = 6;
            private const ushort TypeTxt = 16;
            private const ushort TypeTsig = 250;
            private const ushort ClassIn = 1;
            private const ushort ClassAny = 255;
            private readonly string _server;
            private readonly string _zone;
            private readonly string _keyName;
            private readonly byte[] _secret;
            private readonly string _algorithm;

            public Rfc2136TsigClient(string server, string zone, string keyName, string secret, string algorithm)
            {
                _server = server;
                _zone = NormalizeZone(zone);
                _keyName = NormalizeKeyName(keyName);
                _secret = Convert.FromBase64String(secret);
                _algorithm = NormalizeAlgorithm(algorithm);
            }

            public Task ReplaceAAsync(string name, IPAddress address)
            {
                if (address == null || address.AddressFamily != AddressFamily.InterNetwork)
                    throw new InvalidOperationException("Only IPv4 A records are supported for automatic address synchronization.");
                return SendUpdateAsync(new[]
                {
                    UpdateRecord.DeleteSet(NormalizeZone(name), TypeA),
                    UpdateRecord.Add(NormalizeZone(name), TypeA, 60, address.GetAddressBytes())
                });
            }

            public Task AddTxtAsync(string name, string value, uint ttl)
            {
                var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                if (bytes.Length > 255)
                    throw new InvalidOperationException("TXT values longer than 255 bytes are not supported.");
                var data = new byte[bytes.Length + 1];
                data[0] = (byte)bytes.Length;
                Buffer.BlockCopy(bytes, 0, data, 1, bytes.Length);
                return SendUpdateAsync(new[] { UpdateRecord.Add(NormalizeRecordName(name), TypeTxt, ttl, data) });
            }

            public Task DeleteTxtAsync(string name)
            {
                return SendUpdateAsync(new[] { UpdateRecord.DeleteSet(NormalizeRecordName(name), TypeTxt) });
            }

            private async Task SendUpdateAsync(UpdateRecord[] updates)
            {
                var idBytes = new byte[2];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(idBytes);
                var id = (ushort)((idBytes[0] << 8) | idBytes[1]);
                var unsigned = BuildUnsignedMessage(id, updates);
                var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var fudge = (ushort)300;
                byte[] mac;
                using (var hmac = CreateHmac(_algorithm, _secret))
                    mac = hmac.ComputeHash(BuildMacInput(unsigned, now, fudge));
                var final = BuildSignedMessage(unsigned, id, now, fudge, mac);
                Exception tcpError = null;
                try
                {
                    var tcpResponse = await SendTcpAsync(final).ConfigureAwait(false);
                    ValidateResponse(tcpResponse, id);
                    return;
                }
                catch (Exception ex)
                {
                    tcpError = ex;
                }
                try
                {
                    var udpResponse = await SendUdpAsync(final).ConfigureAwait(false);
                    if (udpResponse.Length < 12 || (udpResponse[2] & 0x02) != 0)
                        throw new InvalidOperationException("DNS update UDP response is truncated.");
                    ValidateResponse(udpResponse, id);
                }
                catch (Exception udpError)
                {
                    throw new InvalidOperationException("RFC2136 DNS update failed over TCP and UDP. TCP: " + tcpError.Message + " UDP: " + udpError.Message);
                }
            }


            private static HMAC CreateHmac(string algorithm, byte[] key)
            {
                switch (NormalizeAlgorithm(algorithm))
                {
                    case "hmac-sha1": return new HMACSHA1(key);
                    case "hmac-sha512": return new HMACSHA512(key);
                    default: return new HMACSHA256(key);
                }
            }

            private byte[] BuildUnsignedMessage(ushort id, UpdateRecord[] updates)
            {
                using (var ms = new MemoryStream())
                {
                    WriteU16(ms, id);
                    WriteU16(ms, 0x2800);
                    WriteU16(ms, 1);
                    WriteU16(ms, 0);
                    WriteU16(ms, (ushort)updates.Length);
                    WriteU16(ms, 0);
                    WriteName(ms, _zone);
                    WriteU16(ms, TypeSoa);
                    WriteU16(ms, ClassIn);
                    foreach (var update in updates)
                        WriteUpdate(ms, update);
                    return ms.ToArray();
                }
            }

            private byte[] BuildSignedMessage(byte[] unsigned, ushort id, ulong timeSigned, ushort fudge, byte[] mac)
            {
                var message = (byte[])unsigned.Clone();
                message[10] = 0;
                message[11] = 1;
                using (var ms = new MemoryStream())
                {
                    ms.Write(message, 0, message.Length);
                    WriteName(ms, _keyName);
                    WriteU16(ms, TypeTsig);
                    WriteU16(ms, ClassAny);
                    WriteU32(ms, 0);
                    using (var rdata = new MemoryStream())
                    {
                        WriteName(rdata, _algorithm);
                        WriteU48(rdata, timeSigned);
                        WriteU16(rdata, fudge);
                        WriteU16(rdata, (ushort)mac.Length);
                        rdata.Write(mac, 0, mac.Length);
                        WriteU16(rdata, id);
                        WriteU16(rdata, 0);
                        WriteU16(rdata, 0);
                        var bytes = rdata.ToArray();
                        WriteU16(ms, (ushort)bytes.Length);
                        ms.Write(bytes, 0, bytes.Length);
                    }
                    return ms.ToArray();
                }
            }

            private byte[] BuildMacInput(byte[] unsigned, ulong timeSigned, ushort fudge)
            {
                using (var ms = new MemoryStream())
                {
                    ms.Write(unsigned, 0, unsigned.Length);
                    WriteName(ms, _keyName);
                    WriteU16(ms, ClassAny);
                    WriteU32(ms, 0);
                    WriteName(ms, _algorithm);
                    WriteU48(ms, timeSigned);
                    WriteU16(ms, fudge);
                    WriteU16(ms, 0);
                    WriteU16(ms, 0);
                    return ms.ToArray();
                }
            }

            private async Task<byte[]> SendUdpAsync(byte[] message)
            {
                var addresses = await Dns.GetHostAddressesAsync(_server).ConfigureAwait(false);
                var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (address == null)
                    throw new InvalidOperationException("DNS update server could not be resolved.");
                using (var udp = new UdpClient(address.AddressFamily))
                {
                    udp.Connect(new IPEndPoint(address, 53));
                    await udp.SendAsync(message, message.Length).ConfigureAwait(false);
                    var receive = udp.ReceiveAsync();
                    var completed = await Task.WhenAny(receive, Task.Delay(8000)).ConfigureAwait(false);
                    if (completed != receive)
                        throw new TimeoutException("DNS update timed out over UDP.");
                    return receive.Result.Buffer;
                }
            }

            private async Task<byte[]> SendTcpAsync(byte[] message)
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(_server, 53);
                    var connected = await Task.WhenAny(connect, Task.Delay(8000)).ConfigureAwait(false);
                    if (connected != connect)
                        throw new TimeoutException("DNS update timed out while connecting over TCP.");
                    await connect.ConfigureAwait(false);
                    using (var stream = client.GetStream())
                    {
                        var prefix = new[] { (byte)(message.Length >> 8), (byte)message.Length };
                        await stream.WriteAsync(prefix, 0, prefix.Length).ConfigureAwait(false);
                        await stream.WriteAsync(message, 0, message.Length).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                        var lengthBytes = await ReadExactAsync(stream, 2).ConfigureAwait(false);
                        var length = (lengthBytes[0] << 8) | lengthBytes[1];
                        if (length < 12 || length > 65535)
                            throw new InvalidOperationException("DNS update server returned an invalid response length.");
                        return await ReadExactAsync(stream, length).ConfigureAwait(false);
                    }
                }
            }

            private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
            {
                var buffer = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    var readTask = stream.ReadAsync(buffer, offset, count - offset);
                    var completed = await Task.WhenAny(readTask, Task.Delay(8000)).ConfigureAwait(false);
                    if (completed != readTask)
                        throw new TimeoutException("DNS update timed out while reading the response.");
                    var read = await readTask.ConfigureAwait(false);
                    if (read <= 0)
                        throw new IOException("DNS update server closed the connection unexpectedly.");
                    offset += read;
                }
                return buffer;
            }

            private static void ValidateResponse(byte[] response, ushort id)
            {
                if (response == null || response.Length < 12)
                    throw new InvalidOperationException("DNS update server returned an invalid response.");
                var responseId = (ushort)((response[0] << 8) | response[1]);
                if (responseId != id)
                    throw new InvalidOperationException("DNS update response transaction ID does not match the request.");
                if ((response[2] & 0x80) == 0)
                    throw new InvalidOperationException("DNS update response is not marked as a response.");
                var rcode = response[3] & 0x0F;
                if (rcode == 0)
                    return;
                if (rcode == 2)
                    throw new InvalidOperationException("DNS update server returned RCODE 2 (SERVFAIL). The authoritative DNS server failed while applying the update. This can be a DNS-provider-side failure even when the TSIG key and A-record update are valid.");
                if (rcode == 5 || rcode == 9 || rcode == 10)
                    throw new InvalidOperationException("DNS update failed with RCODE " + rcode + " (" + RcodeName(rcode) + "). Check the authoritative zone and TSIG permissions, key name, algorithm and secret.");
                throw new InvalidOperationException("DNS update failed with RCODE " + rcode + " (" + RcodeName(rcode) + ").");
            }

            private static string RcodeName(int code)
            {
                switch (code)
                {
                    case 1: return "FORMERR";
                    case 2: return "SERVFAIL";
                    case 3: return "NXDOMAIN";
                    case 4: return "NOTIMP";
                    case 5: return "REFUSED";
                    case 9: return "NOTAUTH";
                    case 10: return "NOTZONE";
                    default: return "DNS error";
                }
            }

            private static void WriteUpdate(Stream stream, UpdateRecord record)
            {
                WriteName(stream, record.Name);
                WriteU16(stream, record.Type);
                WriteU16(stream, record.IsDeleteSet ? ClassAny : ClassIn);
                WriteU32(stream, record.IsDeleteSet ? 0u : record.Ttl);
                if (record.IsDeleteSet)
                {
                    WriteU16(stream, 0);
                    return;
                }
                WriteU16(stream, (ushort)record.Data.Length);
                stream.Write(record.Data, 0, record.Data.Length);
            }

            private static void WriteName(Stream stream, string name)
            {
                var normalized = (name ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
                foreach (var label in normalized.Split('.'))
                {
                    var bytes = Encoding.ASCII.GetBytes(label);
                    if (bytes.Length < 1 || bytes.Length > 63)
                        throw new InvalidOperationException("Invalid DNS label in " + name + ".");
                    stream.WriteByte((byte)bytes.Length);
                    stream.Write(bytes, 0, bytes.Length);
                }
                stream.WriteByte(0);
            }

            private static void WriteU16(Stream stream, ushort value)
            {
                stream.WriteByte((byte)(value >> 8));
                stream.WriteByte((byte)value);
            }

            private static void WriteU32(Stream stream, uint value)
            {
                stream.WriteByte((byte)(value >> 24));
                stream.WriteByte((byte)(value >> 16));
                stream.WriteByte((byte)(value >> 8));
                stream.WriteByte((byte)value);
            }

            private static void WriteU48(Stream stream, ulong value)
            {
                stream.WriteByte((byte)(value >> 40));
                stream.WriteByte((byte)(value >> 32));
                stream.WriteByte((byte)(value >> 24));
                stream.WriteByte((byte)(value >> 16));
                stream.WriteByte((byte)(value >> 8));
                stream.WriteByte((byte)value);
            }

            private sealed class UpdateRecord
            {
                public string Name { get; private set; }
                public ushort Type { get; private set; }
                public uint Ttl { get; private set; }
                public byte[] Data { get; private set; }
                public bool IsDeleteSet { get; private set; }

                public static UpdateRecord DeleteSet(string name, ushort type)
                {
                    return new UpdateRecord { Name = name, Type = type, IsDeleteSet = true, Data = new byte[0] };
                }

                public static UpdateRecord Add(string name, ushort type, uint ttl, byte[] data)
                {
                    return new UpdateRecord { Name = name, Type = type, Ttl = ttl, Data = data ?? new byte[0], IsDeleteSet = false };
                }
            }
        }
    }

    internal sealed class DnsUpdateResult
    {
        public bool Success { get; private set; }
        public string Domain { get; private set; }
        public string Details { get; private set; }
        public string Error { get; private set; }

        public static DnsUpdateResult Ok(string domain, string details)
        {
            return new DnsUpdateResult { Success = true, Domain = domain, Details = details };
        }

        public static DnsUpdateResult Fail(string error)
        {
            return new DnsUpdateResult { Success = false, Error = error };
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Logging;
using APTOFI.FileSharing.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace APTOFI.FileSharing.Network
{
    internal sealed class AcmeService
    {
        private const string DirectoryUrl = "https://acme-v02.api.letsencrypt.org/directory";
        private readonly Database _db;
        private readonly CryptoService _crypto;
        private readonly WindowsNetworkService _windows;
        private readonly LogService _log;
        private readonly DnsUpdateService _dns;
        private readonly ConcurrentDictionary<string, string> _challenges = new ConcurrentDictionary<string, string>();
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private JObject _directory;
        private string _nonce;
        private string _kid;
        private RSACryptoServiceProvider _accountKey;
        private JObject _jwk;

        public AcmeService(Database db, CryptoService crypto, WindowsNetworkService windows, LogService log)
        {
            _db = db;
            _crypto = crypto;
            _windows = windows;
            _log = log;
            _dns = new DnsUpdateService(db, crypto, log);
        }

        public bool TryGetChallenge(string token, out string response)
        {
            return _challenges.TryGetValue(token, out response);
        }

        public Task<DnsUpdateResult> SyncPublicDnsAsync()
        {
            return _dns.UpdateAddressAsync(_db.GetSettings());
        }

        public async Task<CertificateIssueResult> EnsureCertificateAsync(bool force)
        {
            var settings = _db.GetSettings();
            if (settings == null || !settings.EnableHttps)
                return CertificateIssueResult.Fail("HTTPS is disabled.");
            if (!settings.AcmeTermsAccepted)
                return CertificateIssueResult.Fail("Certificate authority terms were not accepted in settings.");
            var identifier = ResolveIdentifier(settings);
            if (string.IsNullOrWhiteSpace(identifier))
                return CertificateIssueResult.Fail("Public IP or domain is not configured.");
            if (!force && HasUsableCertificate(settings, identifier))
                return CertificateIssueResult.Ok(settings.CertificateThumbprint, false);
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                if (DnsUpdateService.IsConfigured(settings))
                {
                    var dnsUpdate = await _dns.UpdateAddressAsync(settings).ConfigureAwait(false);
                    if (!dnsUpdate.Success)
                        throw new InvalidOperationException("DNS address update failed: " + dnsUpdate.Error);
                    identifier = ResolveIdentifier(settings);
                }
                await InitializeAsync(settings).ConfigureAwait(false);
                var isIp = IPAddress.TryParse(identifier, out _);
                var orderPayload = new JObject
                {
                    ["identifiers"] = new JArray(new JObject { ["type"] = isIp ? "ip" : "dns", ["value"] = identifier }),
                    ["profile"] = isIp ? "shortlived" : "tlsserver"
                };
                var orderResponse = await SignedPostAsync((string)_directory["newOrder"], orderPayload.ToString(Formatting.None), false).ConfigureAwait(false);
                EnsureSuccess(orderResponse);
                var order = JObject.Parse(await orderResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
                foreach (var authUrl in order["authorizations"].Values<string>())
                {
                    if (DnsUpdateService.IsConfigured(settings) && !isIp)
                        await CompleteDnsChallengeAsync(settings, identifier, authUrl).ConfigureAwait(false);
                    else
                        await CompleteHttpChallengeAsync(authUrl).ConfigureAwait(false);
                }
                var bundle = CreateCsr(identifier, isIp);
                var finalizeUrl = (string)order["finalize"];
                var finalize = await SignedPostAsync(finalizeUrl, new JObject { ["csr"] = CryptoService.Base64Url(bundle.Csr.GetEncoded()) }.ToString(Formatting.None), false).ConfigureAwait(false);
                EnsureSuccess(finalize);
                var orderUrl = orderResponse.Headers.Location?.ToString();
                if (string.IsNullOrWhiteSpace(orderUrl))
                    throw new InvalidOperationException("ACME order location is missing.");
                var validOrder = await PollOrderAsync(orderUrl).ConfigureAwait(false);
                var certificateUrl = (string)validOrder["certificate"];
                if (string.IsNullOrWhiteSpace(certificateUrl))
                    throw new InvalidOperationException("ACME certificate URL is missing.");
                var certificateResponse = await SignedPostAsync(certificateUrl, string.Empty, false).ConfigureAwait(false);
                EnsureSuccess(certificateResponse);
                var pem = await certificateResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                var installed = InstallCertificate(identifier, pem, bundle.KeyPair);
                var bind = _windows.BindCertificate(settings.HttpsPort, installed.Thumbprint);
                if (!bind.Success)
                    throw new InvalidOperationException("HTTP.sys TLS binding failed: " + bind.Output);
                settings.CertificateThumbprint = installed.Thumbprint;
                settings.HttpsIdentifier = identifier;
                settings.LastCertificateError = null;
                if (string.Equals(settings.PublicMode, "Direct", StringComparison.OrdinalIgnoreCase))
                    settings.PublicBaseUrl = BuildDirectBaseUrl(identifier, settings.HttpsPort, true);
                _db.SaveSettings(settings);
                _log.App("certificate-issued identifier=" + identifier + " thumbprint=" + installed.Thumbprint);
                return CertificateIssueResult.Ok(installed.Thumbprint, true);
            }
            catch (Exception ex)
            {
                settings.LastCertificateError = BuildFriendlyCertificateError(settings, ex);
                _db.SaveSettings(settings);
                _log.App("certificate-error " + ex);
                return CertificateIssueResult.Fail(settings.LastCertificateError);
            }
            finally
            {
                _challenges.Clear();
            }
        }

        private static string BuildDirectBaseUrl(string identifier, int port, bool https)
        {
            var builder = new UriBuilder(https ? "https" : "http", identifier, port);
            return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private static string BuildFriendlyCertificateError(AppSettings settings, Exception ex)
        {
            var message = ex == null ? "Unknown certificate error." : ex.Message;
            if (DnsUpdateService.IsConfigured(settings))
                return "HTTPS certificate validation through RFC2136 DNS-01 failed. Public TCP 80 and 443 are not required for the certificate check. Verify the domain, DNS update server, zone, TSIG key name, algorithm, secret and Internet access. Technical error: " + message;
            if (message.IndexOf("HTTP-01 validation", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("authorization", StringComparison.OrdinalIgnoreCase) >= 0)
                return "HTTPS certificate validation failed. ACME HTTP-01 checks the public IP or domain on TCP port 80. Configure RFC2136 TSIG DNS-01 or use VPS/tunnel mode if public TCP 80 is unavailable. Technical error: " + message;
            return message;
        }

        private async Task InitializeAsync(AppSettings settings)
        {
            _directory = JObject.Parse(await _http.GetStringAsync(DirectoryUrl).ConfigureAwait(false));
            LoadOrCreateAccountKey();
            _kid = settings.AcmeAccountKid;
            if (!string.IsNullOrWhiteSpace(_kid))
                return;
            var email = settings.AcmeEmail;
            if (string.IsNullOrWhiteSpace(email))
                email = _db.Users.FindOne(x => x.Role == "admin")?.Email;
            var payload = new JObject
            {
                ["termsOfServiceAgreed"] = true,
                ["contact"] = string.IsNullOrWhiteSpace(email) ? new JArray() : new JArray("mailto:" + email)
            };
            var response = await SignedPostAsync((string)_directory["newAccount"], payload.ToString(Formatting.None), true).ConfigureAwait(false);
            EnsureSuccess(response);
            _kid = response.Headers.Location?.ToString();
            if (string.IsNullOrWhiteSpace(_kid))
                throw new InvalidOperationException("ACME account location is missing.");
            settings.AcmeAccountKid = _kid;
            _db.SaveSettings(settings);
        }

        private void LoadOrCreateAccountKey()
        {
            _accountKey?.Dispose();
            _accountKey = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
            if (File.Exists(AppPaths.AcmeKeyPath))
            {
                var protectedXml = File.ReadAllText(AppPaths.AcmeKeyPath);
                _accountKey.FromXmlString(_crypto.UnprotectString(protectedXml));
            }
            else
            {
                File.WriteAllText(AppPaths.AcmeKeyPath, _crypto.ProtectString(_accountKey.ToXmlString(true)));
            }
            var p = _accountKey.ExportParameters(false);
            _jwk = new JObject
            {
                ["e"] = CryptoService.Base64Url(p.Exponent),
                ["kty"] = "RSA",
                ["n"] = CryptoService.Base64Url(p.Modulus)
            };
        }


        private async Task CompleteDnsChallengeAsync(AppSettings settings, string identifier, string authorizationUrl)
        {
            var authResponse = await SignedPostAsync(authorizationUrl, string.Empty, false).ConfigureAwait(false);
            EnsureSuccess(authResponse);
            var auth = JObject.Parse(await authResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (string.Equals((string)auth["status"], "valid", StringComparison.OrdinalIgnoreCase))
                return;
            var challenge = auth["challenges"].Children<JObject>().FirstOrDefault(x => string.Equals((string)x["type"], "dns-01", StringComparison.Ordinal));
            if (challenge == null)
                throw new InvalidOperationException("ACME server did not offer DNS-01.");
            var token = (string)challenge["token"];
            var challengeUrl = (string)challenge["url"];
            var keyAuth = token + "." + JwkThumbprint();
            string dnsValue;
            using (var sha = SHA256.Create())
                dnsValue = CryptoService.Base64Url(sha.ComputeHash(Encoding.UTF8.GetBytes(keyAuth)));
            await _dns.SetTxtAsync(settings, dnsValue).ConfigureAwait(false);
            try
            {
                var visible = await _dns.WaitForTxtAsync(identifier, dnsValue, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                if (!visible)
                {
                    var detail = string.IsNullOrWhiteSpace(_dns.LastPublicDnsError) ? string.Empty : " " + _dns.LastPublicDnsError;
                    throw new TimeoutException("DNS-01 TXT record did not become visible in public DNS within two minutes." + detail);
                }
                var trigger = await SignedPostAsync(challengeUrl, "{}", false).ConfigureAwait(false);
                EnsureSuccess(trigger);
                for (var i = 0; i < 60; i++)
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    var poll = await SignedPostAsync(authorizationUrl, string.Empty, false).ConfigureAwait(false);
                    EnsureSuccess(poll);
                    var current = JObject.Parse(await poll.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var status = (string)current["status"];
                    if (status == "valid")
                        return;
                    if (status == "invalid")
                        throw new InvalidOperationException("DNS-01 validation failed: " + current.ToString(Formatting.None));
                }
                throw new TimeoutException("DNS-01 validation timed out.");
            }
            finally
            {
                await _dns.ClearTxtAsync(settings).ConfigureAwait(false);
            }
        }

        private async Task CompleteHttpChallengeAsync(string authorizationUrl)
        {
            var authResponse = await SignedPostAsync(authorizationUrl, string.Empty, false).ConfigureAwait(false);
            EnsureSuccess(authResponse);
            var auth = JObject.Parse(await authResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            var challenge = auth["challenges"].Children<JObject>().FirstOrDefault(x => string.Equals((string)x["type"], "http-01", StringComparison.Ordinal));
            if (challenge == null)
                throw new InvalidOperationException("ACME server did not offer HTTP-01.");
            var token = (string)challenge["token"];
            var challengeUrl = (string)challenge["url"];
            var keyAuth = token + "." + JwkThumbprint();
            _challenges[token] = keyAuth;
            var trigger = await SignedPostAsync(challengeUrl, "{}", false).ConfigureAwait(false);
            EnsureSuccess(trigger);
            for (var i = 0; i < 60; i++)
            {
                await Task.Delay(2000).ConfigureAwait(false);
                var poll = await SignedPostAsync(authorizationUrl, string.Empty, false).ConfigureAwait(false);
                EnsureSuccess(poll);
                var current = JObject.Parse(await poll.Content.ReadAsStringAsync().ConfigureAwait(false));
                var status = (string)current["status"];
                if (status == "valid")
                {
                    _challenges.TryRemove(token, out _);
                    return;
                }
                if (status == "invalid")
                    throw new InvalidOperationException("HTTP-01 validation failed: " + current.ToString(Formatting.None));
            }
            throw new TimeoutException("HTTP-01 validation timed out.");
        }

        private async Task<JObject> PollOrderAsync(string orderUrl)
        {
            for (var i = 0; i < 60; i++)
            {
                var response = await SignedPostAsync(orderUrl, string.Empty, false).ConfigureAwait(false);
                EnsureSuccess(response);
                var order = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                var status = (string)order["status"];
                if (status == "valid")
                    return order;
                if (status == "invalid")
                    throw new InvalidOperationException("ACME order failed: " + order.ToString(Formatting.None));
                await Task.Delay(2000).ConfigureAwait(false);
            }
            throw new TimeoutException("ACME order finalization timed out.");
        }

        private async Task<HttpResponseMessage> SignedPostAsync(string url, string payloadJson, bool useJwk, bool retryBadNonce = true)
        {
            if (string.IsNullOrWhiteSpace(_nonce))
                await GetNonceAsync().ConfigureAwait(false);
            var protectedHeader = new JObject
            {
                ["alg"] = "RS256",
                ["nonce"] = _nonce,
                ["url"] = url
            };
            if (useJwk)
                protectedHeader["jwk"] = _jwk;
            else
                protectedHeader["kid"] = _kid;
            var protectedEncoded = CryptoService.Base64Url(Encoding.UTF8.GetBytes(protectedHeader.ToString(Formatting.None)));
            var payloadEncoded = CryptoService.Base64Url(Encoding.UTF8.GetBytes(payloadJson ?? string.Empty));
            var signingInput = Encoding.ASCII.GetBytes(protectedEncoded + "." + payloadEncoded);
            var signature = _accountKey.SignData(signingInput, CryptoConfig.MapNameToOID("SHA256"));
            var body = new JObject
            {
                ["protected"] = protectedEncoded,
                ["payload"] = payloadEncoded,
                ["signature"] = CryptoService.Base64Url(signature)
            }.ToString(Formatting.None);
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
            request.Content = content;
            var response = await _http.SendAsync(request).ConfigureAwait(false);
            UpdateNonce(response);
            if (retryBadNonce && response.StatusCode == HttpStatusCode.BadRequest)
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (text.IndexOf("badNonce", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    response.Dispose();
                    _nonce = null;
                    return await SignedPostAsync(url, payloadJson, useJwk, false).ConfigureAwait(false);
                }
            }
            return response;
        }

        private async Task GetNonceAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Head, (string)_directory["newNonce"]);
            var response = await _http.SendAsync(request).ConfigureAwait(false);
            UpdateNonce(response);
            if (string.IsNullOrWhiteSpace(_nonce))
                throw new InvalidOperationException("ACME replay nonce is missing.");
        }

        private void UpdateNonce(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Replay-Nonce", out var values))
                _nonce = values.FirstOrDefault();
        }

        private string JwkThumbprint()
        {
            var canonical = "{\"e\":\"" + (string)_jwk["e"] + "\",\"kty\":\"RSA\",\"n\":\"" + (string)_jwk["n"] + "\"}";
            using (var sha = SHA256.Create())
                return CryptoService.Base64Url(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
        }

        private static void EnsureSuccess(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
                return;
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new InvalidOperationException("ACME HTTP " + (int)response.StatusCode + ": " + body);
        }

        private CsrBundle CreateCsr(string identifier, bool isIp)
        {
            var random = new SecureRandom();
            var generator = new RsaKeyPairGenerator();
            generator.Init(new KeyGenerationParameters(random, 2048));
            var pair = generator.GenerateKeyPair();
            var extensions = new X509ExtensionsGenerator();
            var generalName = new GeneralName(isIp ? GeneralName.IPAddress : GeneralName.DnsName, identifier);
            extensions.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(generalName));
            var attributes = new DerSet(new AttributePkcs(PkcsObjectIdentifiers.Pkcs9AtExtensionRequest, new DerSet(extensions.Generate())));
            var subject = X509Name.GetInstance(new DerSequence());
            var factory = new Asn1SignatureFactory("SHA256WITHRSA", pair.Private, random);
            var csr = new Pkcs10CertificationRequest(factory, subject, pair.Public, attributes);
            return new CsrBundle { Csr = csr, KeyPair = pair };
        }

        private X509Certificate2 InstallCertificate(string identifier, string pem, AsymmetricCipherKeyPair keyPair)
        {
            var parser = new X509CertificateParser();
            var certs = new List<Org.BouncyCastle.X509.X509Certificate>();
            var blocks = ExtractPemCertificates(pem);
            foreach (var block in blocks)
                certs.Add(parser.ReadCertificate(block));
            if (certs.Count == 0)
                throw new InvalidOperationException("ACME response did not contain a certificate.");
            var entries = certs.Select(x => new X509CertificateEntry(x)).ToArray();
            var store = new Pkcs12StoreBuilder().Build();
            var alias = identifier + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            store.SetKeyEntry(alias, new AsymmetricKeyEntry(keyPair.Private), entries);
            var pfxPassword = _crypto.RandomToken(18);
            byte[] pfx;
            using (var ms = new MemoryStream())
            {
                store.Save(ms, pfxPassword.ToCharArray(), new SecureRandom());
                pfx = ms.ToArray();
            }
            var safe = identifier.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
            var pfxPath = Path.Combine(AppPaths.CertificatesDirectory, safe + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".pfx");
            File.WriteAllBytes(pfxPath, pfx);
            File.WriteAllText(pfxPath + ".pwd.dat", _crypto.ProtectString(pfxPassword));
            var certificate = new X509Certificate2(pfx, pfxPassword, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            using (var machineStore = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                machineStore.Open(OpenFlags.ReadWrite);
                machineStore.Add(certificate);
            }
            return certificate;
        }

        private static IEnumerable<byte[]> ExtractPemCertificates(string pem)
        {
            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            var index = 0;
            while (true)
            {
                var a = pem.IndexOf(begin, index, StringComparison.Ordinal);
                if (a < 0)
                    yield break;
                var b = pem.IndexOf(end, a, StringComparison.Ordinal);
                if (b < 0)
                    yield break;
                var base64 = pem.Substring(a + begin.Length, b - a - begin.Length).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
                yield return Convert.FromBase64String(base64);
                index = b + end.Length;
            }
        }

        private bool HasUsableCertificate(AppSettings settings, string identifier)
        {
            if (string.IsNullOrWhiteSpace(settings.CertificateThumbprint) || !string.Equals(settings.HttpsIdentifier, identifier, StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadOnly);
                    var certs = store.Certificates.Find(X509FindType.FindByThumbprint, settings.CertificateThumbprint, false);
                    if (certs.Count == 0)
                        return false;
                    var minimum = IPAddress.TryParse(identifier, out _) ? DateTime.UtcNow.AddHours(48) : DateTime.UtcNow.AddDays(10);
                    return certs[0].NotAfter.ToUniversalTime() > minimum;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveIdentifier(AppSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.Domain))
                return settings.Domain.Trim();
            if (!string.IsNullOrWhiteSpace(settings.HttpsIdentifier))
                return settings.HttpsIdentifier.Trim();
            if (Uri.TryCreate(settings.PublicBaseUrl, UriKind.Absolute, out var uri))
                return uri.Host;
            return null;
        }

        private sealed class CsrBundle
        {
            public Pkcs10CertificationRequest Csr { get; set; }
            public AsymmetricCipherKeyPair KeyPair { get; set; }
        }
    }

    internal sealed class CertificateIssueResult
    {
        public bool Success { get; private set; }
        public bool Changed { get; private set; }
        public string Thumbprint { get; private set; }
        public string Error { get; private set; }

        public static CertificateIssueResult Ok(string thumbprint, bool changed)
        {
            return new CertificateIssueResult { Success = true, Thumbprint = thumbprint, Changed = changed };
        }

        public static CertificateIssueResult Fail(string error)
        {
            return new CertificateIssueResult { Success = false, Error = error };
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Logging;
using APTOFI.FileSharing.Security;

namespace APTOFI.FileSharing.Network
{
    internal sealed class WebServer : IDisposable
    {
        private readonly Database _db;
        private readonly ApiRouter _router;
        private readonly AntiScannerService _antiScanner;
        private readonly AcmeService _acme;
        private readonly LogService _log;
        private readonly object _gate = new object();
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _loop;

        public WebServer(Database db, ApiRouter router, AntiScannerService antiScanner, AcmeService acme, LogService log)
        {
            _db = db;
            _router = router;
            _antiScanner = antiScanner;
            _acme = acme;
            _log = log;
        }

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                    return _listener != null && _listener.IsListening;
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_listener != null && _listener.IsListening)
                    return;
                var settings = _db.GetSettings();
                if (settings == null)
                    throw new InvalidOperationException("Server is not configured.");
                var host = string.IsNullOrWhiteSpace(settings.BindAddress) || settings.BindAddress == "0.0.0.0" ? "+" : settings.BindAddress;
                _listener = new HttpListener();
                _listener.IgnoreWriteExceptions = true;
                _listener.Prefixes.Add("http://" + host + ":" + settings.HttpPort + "/");
                if (settings.EnableHttps && !string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
                    _listener.Prefixes.Add("https://" + host + ":" + settings.HttpsPort + "/");
                _listener.Start();
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
                _log.App("web-started prefixes=" + string.Join(",", _listener.Prefixes));
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                try { _cts?.Cancel(); } catch { }
                try { _listener?.Stop(); } catch { }
                try { _listener?.Close(); } catch { }
                _listener = null;
                try { _loop?.Wait(1500); } catch { }
                _loop = null;
                _cts?.Dispose();
                _cts = null;
            }
        }

        public void Restart()
        {
            Stop();
            Start();
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context = null;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = ProcessAsync(context);
                }
                catch (HttpListenerException)
                {
                    if (!token.IsCancellationRequested)
                        await Task.Delay(250).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.App("accept-error " + ex);
                    if (!token.IsCancellationRequested)
                        await Task.Delay(250).ConfigureAwait(false);
                }
            }
        }

        private async Task ProcessAsync(HttpListenerContext context)
        {
            var sw = Stopwatch.StartNew();
            var ip = HttpUtil.RemoteIp(context.Request);
            var path = context.Request.Url.AbsolutePath;
            RequestResult result = null;
            try
            {
                ApplySecurityHeaders(context.Response);
                if (path.StartsWith("/.well-known/acme-challenge/", StringComparison.OrdinalIgnoreCase))
                {
                    var token = path.Substring("/.well-known/acme-challenge/".Length);
                    if (_acme.TryGetChallenge(token, out var challenge))
                    {
                        await HttpUtil.WriteTextAsync(context.Response, challenge, "text/plain; charset=utf-8").ConfigureAwait(false);
                        result = new RequestResult { StatusCode = 200, Bytes = challenge.Length };
                    }
                    else
                    {
                        await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                        result = new RequestResult { StatusCode = 404 };
                    }
                    return;
                }
                if (ShouldBlockUnencryptedPublicRequest(context.Request))
                {
                    context.Response.Headers["X-APTOFI-HTTPS-Pending"] = "1";
                    await HttpUtil.WriteTextAsync(context.Response, "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>HTTPS setup required</title><link rel=\"icon\" href=\"/favicon.ico\"></head><body><h1>HTTPS setup required</h1><p>Public access is not enabled until a trusted certificate is installed. Open the local administrator diagnostics or configure VPS/tunnel mode.</p></body></html>", "text/html; charset=utf-8", 503).ConfigureAwait(false);
                    result = new RequestResult { StatusCode = 503 };
                    return;
                }
                if (ShouldRedirectToHttps(context.Request))
                {
                    var settings = _db.GetSettings();
                    var target = BuildCanonicalHttpsBaseUrl(settings) + context.Request.Url.PathAndQuery;
                    context.Response.StatusCode = 308;
                    context.Response.RedirectLocation = target;
                    context.Response.Headers["Cache-Control"] = "no-store";
                    context.Response.ContentLength64 = 0;
                    result = new RequestResult { StatusCode = 308 };
                    return;
                }
                if (context.Request.IsSecureConnection)
                    context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
                if (_antiScanner.IsBanned(ip))
                {
                    await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                    result = new RequestResult { StatusCode = 404 };
                    return;
                }
                result = await _router.HandleAsync(context).ConfigureAwait(false);
                if (result != null && result.UnknownPath)
                    _antiScanner.RecordUnknownPath(ip, path);
            }
            catch (HttpListenerException)
            {
            }
            catch (IOException)
            {
            }
            catch (Exception ex)
            {
                _log.App("request-error path=" + path + " error=" + ex);
                try
                {
                    if (!context.Response.OutputStream.CanWrite)
                        return;
                    if (!context.Response.HeadersSent())
                        await HttpUtil.ErrorAsync(context.Response, 500, "internal_error", "Internal server error.").ConfigureAwait(false);
                }
                catch
                {
                }
                result = new RequestResult { StatusCode = 500 };
            }
            finally
            {
                sw.Stop();
                try { context.Response.OutputStream.Close(); } catch { }
                try { context.Response.Close(); } catch { }
                var status = result?.StatusCode ?? context.Response.StatusCode;
                var bytes = result?.Bytes ?? (context.Response.ContentLength64 > 0 ? context.Response.ContentLength64 : 0);
                if (!IsSuccessfulUploadChunk(context.Request.HttpMethod, path, status))
                    _log.Access(ip, context.Request.HttpMethod, path, status, bytes, sw.ElapsedMilliseconds, result?.UserId);
            }
        }





        private static bool IsSuccessfulUploadChunk(string method, string path, int status)
        {
            return string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(path) &&
                   path.StartsWith("/api/uploads/", StringComparison.OrdinalIgnoreCase) &&
                   path.EndsWith("/chunk", StringComparison.OrdinalIgnoreCase) &&
                   status >= 200 && status < 300;
        }

        private static void ApplySecurityHeaders(HttpListenerResponse response)
        {
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
        }

        private bool ShouldBlockUnencryptedPublicRequest(HttpListenerRequest request)
        {
            if (request.IsSecureConnection || string.Equals(request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase))
                return false;
            var remote = request.RemoteEndPoint?.Address;
            if (IsPrivateOrLoopback(remote))
                return false;
            var settings = _db.GetSettings();
            return settings != null && string.Equals(settings.PublicMode, "Direct", StringComparison.OrdinalIgnoreCase) && settings.EnableHttps && string.IsNullOrWhiteSpace(settings.CertificateThumbprint);
        }

        private static bool IsPrivateOrLoopback(IPAddress address)
        {
            if (address == null || IPAddress.IsLoopback(address))
                return true;
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31 || bytes[0] == 169 && bytes[1] == 254;
            }
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
            return false;
        }

        private static string BuildCanonicalHttpsBaseUrl(AppSettings settings)
        {
            if (settings == null)
                throw new InvalidOperationException("Server settings are unavailable.");
            var host = settings.HttpsIdentifier;
            if (string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(settings.Domain))
                host = settings.Domain;
            if (string.IsNullOrWhiteSpace(host) && Uri.TryCreate(settings.PublicBaseUrl, UriKind.Absolute, out var uri))
                host = uri.Host;
            if (string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("Public HTTPS host is not configured.");
            return new UriBuilder("https", host.Trim().Trim('.'), settings.HttpsPort).Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private bool ShouldRedirectToHttps(HttpListenerRequest request)
        {
            if (request.IsSecureConnection || string.Equals(request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase))
                return false;
            if (IPAddress.IsLoopback(request.LocalEndPoint?.Address ?? IPAddress.None) || IPAddress.IsLoopback(request.RemoteEndPoint?.Address ?? IPAddress.None))
                return false;
            var settings = _db.GetSettings();
            return settings != null && settings.EnableHttps && !string.IsNullOrWhiteSpace(settings.CertificateThumbprint) && !string.IsNullOrWhiteSpace(settings.PublicBaseUrl) && settings.PublicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            Stop();
        }
    }

    internal sealed class RequestResult
    {
        public int StatusCode { get; set; }
        public long Bytes { get; set; }
        public string UserId { get; set; }
        public bool UnknownPath { get; set; }
    }

    internal static class HttpListenerResponseExtensions
    {
        public static bool HeadersSent(this HttpListenerResponse response)
        {
            try
            {
                var value = response.StatusCode;
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}

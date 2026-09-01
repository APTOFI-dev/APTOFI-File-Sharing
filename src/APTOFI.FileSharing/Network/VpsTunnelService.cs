using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Logging;
using APTOFI.FileSharing.Security;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace APTOFI.FileSharing.Network
{
    internal sealed class VpsTunnelService : IDisposable
    {
        private readonly Database _db;
        private readonly CryptoService _crypto;
        private readonly LogService _log;
        private readonly object _gate = new object();
        private SshClient _client;
        private ForwardedPortRemote _forward;
        private CancellationTokenSource _cts;
        private Task _loop;
        private VpsRuntimeStatus _status = new VpsRuntimeStatus();

        public VpsTunnelService(Database db, CryptoService crypto, LogService log)
        {
            _db = db;
            _crypto = crypto;
            _log = log;
        }

        public VpsRuntimeStatus Status
        {
            get
            {
                lock (_gate)
                {
                    return new VpsRuntimeStatus
                    {
                        Connected = _status.Connected,
                        Host = _status.Host,
                        RemotePort = _status.RemotePort,
                        LastConnectedUtc = _status.LastConnectedUtc,
                        LastError = _status.LastError,
                        RetrySeconds = _status.RetrySeconds
                    };
                }
            }
        }

        public void Start()
        {
            Stop();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            Disconnect();
            try { _loop?.Wait(1500); } catch { }
            _cts?.Dispose();
            _cts = null;
            _loop = null;
        }

        public async Task<VpsSetupResult> TestAsync(AppSettings settings)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var client = CreateClient(settings))
                    {
                        client.Connect();
                        var command = client.RunCommand("uname -a && command -v nginx || true && command -v certbot || true");
                        var ok = client.IsConnected && command.ExitStatus == 0;
                        client.Disconnect();
                        return ok ? VpsSetupResult.Ok(command.Result) : VpsSetupResult.Fail(command.Error);
                    }
                }
                catch (Exception ex)
                {
                    return VpsSetupResult.Fail(ex.Message);
                }
            }).ConfigureAwait(false);
        }

        public async Task<VpsSetupResult> ConfigureRemoteAsync(AppSettings settings)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var client = CreateClient(settings))
                    {
                        client.Connect();
                        var email = settings.AcmeEmail;
                        if (string.IsNullOrWhiteSpace(email))
                            email = _db.Users.FindOne(x => x.Role == "admin")?.Email;
                        if (string.IsNullOrWhiteSpace(email))
                            throw new InvalidOperationException("Administrator email is required for VPS certificate issuance.");
                        var publicHost = !string.IsNullOrWhiteSpace(settings.VpsDomain) ? settings.VpsDomain.Trim() : settings.VpsHost.Trim();
                        var install = ExecutePrivileged(client, settings, "export DEBIAN_FRONTEND=noninteractive; apt-get update; apt-get install -y nginx python3 python3-venv ca-certificates; python3 -m venv /opt/afsharing-certbot; /opt/afsharing-certbot/bin/pip install --disable-pip-version-check --upgrade 'certbot>=5.4.0'; mkdir -p /var/www/html/.well-known/acme-challenge");
                        if (!install.Success)
                            return VpsSetupResult.Fail("VPS package setup failed: " + install.Output);
                        var httpConfig = BuildNginxHttpConfig(publicHost, settings.VpsRemotePort);
                        var writeHttp = WriteRemoteFile(client, settings, "/etc/nginx/sites-available/afsharing", httpConfig);
                        if (!writeHttp.Success)
                            return VpsSetupResult.Fail(writeHttp.Output);
                        var enable = ExecutePrivileged(client, settings, "ln -sf /etc/nginx/sites-available/afsharing /etc/nginx/sites-enabled/afsharing; rm -f /etc/nginx/sites-enabled/default; nginx -t; systemctl enable nginx; systemctl reload nginx; if command -v ufw >/dev/null 2>&1; then ufw allow 80/tcp || true; ufw allow 443/tcp || true; fi");
                        if (!enable.Success)
                            return VpsSetupResult.Fail("Nginx setup failed: " + enable.Output);
                        var isIp = IPAddress.TryParse(publicHost, out _);
                        var certbot = "/opt/afsharing-certbot/bin/certbot";
                        var issueCommand = isIp
                            ? certbot + " certonly --non-interactive --agree-tos -m " + ShellQuote(email) + " --preferred-profile shortlived --webroot --webroot-path /var/www/html --ip-address " + ShellQuote(publicHost)
                            : certbot + " certonly --non-interactive --agree-tos -m " + ShellQuote(email) + " --preferred-profile tlsserver --webroot --webroot-path /var/www/html -d " + ShellQuote(publicHost);
                        var issue = ExecutePrivileged(client, settings, issueCommand);
                        if (!issue.Success)
                            return VpsSetupResult.Fail("VPS certificate issuance failed: " + issue.Output);
                        var httpsConfig = BuildNginxHttpsConfig(publicHost, settings.VpsRemotePort);
                        var writeHttps = WriteRemoteFile(client, settings, "/etc/nginx/sites-available/afsharing", httpsConfig);
                        if (!writeHttps.Success)
                            return VpsSetupResult.Fail(writeHttps.Output);
                        RemoteCommandResult timerResult;
                        if (isIp)
                        {
                            var serviceText = "[Unit]\nDescription=APTOFI File Sharing certificate renewal\nAfter=network-online.target\n\n[Service]\nType=oneshot\nExecStart=/opt/afsharing-certbot/bin/certbot renew -q --preferred-profile shortlived --deploy-hook \"systemctl reload nginx\"\n";
                            var timerText = "[Unit]\nDescription=APTOFI File Sharing certificate renewal timer\n\n[Timer]\nOnBootSec=15min\nOnUnitActiveSec=12h\nPersistent=true\n\n[Install]\nWantedBy=timers.target\n";
                            var writeService = WriteRemoteFile(client, settings, "/etc/systemd/system/afsharing-certbot.service", serviceText);
                            if (!writeService.Success)
                                return VpsSetupResult.Fail("VPS certificate renewal service setup failed: " + writeService.Output);
                            var writeTimer = WriteRemoteFile(client, settings, "/etc/systemd/system/afsharing-certbot.timer", timerText);
                            if (!writeTimer.Success)
                                return VpsSetupResult.Fail("VPS certificate renewal timer setup failed: " + writeTimer.Output);
                            timerResult = ExecutePrivileged(client, settings, "systemctl daemon-reload; systemctl enable --now afsharing-certbot.timer");
                        }
                        else
                        {
                            timerResult = ExecutePrivileged(client, settings, "systemctl enable certbot.timer >/dev/null 2>&1 || true; systemctl start certbot.timer >/dev/null 2>&1 || true");
                        }
                        if (!timerResult.Success)
                            return VpsSetupResult.Fail("VPS certificate renewal setup failed: " + timerResult.Output);
                        var reload = ExecutePrivileged(client, settings, "nginx -t; systemctl reload nginx");
                        if (!reload.Success)
                            return VpsSetupResult.Fail("HTTPS reverse proxy setup failed: " + reload.Output);
                        settings.PublicBaseUrl = "https://" + publicHost;
                        settings.PublicMode = "Vps";
                        settings.LastVpsError = null;
                        _db.SaveSettings(settings);
                        client.Disconnect();
                        return VpsSetupResult.Ok("VPS reverse proxy and certificate are configured. Public URL: " + settings.PublicBaseUrl);
                    }
                }
                catch (Exception ex)
                {
                    settings.LastVpsError = ex.Message;
                    _db.SaveSettings(settings);
                    _log.App("vps-setup-error " + ex);
                    return VpsSetupResult.Fail(ex.Message);
                }
            }).ConfigureAwait(false);
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            var delays = new[] { 1, 2, 5, 10, 30 };
            var attempt = 0;
            while (!token.IsCancellationRequested)
            {
                var settings = _db.GetSettings();
                if (settings == null || !string.Equals(settings.PublicMode, "Vps", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(settings.VpsHost))
                {
                    SetStatus(false, settings?.VpsHost, settings?.VpsRemotePort ?? 0, null, 0);
                    await DelaySafe(5000, token).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    Connect(settings);
                    attempt = 0;
                    SetStatus(true, settings.VpsHost, settings.VpsRemotePort, null, 0);
                    _log.App("vps-tunnel-connected host=" + settings.VpsHost + " remotePort=" + settings.VpsRemotePort);
                    while (!token.IsCancellationRequested && IsConnected())
                        await DelaySafe(2000, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                        throw new SshConnectionException("SSH tunnel disconnected.");
                }
                catch (Exception ex)
                {
                    Disconnect();
                    var seconds = delays[Math.Min(attempt, delays.Length - 1)];
                    attempt++;
                    SetStatus(false, settings.VpsHost, settings.VpsRemotePort, ex.Message, seconds);
                    settings.LastVpsError = ex.Message;
                    _db.SaveSettings(settings);
                    _log.App("vps-tunnel-error host=" + settings.VpsHost + " retry=" + seconds + " error=" + ex.Message);
                    await DelaySafe(seconds * 1000, token).ConfigureAwait(false);
                }
            }
        }

        private void Connect(AppSettings settings)
        {
            Disconnect();
            _client = CreateClient(settings);
            _client.KeepAliveInterval = TimeSpan.FromSeconds(15);
            _client.Connect();
            _forward = new ForwardedPortRemote("127.0.0.1", settings.VpsRemotePort, "127.0.0.1", (uint)settings.HttpPort);
            _client.AddForwardedPort(_forward);
            _forward.Start();
        }

        private bool IsConnected()
        {
            try
            {
                return _client != null && _client.IsConnected && _forward != null && _forward.IsStarted;
            }
            catch
            {
                return false;
            }
        }

        private void Disconnect()
        {
            lock (_gate)
            {
                try { if (_forward != null && _forward.IsStarted) _forward.Stop(); } catch { }
                try { if (_client != null && _client.IsConnected) _client.Disconnect(); } catch { }
                try { _forward?.Dispose(); } catch { }
                try { _client?.Dispose(); } catch { }
                _forward = null;
                _client = null;
                _status.Connected = false;
            }
        }

        private SshClient CreateClient(AppSettings settings)
        {
            SshClient client;
            if (!string.IsNullOrWhiteSpace(settings.VpsPrivateKeyPath))
            {
                var passphrase = _crypto.UnprotectString(settings.VpsPrivateKeyPassphraseProtected);
                var key = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(settings.VpsPrivateKeyPath) : new PrivateKeyFile(settings.VpsPrivateKeyPath, passphrase);
                client = new SshClient(settings.VpsHost, settings.VpsPort, settings.VpsUser, key);
            }
            else
            {
                var password = _crypto.UnprotectString(settings.VpsPasswordProtected);
                client = new SshClient(settings.VpsHost, settings.VpsPort, settings.VpsUser, password ?? string.Empty);
            }
            client.HostKeyReceived += (sender, e) =>
            {
                var fingerprint = e.FingerPrintSHA256;
                if (string.IsNullOrWhiteSpace(settings.VpsHostKeyFingerprint))
                {
                    settings.VpsHostKeyFingerprint = fingerprint;
                    _db.SaveSettings(settings);
                    _log.Security("vps-host-key-trusted host=" + settings.VpsHost + " fingerprint=SHA256:" + fingerprint);
                    e.CanTrust = true;
                    return;
                }
                e.CanTrust = string.Equals(settings.VpsHostKeyFingerprint, fingerprint, StringComparison.Ordinal);
                if (!e.CanTrust)
                    _log.Security("vps-host-key-mismatch host=" + settings.VpsHost + " expected=SHA256:" + settings.VpsHostKeyFingerprint + " received=SHA256:" + fingerprint);
            };
            return client;
        }

        private RemoteCommandResult WriteRemoteFile(SshClient client, AppSettings settings, string path, string content)
        {
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            return ExecutePrivileged(client, settings, "printf %s " + ShellQuote(base64) + " | base64 -d > " + ShellQuote(path));
        }

        private RemoteCommandResult ExecutePrivileged(SshClient client, AppSettings settings, string command)
        {
            string wrapped;
            if (string.Equals(settings.VpsUser, "root", StringComparison.OrdinalIgnoreCase) || !settings.VpsUseSudo)
                wrapped = "bash -lc " + ShellQuote(command);
            else
            {
                var password = _crypto.UnprotectString(settings.VpsPasswordProtected) ?? string.Empty;
                wrapped = "printf '%s\\n' " + ShellQuote(password) + " | sudo -S -p '' bash -lc " + ShellQuote(command);
            }
            var result = client.RunCommand(wrapped);
            var output = (result.Result ?? string.Empty) + (result.Error ?? string.Empty);
            return result.ExitStatus == 0 ? RemoteCommandResult.Ok(output) : RemoteCommandResult.Fail(output);
        }

        private static string BuildNginxHttpConfig(string host, uint remotePort)
        {
            return "server {\nlisten 80 default_server;\nlisten [::]:80 default_server;\nserver_name " + host + ";\nclient_max_body_size 0;\nlocation ^~ /.well-known/acme-challenge/ { root /var/www/html; }\nlocation / { proxy_http_version 1.1; proxy_request_buffering off; proxy_buffering off; proxy_read_timeout 86400s; proxy_send_timeout 86400s; proxy_set_header Host $host; proxy_set_header X-Real-IP $remote_addr; proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for; proxy_set_header X-Forwarded-Proto $scheme; proxy_pass http://127.0.0.1:" + remotePort + "; }\n}\n";
        }

        private static string BuildNginxHttpsConfig(string host, uint remotePort)
        {
            var cert = "/etc/letsencrypt/live/" + host + "/fullchain.pem";
            var key = "/etc/letsencrypt/live/" + host + "/privkey.pem";
            return "server {\nlisten 80 default_server;\nlisten [::]:80 default_server;\nserver_name " + host + ";\nlocation ^~ /.well-known/acme-challenge/ { root /var/www/html; }\nlocation / { return 301 https://$host$request_uri; }\n}\nserver {\nlisten 443 ssl default_server;\nlisten [::]:443 ssl default_server;\nserver_name " + host + ";\nssl_certificate " + cert + ";\nssl_certificate_key " + key + ";\nclient_max_body_size 0;\nproxy_request_buffering off;\nproxy_buffering off;\nlocation / { proxy_http_version 1.1; proxy_request_buffering off; proxy_buffering off; proxy_read_timeout 86400s; proxy_send_timeout 86400s; proxy_set_header Host $host; proxy_set_header X-Real-IP $remote_addr; proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for; proxy_set_header X-Forwarded-Proto https; proxy_pass http://127.0.0.1:" + remotePort + "; }\n}\n";
        }

        private static string ShellQuote(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "'\\''") + "'";
        }

        private void SetStatus(bool connected, string host, uint remotePort, string error, int retry)
        {
            lock (_gate)
            {
                _status.Connected = connected;
                _status.Host = host;
                _status.RemotePort = remotePort;
                _status.LastError = error;
                _status.RetrySeconds = retry;
                if (connected)
                    _status.LastConnectedUtc = DateTime.UtcNow;
            }
        }

        private static async Task DelaySafe(int milliseconds, CancellationToken token)
        {
            try { await Task.Delay(milliseconds, token).ConfigureAwait(false); } catch (TaskCanceledException) { }
        }

        public void Dispose()
        {
            Stop();
        }
    }

    internal sealed class VpsRuntimeStatus
    {
        public bool Connected { get; set; }
        public string Host { get; set; }
        public uint RemotePort { get; set; }
        public DateTime? LastConnectedUtc { get; set; }
        public string LastError { get; set; }
        public int RetrySeconds { get; set; }

        public override string ToString()
        {
            return "Connected=" + Connected + "; Host=" + Host + "; RemotePort=" + RemotePort + "; LastError=" + LastError;
        }
    }

    internal sealed class VpsSetupResult
    {
        public bool Success { get; private set; }
        public string Message { get; private set; }

        public static VpsSetupResult Ok(string message)
        {
            return new VpsSetupResult { Success = true, Message = message };
        }

        public static VpsSetupResult Fail(string message)
        {
            return new VpsSetupResult { Success = false, Message = message };
        }
    }

    internal sealed class RemoteCommandResult
    {
        public bool Success { get; private set; }
        public string Output { get; private set; }

        public static RemoteCommandResult Ok(string output)
        {
            return new RemoteCommandResult { Success = true, Output = output ?? string.Empty };
        }

        public static RemoteCommandResult Fail(string output)
        {
            return new RemoteCommandResult { Success = false, Output = output ?? string.Empty };
        }
    }
}

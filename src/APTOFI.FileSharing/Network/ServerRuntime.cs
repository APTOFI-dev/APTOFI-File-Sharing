using System;
using System.Threading;
using System.Threading.Tasks;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Logging;
using APTOFI.FileSharing.Security;
using APTOFI.FileSharing.Storage;

namespace APTOFI.FileSharing.Network
{
    internal sealed class ServerRuntime : IDisposable
    {
        private readonly CryptoService _crypto;
        private readonly Database _db;
        private readonly LogService _log;
        private readonly QuotaService _quota;
        private readonly StorageService _storage;
        private readonly SessionService _sessions;
        private readonly DownloadService _downloads;
        private readonly UploadService _uploads;
        private readonly AcmeService _acme;
        private readonly VpsTunnelService _vps;
        private WebServer _web;
        private CancellationTokenSource _cts;
        private Task _maintenance;

        public ServerRuntime()
        {
            AppPaths.EnsureRuntimeDirectories();
            _crypto = new CryptoService();
            _db = new Database(_crypto);
            _log = new LogService();
            var settings = _db.GetSettings();
            if (settings == null)
                throw new InvalidOperationException("APTOFI File Sharing has not been configured yet.");
            NormalizeDirectPublicUrl(settings);
            _quota = new QuotaService(_db);
            _storage = new StorageService(_db, _crypto, _quota);
            _storage.EnsureStorage();
            _quota.Reconcile();
            _storage.CleanupTrash(TimeSpan.FromDays(30));
            var buffers = new IoBufferPool(Math.Max(64, settings.IoBufferKiB) * 1024);
            var transfers = new TransferGate(Math.Max(1, settings.MaxConcurrentTransfers));
            var thumbnails = new ThumbnailService(_db, _storage);
            var passwords = new PasswordService();
            _sessions = new SessionService(_db, _crypto);
            var antiScanner = new AntiScannerService(_db, _log);
            _downloads = new DownloadService(_db, _storage, buffers, _crypto, transfers);
            _uploads = new UploadService(_db, _storage, _quota, thumbnails, buffers, transfers, _log);
            _acme = new AcmeService(_db, _crypto, new WindowsNetworkService(), _log);
            _vps = new VpsTunnelService(_db, _crypto, _log);
            var diagnostics = new DiagnosticsService(_db, new WindowsNetworkService(), () => _web != null && _web.IsRunning, () => _vps.Status);
            var shares = new ShareService(_db, _crypto, passwords, _storage, _downloads, buffers, transfers);
            var router = new ApiRouter(_db, _crypto, passwords, _sessions, _storage, _uploads, _downloads, shares, antiScanner, diagnostics, new WindowsNetworkService(), _acme, _vps, _log, RestartWebAsync, RestartVps);
            _web = new WebServer(_db, router, antiScanner, _acme, _log);
        }

        private void NormalizeDirectPublicUrl(AppSettings settings)
        {
            if (settings == null || !string.Equals(settings.PublicMode, "Direct", StringComparison.OrdinalIgnoreCase))
                return;
            var identifier = settings.HttpsIdentifier;
            if (string.IsNullOrWhiteSpace(identifier) && Uri.TryCreate(settings.PublicBaseUrl, UriKind.Absolute, out var current))
                identifier = current.Host;
            if (string.IsNullOrWhiteSpace(identifier))
                return;
            var secure = settings.EnableHttps;
            var builder = new UriBuilder(secure ? "https" : "http", identifier, secure ? settings.HttpsPort : settings.HttpPort);
            var normalized = builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            if (!string.Equals(settings.PublicBaseUrl, normalized, StringComparison.OrdinalIgnoreCase))
            {
                settings.PublicBaseUrl = normalized;
                _db.SaveSettings(settings);
            }
        }

        public bool IsRunning => _web != null && _web.IsRunning;

        public void Start()
        {
            _web.Start();
            _vps.Start();
            _cts = new CancellationTokenSource();
            _maintenance = Task.Run(() => MaintenanceLoopAsync(_cts.Token));
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                await SyncDnsAsync().ConfigureAwait(false);
                await EnsureCertificateAndRestartAsync(false).ConfigureAwait(false);
            });
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _maintenance?.Wait(1500); } catch { }
            _maintenance = null;
            _cts?.Dispose();
            _cts = null;
            _vps.Stop();
            _web.Stop();
        }

        private async Task MaintenanceLoopAsync(CancellationToken token)
        {
            var nextTenMinutes = DateTime.UtcNow;
            var nextHour = DateTime.UtcNow;
            var nextCertificate = DateTime.UtcNow.AddHours(12);
            while (!token.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                if (now >= nextTenMinutes)
                {
                    nextTenMinutes = now.AddMinutes(10);
                    try { _storage.CleanupExpired(); } catch (Exception ex) { _log.App("cleanup-expired-error " + ex.Message); }
                    await SyncDnsAsync().ConfigureAwait(false);
                }
                if (now >= nextHour)
                {
                    nextHour = now.AddHours(1);
                    try
                    {
                        var purged = _storage.CleanupTrash(TimeSpan.FromDays(30));
                        if (purged > 0)
                            _log.App("trash-auto-purge items=" + purged);
                    }
                    catch (Exception ex) { _log.App("cleanup-trash-error " + ex.Message); }
                    try { _uploads.CleanupAbandoned(TimeSpan.FromDays(7)); } catch (Exception ex) { _log.App("cleanup-upload-error " + ex.Message); }
                    try { _sessions.Cleanup(); } catch (Exception ex) { _log.App("cleanup-session-error " + ex.Message); }
                    try { _downloads.CleanupTickets(); } catch (Exception ex) { _log.App("cleanup-download-ticket-error " + ex.Message); }
                }
                if (now >= nextCertificate)
                {
                    nextCertificate = now.AddHours(12);
                    await EnsureCertificateAndRestartAsync(false).ConfigureAwait(false);
                }
                try { await Task.Delay(TimeSpan.FromMinutes(1), token).ConfigureAwait(false); } catch (TaskCanceledException) { }
            }
        }

        private async Task SyncDnsAsync()
        {
            try
            {
                var settings = _db.GetSettings();
                if (!DnsUpdateService.IsConfigured(settings))
                    return;
                var result = await _acme.SyncPublicDnsAsync().ConfigureAwait(false);
                if (!result.Success)
                    _log.App("dns-maintenance-error " + result.Error);
            }
            catch (Exception ex)
            {
                _log.App("dns-maintenance-error " + ex);
            }
        }

        private async Task EnsureCertificateAndRestartAsync(bool force)
        {
            try
            {
                var settings = _db.GetSettings();
                if (settings == null || !settings.EnableHttps || string.Equals(settings.PublicMode, "Vps", StringComparison.OrdinalIgnoreCase))
                    return;
                var result = await _acme.EnsureCertificateAsync(force).ConfigureAwait(false);
                if (result.Success && result.Changed)
                    await RestartWebAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.App("certificate-maintenance-error " + ex);
            }
        }

        private Task RestartWebAsync()
        {
            return Task.Run(() => _web.Restart());
        }

        private void RestartVps()
        {
            _vps.Start();
        }

        public void Dispose()
        {
            Stop();
            _vps.Dispose();
            _web.Dispose();
            _log.Dispose();
            _db.Dispose();
        }
    }
}

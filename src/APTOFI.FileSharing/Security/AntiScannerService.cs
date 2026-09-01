using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Logging;

namespace APTOFI.FileSharing.Security
{
    internal sealed class AntiScannerService
    {
        private readonly Database _db;
        private readonly LogService _log;
        private readonly ConcurrentDictionary<string, WindowCounter> _windows = new ConcurrentDictionary<string, WindowCounter>();
        private readonly ConcurrentDictionary<string, WindowCounter> _loginWindows = new ConcurrentDictionary<string, WindowCounter>();

        public AntiScannerService(Database db, LogService log)
        {
            _db = db;
            _log = log;
        }

        public bool IsBanned(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return false;
            var ban = _db.Bans.FindById(ip);
            if (ban == null)
                return false;
            if (ban.Permanent)
                return true;
            if (ban.BanUntilUtc.HasValue && ban.BanUntilUtc.Value > DateTime.UtcNow)
                return true;
            return false;
        }

        public void RecordUnknownPath(string ip, string path)
        {
            if (string.IsNullOrWhiteSpace(ip) || IsExcluded(path))
                return;
            var now = DateTime.UtcNow;
            var counter = _windows.GetOrAdd(ip, _ => new WindowCounter { StartUtc = now });
            lock (counter)
            {
                if ((now - counter.StartUtc).TotalMinutes > 2)
                {
                    counter.StartUtc = now;
                    counter.Count = 0;
                }
                counter.Count += 1;
                if (counter.Count <= 5)
                    return;
                counter.Count = 0;
                counter.StartUtc = now;
            }
            Escalate(ip, path);
        }

        public void RecordFailedLogin(string ip, string path, string email)
        {
            ip = NormalizeIp(ip);
            _log.Security("login-failed ip=" + ip + " path=" + Safe(path) + " email=" + SafeEmail(email));
            if (string.IsNullOrWhiteSpace(ip))
                return;
            var now = DateTime.UtcNow;
            var counter = _loginWindows.GetOrAdd(ip, _ => new WindowCounter { StartUtc = now });
            lock (counter)
            {
                if ((now - counter.StartUtc).TotalMinutes > 5)
                {
                    counter.StartUtc = now;
                    counter.Count = 0;
                }
                counter.Count += 1;
                if (counter.Count < 10)
                    return;
                counter.Count = 0;
                counter.StartUtc = now;
            }
            Escalate(ip, "login-bruteforce");
        }

        public void RecordFailedSharePassword(string ip, string path)
        {
            ip = NormalizeIp(ip);
            _log.Security("share-password-failed ip=" + ip + " path=" + Safe(path));
            if (string.IsNullOrWhiteSpace(ip))
                return;
            var now = DateTime.UtcNow;
            var counter = _loginWindows.GetOrAdd(ip, _ => new WindowCounter { StartUtc = now });
            lock (counter)
            {
                if ((now - counter.StartUtc).TotalMinutes > 5)
                {
                    counter.StartUtc = now;
                    counter.Count = 0;
                }
                counter.Count += 1;
                if (counter.Count < 10)
                    return;
                counter.Count = 0;
                counter.StartUtc = now;
            }
            Escalate(ip, "share-password-bruteforce");
        }

        public IList<BanRecord> GetAll()
        {
            return _db.Bans.FindAll().OrderByDescending(x => x.Permanent).ThenByDescending(x => x.BanUntilUtc).ThenByDescending(x => x.LastSeenUtc).ToList();
        }

        public void Block(string ip, int minutes, string reason)
        {
            ip = NormalizeIp(ip);
            if (!IPAddress.TryParse(ip, out _))
                throw new InvalidOperationException("Invalid IP address.");
            if (minutes != 5 && minutes != 60 && minutes != 1440 && minutes != 0)
                throw new InvalidOperationException("Unsupported block duration.");
            var now = DateTime.UtcNow;
            var ban = _db.Bans.FindById(ip) ?? new BanRecord { Ip = ip, FirstSeenUtc = now };
            ban.Manual = true;
            ban.BlockedUtc = now;
            ban.LastSeenUtc = now;
            ban.Reason = Safe(string.IsNullOrWhiteSpace(reason) ? "Manual administrator block" : reason.Trim());
            ban.LastPath = null;
            if (minutes == 0)
            {
                ban.Permanent = true;
                ban.BanUntilUtc = null;
                ban.Stage = Math.Max(3, ban.Stage);
            }
            else
            {
                ban.Permanent = false;
                ban.BanUntilUtc = now.AddMinutes(minutes);
            }
            _db.Bans.Upsert(ban);
            _windows.TryRemove(ip, out _);
            _loginWindows.TryRemove(ip, out _);
            _log.Security("manual-block ip=" + ip + " minutes=" + minutes + " reason=" + Safe(ban.Reason));
        }

        public void Unblock(string ip)
        {
            ip = NormalizeIp(ip);
            _db.Bans.Delete(ip);
            _windows.TryRemove(ip, out _);
            _loginWindows.TryRemove(ip, out _);
            _log.Security("unblock ip=" + ip);
        }

        public void ResetHistory(string ip)
        {
            ip = NormalizeIp(ip);
            var ban = _db.Bans.FindById(ip);
            if (ban == null)
                return;
            if (ban.Permanent || ban.BanUntilUtc.HasValue && ban.BanUntilUtc.Value > DateTime.UtcNow)
            {
                ban.Stage = 0;
                ban.TotalSuspiciousPaths = 0;
                ban.FirstSeenUtc = DateTime.UtcNow;
                ban.LastSeenUtc = DateTime.UtcNow;
                ban.LastPath = null;
                _db.Bans.Update(ban);
            }
            else
                _db.Bans.Delete(ip);
            _windows.TryRemove(ip, out _);
            _loginWindows.TryRemove(ip, out _);
            _log.Security("reset-history ip=" + ip);
        }

        private void Escalate(string ip, string path)
        {
            var now = DateTime.UtcNow;
            var ban = _db.Bans.FindById(ip) ?? new BanRecord { Ip = ip, FirstSeenUtc = now };
            ban.Manual = false;
            ban.BlockedUtc = now;
            ban.Reason = string.Equals(path, "login-bruteforce", StringComparison.Ordinal) ? "Automatic login brute-force detection" : string.Equals(path, "share-password-bruteforce", StringComparison.Ordinal) ? "Automatic share password brute-force detection" : "Automatic path scanner detection";
            ban.Stage = Math.Min(3, ban.Stage + 1);
            ban.LastSeenUtc = now;
            ban.LastPath = Safe(path);
            ban.TotalSuspiciousPaths += 6;
            if (ban.Stage == 1)
            {
                ban.Permanent = false;
                ban.BanUntilUtc = now.AddMinutes(5);
            }
            else if (ban.Stage == 2)
            {
                ban.Permanent = false;
                ban.BanUntilUtc = now.AddHours(1);
            }
            else
            {
                ban.Permanent = true;
                ban.BanUntilUtc = null;
            }
            _db.Bans.Upsert(ban);
            _log.Security("scanner-ban ip=" + ip + " stage=" + ban.Stage + " permanent=" + ban.Permanent + " path=" + ban.LastPath);
        }

        private static bool IsExcluded(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;
            return path.StartsWith("/d/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/branding/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/.well-known/acme-challenge/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "/favicon.ico", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeIp(string ip)
        {
            return (ip ?? string.Empty).Trim();
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "-";
            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length > 240 ? value.Substring(0, 240) : value;
        }

        private static string SafeEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return "-";
            email = email.Trim().Replace("\r", string.Empty).Replace("\n", string.Empty);
            var at = email.IndexOf('@');
            if (at <= 1)
                return "***";
            return email.Substring(0, 1) + "***" + email.Substring(at);
        }

        private sealed class WindowCounter
        {
            public DateTime StartUtc { get; set; }
            public int Count { get; set; }
        }
    }
}

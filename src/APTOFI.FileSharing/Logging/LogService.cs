// Date: 2026-10-05
// Time: 10:35:00 +07:00
// File version: 1.1.36
// Description: Writes bounded rotating logs and compact per-IP access summaries with administrator-configurable retention and size limits.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using APTOFI.FileSharing.Core;

namespace APTOFI.FileSharing.Logging
{
    internal sealed class LogService : IDisposable
    {
        private const int QueueCapacity = 4096;
        private const int MaxAccessBuckets = 10000;

        private readonly BlockingCollection<LogEntry> _queue = new BlockingCollection<LogEntry>(QueueCapacity);
        private readonly Thread _thread;
        private readonly object _accessSync = new object();
        private readonly Dictionary<string, AccessAggregate> _access = new Dictionary<string, AccessAggregate>(StringComparer.OrdinalIgnoreCase);
        private readonly bool _maintenanceEnabled;
        private readonly int _retentionDays;
        private readonly long _maxFileBytes;
        private readonly long _maxDirectoryBytes;
        private readonly TimeSpan _maintenanceInterval;
        private readonly bool _compactAccess;
        private readonly TimeSpan _accessWindow;
        private readonly string _adminPath;
        private readonly string _userPath;
        private DateTime _nextMaintenanceUtc;
        private volatile bool _disposed;

        public LogService()
            : this(null, false)
        {
        }

        public LogService(AppSettings settings)
            : this(settings, true)
        {
        }

        private LogService(AppSettings settings, bool maintenanceEnabled)
        {
            AppPaths.EnsureRuntimeDirectories();
            _maintenanceEnabled = maintenanceEnabled;
            _retentionDays = Clamp(settings?.LogRetentionDays ?? 7, 1, 3650);
            _maxFileBytes = (long)Clamp(settings?.LogMaxFileMiB ?? 20, 1, 1024) * 1024L * 1024L;
            _maxDirectoryBytes = (long)Clamp(settings?.LogMaxDirectoryMiB ?? 150, 10, 102400) * 1024L * 1024L;
            _maintenanceInterval = TimeSpan.FromMinutes(Clamp(settings?.LogMaintenanceIntervalMinutes ?? 60, 5, 1440));
            _compactAccess = !string.Equals(settings?.AccessLogMode, "Full", StringComparison.OrdinalIgnoreCase);
            _accessWindow = TimeSpan.FromMinutes(Clamp(settings?.AccessLogAggregateMinutes ?? 30, 1, 1440));
            _adminPath = NormalizeSecretPath(settings?.AdminPath);
            _userPath = NormalizeSecretPath(settings?.UserPath);

            if (_maintenanceEnabled)
            {
                MaintainLogs();
                _nextMaintenanceUtc = DateTime.UtcNow.Add(_maintenanceInterval);
            }
            else
            {
                _nextMaintenanceUtc = DateTime.MaxValue;
            }

            _thread = new Thread(WriterLoop) { IsBackground = true, Name = "AFSharingLogWriter" };
            _thread.Start();
        }

        public void App(string message)
        {
            Enqueue("app", message);
        }

        public void Security(string message)
        {
            Enqueue("security", message);
        }

        public void Access(string ip, string method, string path, int status, long bytes, long elapsedMs, string userId)
        {
            var sanitized = SanitizePath(path);
            if (!_compactAccess)
            {
                Enqueue("access", FormatFullAccess(ip, method, sanitized, status, bytes, elapsedMs, userId));
                return;
            }

            var now = DateTime.UtcNow;
            LogEntry completed = null;
            lock (_accessSync)
            {
                var key = string.IsNullOrWhiteSpace(ip) ? "-" : ip.Trim();
                if (_access.TryGetValue(key, out var aggregate) && now - aggregate.LastUtc >= _accessWindow)
                {
                    completed = CreateAccessSummary(aggregate);
                    _access.Remove(key);
                    aggregate = null;
                }

                if (aggregate == null)
                {
                    aggregate = new AccessAggregate { Ip = key, StartUtc = now, LastUtc = now };
                    _access[key] = aggregate;
                }

                AddAccess(aggregate, method, sanitized, status, bytes, elapsedMs, userId, now);

                if (_access.Count > MaxAccessBuckets)
                {
                    var oldest = _access.Values.OrderBy(x => x.LastUtc).Take(Math.Max(1, _access.Count - MaxAccessBuckets + 256)).ToList();
                    foreach (var item in oldest)
                    {
                        _access.Remove(item.Ip);
                        EnqueueEntry(CreateAccessSummary(item));
                    }
                }
            }

            if (completed != null)
                EnqueueEntry(completed);
        }

        public IList<RecentLogEntry> ReadRecent(int maxLines)
        {
            maxLines = Math.Max(20, Math.Min(500, maxLines));
            var result = new List<RecentLogEntry>();
            foreach (var channel in new[] { "security", "app" })
            {
                foreach (var file in Directory.GetFiles(AppPaths.LogsDirectory, channel + "-*.log", SearchOption.TopDirectoryOnly).OrderByDescending(x => x))
                {
                    string[] lines;
                    try
                    {
                        lines = File.ReadAllLines(file);
                    }
                    catch
                    {
                        continue;
                    }
                    for (var i = lines.Length - 1; i >= 0 && result.Count < maxLines * 2; i--)
                    {
                        var line = lines[i];
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                        var when = DateTime.MinValue;
                        var message = line;
                        if (line.Length > 23 && DateTime.TryParseExact(line.Substring(0, 23), "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                        {
                            when = parsed;
                            message = line.Substring(23).Trim();
                        }
                        result.Add(new RecentLogEntry { Time = when, Channel = channel, Message = message });
                    }
                    if (result.Count >= maxLines * 2)
                        break;
                }
            }
            return result.OrderByDescending(x => x.Time).Take(maxLines).ToList();
        }

        private void Enqueue(string channel, string message)
        {
            EnqueueEntry(new LogEntry { Channel = channel, TimeUtc = DateTime.UtcNow, Message = SanitizeMessage(message) });
        }

        private void EnqueueEntry(LogEntry entry)
        {
            if (_disposed || entry == null)
                return;
            _queue.TryAdd(entry);
        }

        private void WriterLoop()
        {
            while (!_queue.IsCompleted)
            {
                try
                {
                    if (_queue.TryTake(out var entry, 30000))
                        WriteEntry(entry);
                }
                catch
                {
                }

                try
                {
                    FlushAccessAggregates(false);
                }
                catch
                {
                }

                if (_maintenanceEnabled && DateTime.UtcNow >= _nextMaintenanceUtc)
                {
                    MaintainLogs();
                    _nextMaintenanceUtc = DateTime.UtcNow.Add(_maintenanceInterval);
                }
            }

            while (_queue.TryTake(out var remaining))
            {
                try { WriteEntry(remaining); } catch { }
            }
        }

        private void WriteEntry(LogEntry entry)
        {
            var local = entry.TimeUtc.ToLocalTime();
            var line = local.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + entry.Message + Environment.NewLine;
            var file = ResolveOutputFile(entry.Channel, local, Encoding.UTF8.GetByteCount(line));
            File.AppendAllText(file, line);
        }

        private string ResolveOutputFile(string channel, DateTime local, long estimatedBytes)
        {
            var stem = channel + "-" + local.ToString("yyyy-MM-dd");
            var primary = Path.Combine(AppPaths.LogsDirectory, stem + ".log");
            if (!_maintenanceEnabled || !WouldExceed(primary, estimatedBytes))
                return primary;

            for (var i = 1; i < 10000; i++)
            {
                var candidate = Path.Combine(AppPaths.LogsDirectory, stem + "." + i.ToString("000", CultureInfo.InvariantCulture) + ".log");
                if (!WouldExceed(candidate, estimatedBytes))
                    return candidate;
            }
            return primary;
        }

        private bool WouldExceed(string path, long additionalBytes)
        {
            try
            {
                if (!File.Exists(path))
                    return false;
                return new FileInfo(path).Length + Math.Max(0, additionalBytes) > _maxFileBytes;
            }
            catch
            {
                return false;
            }
        }

        private void FlushAccessAggregates(bool all)
        {
            if (!_compactAccess)
                return;

            List<AccessAggregate> ready;
            var now = DateTime.UtcNow;
            lock (_accessSync)
            {
                ready = _access.Values.Where(x => all || now - x.LastUtc >= _accessWindow).ToList();
                foreach (var aggregate in ready)
                    _access.Remove(aggregate.Ip);
            }

            foreach (var aggregate in ready)
                EnqueueEntry(CreateAccessSummary(aggregate));
        }

        private static void AddAccess(AccessAggregate aggregate, string method, string path, int status, long bytes, long elapsedMs, string userId, DateTime now)
        {
            aggregate.Count++;
            aggregate.LastUtc = now;
            aggregate.Bytes += Math.Max(0, bytes);
            aggregate.ElapsedTotalMs += Math.Max(0, elapsedMs);
            aggregate.ElapsedMaxMs = Math.Max(aggregate.ElapsedMaxMs, Math.Max(0, elapsedMs));
            if (!string.IsNullOrWhiteSpace(method))
                aggregate.Methods.Add(method.Trim().ToUpperInvariant());
            if (status >= 500) aggregate.Status5xx++;
            else if (status >= 400) aggregate.Status4xx++;
            else if (status >= 300) aggregate.Status3xx++;
            else if (status >= 200) aggregate.Status2xx++;
            else aggregate.StatusOther++;
            if (aggregate.FirstPath == null)
                aggregate.FirstPath = path;
            aggregate.LastPath = path;
            if (aggregate.RepresentativePath == null || IsStaticPath(aggregate.RepresentativePath))
            {
                if (!IsStaticPath(path) || aggregate.RepresentativePath == null)
                    aggregate.RepresentativePath = path;
            }
            var normalizedUser = string.IsNullOrWhiteSpace(userId) ? "-" : userId.Trim();
            if (aggregate.User == null)
                aggregate.User = normalizedUser;
            else if (!string.Equals(aggregate.User, normalizedUser, StringComparison.Ordinal))
                aggregate.User = "multiple";
        }

        private static LogEntry CreateAccessSummary(AccessAggregate aggregate)
        {
            var methods = aggregate.Methods.Count == 0 ? "-" : string.Join(",", aggregate.Methods.OrderBy(x => x));
            var statuses = string.Format(CultureInfo.InvariantCulture, "2xx:{0},3xx:{1},4xx:{2},5xx:{3},other:{4}", aggregate.Status2xx, aggregate.Status3xx, aggregate.Status4xx, aggregate.Status5xx, aggregate.StatusOther);
            var message = string.Format(CultureInfo.InvariantCulture,
                "ip={0} requests={1} methods={2} statuses={3} bytes={4} elapsedMsTotal={5} elapsedMsMax={6} path={7} user={8} windowSec={9}",
                aggregate.Ip,
                aggregate.Count,
                methods,
                statuses,
                aggregate.Bytes,
                aggregate.ElapsedTotalMs,
                aggregate.ElapsedMaxMs,
                aggregate.RepresentativePath ?? aggregate.LastPath ?? aggregate.FirstPath ?? "/",
                aggregate.User ?? "-",
                Math.Max(1, (long)(aggregate.LastUtc - aggregate.StartUtc).TotalSeconds));
            return new LogEntry { Channel = "access", TimeUtc = aggregate.LastUtc, Message = SanitizeMessage(message) };
        }

        private static string FormatFullAccess(string ip, string method, string path, int status, long bytes, long elapsedMs, string userId)
        {
            return string.Format(CultureInfo.InvariantCulture, "ip={0} method={1} path={2} status={3} bytes={4} elapsedMs={5} user={6}", ip ?? "-", method ?? "-", path, status, bytes, elapsedMs, userId ?? "-");
        }

        private void MaintainLogs()
        {
            try
            {
                AppPaths.EnsureRuntimeDirectories();
                var keepFrom = DateTime.Now.Date.AddDays(-(_retentionDays - 1));
                foreach (var file in Directory.GetFiles(AppPaths.LogsDirectory, "*.log", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTime < keepFrom)
                            info.Delete();
                    }
                    catch
                    {
                    }
                }

                EnforceDirectoryLimit();
            }
            catch
            {
            }
        }

        private void EnforceDirectoryLimit()
        {
            try
            {
                var files = new DirectoryInfo(AppPaths.LogsDirectory)
                    .GetFiles("*.log", SearchOption.TopDirectoryOnly)
                    .OrderBy(x => x.LastWriteTimeUtc)
                    .ToList();
                long total = files.Sum(x => SafeLength(x));
                if (total <= _maxDirectoryBytes)
                    return;

                foreach (var file in files)
                {
                    if (total <= _maxDirectoryBytes)
                        break;
                    var length = SafeLength(file);
                    try
                    {
                        file.Delete();
                        total -= length;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static long SafeLength(FileInfo info)
        {
            try { return info.Length; } catch { return 0; }
        }

        private static bool IsStaticPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            return path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(path, "/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(path, "/branding/logo", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(path, "/branding/favicon", StringComparison.OrdinalIgnoreCase);
        }

        private string SanitizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "/";
            if (MatchesSecretPath(path, _adminPath))
                return "/[admin]" + path.Substring(_adminPath.Length);
            if (MatchesSecretPath(path, _userPath))
                return "/[user]" + path.Substring(_userPath.Length);
            if (path.StartsWith("/d/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = path.Split('/');
                if (parts.Length > 2 && !string.IsNullOrEmpty(parts[2]))
                    parts[2] = "[share]";
                return string.Join("/", parts);
            }
            return path.Length > 512 ? path.Substring(0, 512) : path;
        }


        private static bool MatchesSecretPath(string path, string secretPath)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(secretPath))
                return false;
            if (!path.StartsWith(secretPath, StringComparison.OrdinalIgnoreCase))
                return false;
            return path.Length == secretPath.Length || path[secretPath.Length] == '/';
        }

        private static string NormalizeSecretPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var value = path.Trim();
            if (!value.StartsWith("/", StringComparison.Ordinal))
                value = "/" + value;
            return value.Length > 1 ? value.TrimEnd('/') : value;
        }

        private static string SanitizeMessage(string message)
        {
            message = message ?? string.Empty;
            var lineBreak = message.IndexOfAny(new[] { '\r', '\n' });
            if (lineBreak >= 0)
                message = message.Substring(0, lineBreak);
            message = message.Trim();
            return message.Length > 2048 ? message.Substring(0, 2048) : message;
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            try { FlushAccessAggregates(true); } catch { }
            _disposed = true;
            _queue.CompleteAdding();
            if (_thread.IsAlive)
                _thread.Join(2000);
            _queue.Dispose();
        }

        internal sealed class RecentLogEntry
        {
            public DateTime Time { get; set; }
            public string Channel { get; set; }
            public string Message { get; set; }
        }

        private sealed class LogEntry
        {
            public string Channel { get; set; }
            public DateTime TimeUtc { get; set; }
            public string Message { get; set; }
        }

        private sealed class AccessAggregate
        {
            public string Ip { get; set; }
            public DateTime StartUtc { get; set; }
            public DateTime LastUtc { get; set; }
            public long Count { get; set; }
            public long Bytes { get; set; }
            public long ElapsedTotalMs { get; set; }
            public long ElapsedMaxMs { get; set; }
            public long Status2xx { get; set; }
            public long Status3xx { get; set; }
            public long Status4xx { get; set; }
            public long Status5xx { get; set; }
            public long StatusOther { get; set; }
            public string FirstPath { get; set; }
            public string LastPath { get; set; }
            public string RepresentativePath { get; set; }
            public string User { get; set; }
            public HashSet<string> Methods { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

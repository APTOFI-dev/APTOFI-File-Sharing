using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using APTOFI.FileSharing.Core;

namespace APTOFI.FileSharing.Logging
{
    internal sealed class LogService : IDisposable
    {
        private readonly BlockingCollection<LogEntry> _queue = new BlockingCollection<LogEntry>(4096);
        private readonly Thread _thread;
        private volatile bool _disposed;

        public LogService()
        {
            AppPaths.EnsureRuntimeDirectories();
            Rotate();
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
            Enqueue("access", string.Format(CultureInfo.InvariantCulture, "ip={0} method={1} path={2} status={3} bytes={4} elapsedMs={5} user={6}", ip ?? "-", method ?? "-", sanitized, status, bytes, elapsedMs, userId ?? "-"));
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
            if (_disposed)
                return;
            _queue.TryAdd(new LogEntry { Channel = channel, TimeUtc = DateTime.UtcNow, Message = SanitizeMessage(message) });
        }

        private void WriterLoop()
        {
            foreach (var entry in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var local = entry.TimeUtc.ToLocalTime();
                    var file = Path.Combine(AppPaths.LogsDirectory, entry.Channel + "-" + local.ToString("yyyy-MM-dd") + ".log");
                    var line = local.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + entry.Message + Environment.NewLine;
                    File.AppendAllText(file, line);
                }
                catch
                {
                }
            }
        }

        private static string SanitizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "/";
            if (path.StartsWith("/d/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = path.Split('/');
                if (parts.Length > 2 && !string.IsNullOrEmpty(parts[2]))
                    parts[2] = "[share]";
                return string.Join("/", parts);
            }
            return path.Length > 512 ? path.Substring(0, 512) : path;
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

        private static void Rotate()
        {
            try
            {
                var threshold = DateTime.Now.Date.AddDays(-7);
                foreach (var file in Directory.GetFiles(AppPaths.LogsDirectory, "*.log", SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTime < threshold)
                        info.Delete();
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _queue.CompleteAdding();
            if (_thread.IsAlive)
                _thread.Join(1500);
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
    }
}

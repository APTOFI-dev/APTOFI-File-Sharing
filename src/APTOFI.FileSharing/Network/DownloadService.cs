using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Security;
using APTOFI.FileSharing.Storage;

namespace APTOFI.FileSharing.Network
{
    internal sealed class DownloadService
    {
        private readonly Database _db;
        private readonly StorageService _storage;
        private readonly IoBufferPool _buffers;
        private readonly CryptoService _crypto;
        private readonly TransferGate _transfers;

        public DownloadService(Database db, StorageService storage, IoBufferPool buffers, CryptoService crypto, TransferGate transfers)
        {
            _db = db;
            _storage = storage;
            _buffers = buffers;
            _crypto = crypto;
            _transfers = transfers;
        }

        public async Task<bool> EnsureTicketOrRedirectAsync(HttpListenerContext context, FileRecord file, ShareRecord share)
        {
            var sid = context.Request.QueryString["sid"];
            if (!string.IsNullOrWhiteSpace(sid))
                return true;
            sid = _crypto.RandomToken(18);
            _db.DownloadTickets.Insert(new DownloadTicketRecord
            {
                Id = sid,
                FileId = file.Id,
                ShareId = share?.Id,
                CreatedUtc = DateTime.UtcNow
            });
            var url = context.Request.Url.AbsolutePath + BuildQueryWithSid(context.Request.Url.Query, sid);
            context.Response.StatusCode = 302;
            context.Response.RedirectLocation = url;
            context.Response.ContentLength64 = 0;
            await context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
            return false;
        }

        public async Task<long> SendFileAsync(HttpListenerContext context, FileRecord file, ShareRecord share)
        {
            using (await _transfers.EnterAsync().ConfigureAwait(false))
            {
            var path = _storage.ResolvePhysicalPath(file.PhysicalRelativePath, file.StorageLocationId);
            if (!File.Exists(path))
                throw new FileNotFoundException();
            var length = new FileInfo(path).Length;
            if (length != file.Size)
                throw new IOException("Physical file size does not match metadata.");
            long start = 0;
            long end = length == 0 ? -1 : length - 1;
            var rangeHeader = context.Request.Headers["Range"];
            var partial = false;
            if (!string.IsNullOrWhiteSpace(rangeHeader))
            {
                if (!TryParseRange(rangeHeader, length, out start, out end))
                {
                    context.Response.StatusCode = 416;
                    context.Response.Headers["Content-Range"] = "bytes */" + length.ToString(CultureInfo.InvariantCulture);
                    context.Response.ContentLength64 = 0;
                    return 0;
                }
                partial = true;
            }
            var count = length == 0 ? 0 : end - start + 1;
            context.Response.StatusCode = partial ? 206 : 200;
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers["Accept-Ranges"] = "bytes";
            context.Response.Headers["ETag"] = "\"" + file.Id + "-" + file.Size.ToString(CultureInfo.InvariantCulture) + "-" + file.ModifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\"";
            context.Response.Headers["Content-Disposition"] = HttpUtil.SafeFileNameHeader(file.OriginalName);
            if (partial)
                context.Response.Headers["Content-Range"] = "bytes " + start + "-" + end + "/" + length;
            context.Response.ContentLength64 = count;
            if (string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase) || count == 0)
                return 0;
            var buffer = _buffers.Rent();
            long sent = 0;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    stream.Position = start;
                    while (sent < count)
                    {
                        var wanted = (int)Math.Min(buffer.Length, count - sent);
                        var read = await stream.ReadAsync(buffer, 0, wanted).ConfigureAwait(false);
                        if (read <= 0)
                            break;
                        await context.Response.OutputStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                        sent += read;
                    }
                }
            }
            finally
            {
                _buffers.Return(buffer);
            }
            if (sent == count && end == length - 1)
                CountTicket(context.Request.QueryString["sid"], file, share);
            return sent;
            }
        }
        public void CleanupTickets()
        {
            var threshold = DateTime.UtcNow.AddDays(-2);
            _db.DownloadTickets.DeleteMany(x => x.CreatedUtc < threshold);
        }

        private void CountTicket(string sid, FileRecord file, ShareRecord share)
        {
            if (string.IsNullOrWhiteSpace(sid))
                return;
            var ticket = _db.DownloadTickets.FindById(sid);
            if (ticket == null || ticket.Counted || ticket.FileId != file.Id)
                return;
            ticket.Counted = true;
            _db.DownloadTickets.Update(ticket);
            var current = _db.Files.FindById(file.Id);
            if (current != null)
            {
                current.DownloadCount++;
                _db.Files.Update(current);
            }
            if (share != null)
            {
                var currentShare = _db.Shares.FindById(share.Id);
                if (currentShare != null)
                {
                    currentShare.DownloadCount++;
                    _db.Shares.Update(currentShare);
                }
            }
        }

        private static bool TryParseRange(string header, long length, out long start, out long end)
        {
            start = 0;
            end = length - 1;
            if (length <= 0 || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(","))
                return false;
            var range = header.Substring(6).Trim();
            var dash = range.IndexOf('-');
            if (dash < 0)
                return false;
            var left = range.Substring(0, dash).Trim();
            var right = range.Substring(dash + 1).Trim();
            if (left.Length == 0)
            {
                if (!long.TryParse(right, out var suffix) || suffix <= 0)
                    return false;
                suffix = Math.Min(suffix, length);
                start = length - suffix;
                end = length - 1;
                return true;
            }
            if (!long.TryParse(left, out start) || start < 0 || start >= length)
                return false;
            if (right.Length == 0)
            {
                end = length - 1;
                return true;
            }
            if (!long.TryParse(right, out end) || end < start)
                return false;
            end = Math.Min(end, length - 1);
            return true;
        }

        private static string BuildQueryWithSid(string query, string sid)
        {
            var q = string.IsNullOrWhiteSpace(query) ? string.Empty : query.TrimStart('?');
            return "?" + (string.IsNullOrWhiteSpace(q) ? string.Empty : q + "&") + "sid=" + Uri.EscapeDataString(sid);
        }
    }
}

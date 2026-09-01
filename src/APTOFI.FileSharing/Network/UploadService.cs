using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading;
using System.Net;
using System.Threading.Tasks;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Logging;
using APTOFI.FileSharing.Storage;

namespace APTOFI.FileSharing.Network
{
    internal sealed class UploadService
    {
        private readonly Database _db;
        private readonly StorageService _storage;
        private readonly QuotaService _quota;
        private readonly ThumbnailService _thumbnails;
        private readonly IoBufferPool _buffers;
        private readonly TransferGate _transfers;
        private readonly LogService _log;
        private readonly object _gate = new object();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _uploadLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        public UploadService(Database db, StorageService storage, QuotaService quota, ThumbnailService thumbnails, IoBufferPool buffers, TransferGate transfers, LogService log)
        {
            _db = db;
            _storage = storage;
            _quota = quota;
            _thumbnails = thumbnails;
            _buffers = buffers;
            _transfers = transfers;
            _log = log;
        }

        public UploadRecord Start(UserRecord user, string parentFolderId, string name, long size, DateTime? expiresUtc, string resumeKey)
        {
            lock (_gate)
            {
                var effectiveOwner = user.Id;
                var normalizedParent = string.IsNullOrWhiteSpace(parentFolderId) ? null : parentFolderId;
                var cleanName = HttpUtil.RepairLegacyUtf8(Path.GetFileName(name ?? "file"));
                var normalizedResumeKey = string.IsNullOrWhiteSpace(resumeKey) ? null : resumeKey.Trim();
                if (normalizedResumeKey != null)
                {
                    var existing = _db.Uploads.FindOne(x =>
                        x.OwnerId == effectiveOwner &&
                        x.Status == "active" &&
                        x.ResumeKey == normalizedResumeKey);
                    if (existing != null &&
                        existing.ExpectedSize == size &&
                        string.Equals(existing.ParentFolderId ?? string.Empty, normalizedParent ?? string.Empty, StringComparison.Ordinal) &&
                        string.Equals(existing.OriginalName, cleanName, StringComparison.Ordinal))
                    {
                        existing.LastActivityUtc = DateTime.UtcNow;
                        _db.Uploads.Update(existing);
                        return existing;
                    }
                }
                if (normalizedParent != null)
                {
                    var parent = _db.Folders.FindById(normalizedParent);
                    if (parent == null || !string.Equals(parent.OwnerId, effectiveOwner, StringComparison.Ordinal))
                        throw new DirectoryNotFoundException();
                }
                var owner = _db.Users.FindById(effectiveOwner);
                var check = _quota.CheckAndReserve(owner, size);
                if (!check.Allowed)
                    throw new QuotaException(check.Error, check.AvailableBytes);
                var storageLocationId = check.StorageLocationId;
                var relative = _storage.AllocatePhysicalRelativePath(storageLocationId);
                var full = _storage.ResolvePhysicalPath(relative, storageLocationId);
                using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                }
                var now = DateTime.UtcNow;
                var record = new UploadRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    OwnerId = effectiveOwner,
                    ParentFolderId = normalizedParent,
                    OriginalName = cleanName,
                    OriginalExtension = Path.GetExtension(cleanName),
                    MimeType = MimeTypes.FromName(cleanName),
                    PhysicalRelativePath = relative,
                    StorageLocationId = storageLocationId,
                    ExpectedSize = size,
                    CurrentOffset = 0,
                    CreatedUtc = now,
                    LastActivityUtc = now,
                    ExpiresUtc = expiresUtc,
                    Status = "active",
                    ResumeKey = normalizedResumeKey
                };
                _db.Uploads.Insert(record);
                return record;
            }
        }

        public UploadRecord Get(UserRecord user, string id)
        {
            var upload = _db.Uploads.FindById(id);
            if (upload == null || upload.OwnerId != user.Id)
                throw new FileNotFoundException();
            return upload;
        }

        public async Task<UploadRecord> WriteAsync(UserRecord user, string id, long requestedOffset, HttpListenerRequest request)
        {
            var uploadLock = _uploadLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
            await uploadLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using (await _transfers.EnterAsync().ConfigureAwait(false))
                {
                    UploadRecord upload;
                    lock (_gate)
                    {
                        upload = Get(user, id);
                        if (upload.Status != "active")
                            throw new InvalidOperationException("Upload is not active.");
                        if (requestedOffset != upload.CurrentOffset)
                            throw new OffsetMismatchException(upload.CurrentOffset);
                    }
                    var path = _storage.ResolvePhysicalPath(upload.PhysicalRelativePath, upload.StorageLocationId);
                    var buffer = _buffers.Rent();
                    long position = requestedOffset;
                    var requestBytes = request.ContentLength64;
                    var fileRemaining = upload.ExpectedSize - requestedOffset;
                    if (requestBytes < 0)
                        requestBytes = fileRemaining;
                    if (requestBytes > fileRemaining)
                        throw new InvalidOperationException("Upload chunk is larger than the remaining file size.");
                    long requestRemaining = requestBytes;
                    try
                    {
                        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            if (file.Length != requestedOffset)
                                file.SetLength(requestedOffset);
                            file.Position = requestedOffset;
                            while (position < upload.ExpectedSize && requestRemaining > 0)
                            {
                                var max = (int)Math.Min(buffer.Length, Math.Min(upload.ExpectedSize - position, requestRemaining));
                                var read = await ReadRequestWithInactivityTimeoutAsync(request, buffer, max, id).ConfigureAwait(false);
                                if (read <= 0)
                                    break;
                                var writeWatch = Stopwatch.StartNew();
                                await file.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                                writeWatch.Stop();
                                if (writeWatch.ElapsedMilliseconds >= 2000)
                                    _log.App("storage-write-stall upload=" + id + " storage=" + (upload.StorageLocationId ?? "primary") + " bytes=" + read + " elapsedMs=" + writeWatch.ElapsedMilliseconds);
                                position += read;
                                requestRemaining -= read;
                            }
                            var flushWatch = Stopwatch.StartNew();
                            await file.FlushAsync().ConfigureAwait(false);
                            flushWatch.Stop();
                            if (flushWatch.ElapsedMilliseconds >= 3000)
                                _log.App("storage-flush-stall upload=" + id + " storage=" + (upload.StorageLocationId ?? "primary") + " elapsedMs=" + flushWatch.ElapsedMilliseconds);
                        }
                    }
                    finally
                    {
                        _buffers.Return(buffer);
                        lock (_gate)
                        {
                            var current = _db.Uploads.FindById(id);
                            if (current != null)
                            {
                                current.CurrentOffset = position;
                                current.LastActivityUtc = DateTime.UtcNow;
                                _db.Uploads.Update(current);
                                upload = current;
                            }
                        }
                    }
                    return upload;
                }
            }
            finally
            {
                uploadLock.Release();
            }
        }


        public void Cancel(UserRecord user, string id)
        {
            var uploadLock = _uploadLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
            uploadLock.Wait();
            try
            {
                lock (_gate)
                {
                    var upload = Get(user, id);
                    if (string.Equals(upload.Status, "completed", StringComparison.Ordinal))
                        return;
                    var path = _storage.ResolvePhysicalPath(upload.PhysicalRelativePath, upload.StorageLocationId);
                    _db.Uploads.Delete(upload.Id);
                    if (File.Exists(path))
                        File.Delete(path);
                }
            }
            finally
            {
                uploadLock.Release();
                _uploadLocks.TryRemove(id, out _);
            }
        }
        public void PurgeOwnerUploads(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId))
                return;
            foreach (var candidate in _db.Uploads.Find(x => x.OwnerId == ownerId).ToList())
            {
                var id = candidate.Id;
                var uploadLock = _uploadLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
                uploadLock.Wait();
                try
                {
                    lock (_gate)
                    {
                        var upload = _db.Uploads.FindById(id);
                        if (upload == null || upload.OwnerId != ownerId)
                            continue;
                        if (!string.Equals(upload.Status, "completed", StringComparison.Ordinal))
                        {
                            var path = _storage.ResolvePhysicalPath(upload.PhysicalRelativePath, upload.StorageLocationId);
                            if (File.Exists(path))
                                File.Delete(path);
                        }
                        _db.Uploads.Delete(upload.Id);
                    }
                }
                finally
                {
                    uploadLock.Release();
                    _uploadLocks.TryRemove(id, out _);
                }
            }
        }

        private async Task<int> ReadRequestWithInactivityTimeoutAsync(HttpListenerRequest request, byte[] buffer, int count, string uploadId)
        {
            var readTask = request.InputStream.ReadAsync(buffer, 0, count);
            using (var timeout = new CancellationTokenSource())
            {
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(45), timeout.Token);
                var finished = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
                if (finished == readTask)
                {
                    timeout.Cancel();
                    return await readTask.ConfigureAwait(false);
                }
            }

            _log.App("upload-read-timeout upload=" + uploadId + " inactivitySeconds=45");
            try { request.InputStream.Close(); } catch { }
            readTask.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            throw new IOException("Upload request stalled while receiving data.");
        }

        public FileRecord Complete(UserRecord user, string id)
        {
            lock (_gate)
            {
                var upload = Get(user, id);
                if (string.Equals(upload.Status, "completed", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(upload.CompletedFileId))
                    {
                        var existing = _db.Files.FindById(upload.CompletedFileId);
                        if (existing != null)
                            return existing;
                    }
                    throw new FileNotFoundException();
                }
                if (!string.Equals(upload.Status, "active", StringComparison.Ordinal))
                    throw new InvalidOperationException("Upload is not active.");
                var path = _storage.ResolvePhysicalPath(upload.PhysicalRelativePath, upload.StorageLocationId);
                if (!File.Exists(path))
                    throw new FileNotFoundException();
                var length = new FileInfo(path).Length;
                if (length != upload.ExpectedSize || upload.CurrentOffset != upload.ExpectedSize)
                    throw new InvalidOperationException("Upload is incomplete.");
                var targetParentId = upload.ParentFolderId;
                if (!string.IsNullOrWhiteSpace(targetParentId))
                {
                    var parent = _db.Folders.FindById(targetParentId);
                    if (parent == null || parent.IsTrashed || !string.Equals(parent.OwnerId, upload.OwnerId, StringComparison.Ordinal))
                        targetParentId = null;
                }
                var name = _storage.UniqueFileName(upload.OwnerId, targetParentId, upload.OriginalName);
                var now = DateTime.UtcNow;
                var file = new FileRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    OwnerId = upload.OwnerId,
                    ParentFolderId = targetParentId,
                    OriginalName = name,
                    OriginalExtension = Path.GetExtension(name),
                    MimeType = MimeTypes.FromName(name),
                    PhysicalRelativePath = upload.PhysicalRelativePath,
                    StorageLocationId = upload.StorageLocationId,
                    Size = upload.ExpectedSize,
                    CreatedUtc = now,
                    ModifiedUtc = now,
                    ExpiresUtc = upload.ExpiresUtc
                };
                _db.Raw.BeginTrans();
                try
                {
                    _db.Files.Insert(file);
                    upload.Status = "completed";
                    upload.CompletedFileId = file.Id;
                    upload.CurrentOffset = upload.ExpectedSize;
                    upload.LastActivityUtc = now;
                    _db.Uploads.Update(upload);
                    _db.Raw.Commit();
                }
                catch
                {
                    _db.Raw.Rollback();
                    throw;
                }
                _uploadLocks.TryRemove(upload.Id, out _);
                _quota.CommitFile(file.OwnerId, file.Size, file.StorageLocationId);
                _ = _thumbnails.QueueAsync(file);
                return file;
            }
        }

        public void CleanupAbandoned(TimeSpan age)
        {
            var threshold = DateTime.UtcNow.Subtract(age);
            foreach (var upload in _db.Uploads.Find(x => x.LastActivityUtc < threshold))
            {
                try
                {
                    if (!string.Equals(upload.Status, "completed", StringComparison.Ordinal))
                    {
                        var path = _storage.ResolvePhysicalPath(upload.PhysicalRelativePath, upload.StorageLocationId);
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    _db.Uploads.Delete(upload.Id);
                    _uploadLocks.TryRemove(upload.Id, out _);
                }
                catch
                {
                }
            }
        }
    }

    internal sealed class QuotaException : Exception
    {
        public QuotaException(string code, long availableBytes) : base(code)
        {
            Code = code;
            AvailableBytes = availableBytes;
        }

        public string Code { get; }
        public long AvailableBytes { get; }
    }

    internal sealed class OffsetMismatchException : Exception
    {
        public OffsetMismatchException(long expectedOffset) : base("Upload offset mismatch.")
        {
            ExpectedOffset = expectedOffset;
        }

        public long ExpectedOffset { get; }
    }
}

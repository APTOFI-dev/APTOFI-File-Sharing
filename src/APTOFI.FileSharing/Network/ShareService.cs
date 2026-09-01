using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Security;
using APTOFI.FileSharing.Storage;

namespace APTOFI.FileSharing.Network
{
    internal sealed class ShareService
    {
        private readonly Database _db;
        private readonly CryptoService _crypto;
        private readonly PasswordService _passwords;
        private readonly StorageService _storage;
        private readonly DownloadService _downloads;
        private readonly IoBufferPool _buffers;
        private readonly TransferGate _transfers;
        private readonly ConcurrentDictionary<string, PrivateArchiveTicket> _archiveTickets = new ConcurrentDictionary<string, PrivateArchiveTicket>(StringComparer.Ordinal);
        private static readonly uint[] Crc32Table = BuildCrc32Table();

        public ShareService(Database db, CryptoService crypto, PasswordService passwords, StorageService storage, DownloadService downloads, IoBufferPool buffers, TransferGate transfers)
        {
            _db = db;
            _crypto = crypto;
            _passwords = passwords;
            _storage = storage;
            _downloads = downloads;
            _buffers = buffers;
            _transfers = transfers;
        }

        public ShareResult Create(UserRecord actor, string type, string resourceId, string password, DateTime? expiresUtc)
        {
            var ownerId = ResolveOwner(actor, type, resourceId);
            var token = _crypto.RandomToken(32);
            var record = new ShareRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                OwnerId = ownerId,
                ResourceType = type,
                ResourceId = resourceId,
                TokenHash = _crypto.Sha256Hex(token),
                TokenProtected = _crypto.ProtectString(token),
                PasswordHash = string.IsNullOrWhiteSpace(password) ? null : _passwords.Hash(password),
                ExpiresUtc = expiresUtc,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow
            };
            _db.Shares.Insert(record);
            return ToResult(record, token);
        }

        public ShareResult Regenerate(UserRecord actor, string shareId)
        {
            var share = GetForActor(actor, shareId);
            var token = _crypto.RandomToken(32);
            share.TokenHash = _crypto.Sha256Hex(token);
            share.TokenProtected = _crypto.ProtectString(token);
            share.CreatedUtc = DateTime.UtcNow;
            _db.Shares.Update(share);
            return ToResult(share, token);
        }

        public void Delete(UserRecord actor, string shareId)
        {
            var share = GetForActor(actor, shareId);
            _db.Shares.Delete(share.Id);
        }

        public IList<object> List(UserRecord actor, string resourceType, string resourceId)
        {
            var query = _db.Shares.FindAll().ToList();
            if (!string.IsNullOrWhiteSpace(resourceType))
                query = query.Where(x => string.Equals(x.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(resourceId))
                query = query.Where(x => string.Equals(x.ResourceId, resourceId, StringComparison.Ordinal)).ToList();
            return query.Where(x => x.OwnerId == actor.Id).OrderByDescending(x => x.CreatedUtc).Select(x => (object)new
            {
                x.Id,
                x.OwnerId,
                x.ResourceType,
                x.ResourceId,
                resourceName = ResolveResourceName(x),
                x.ExpiresUtc,
                x.Enabled,
                x.DownloadCount,
                hasPassword = !string.IsNullOrWhiteSpace(x.PasswordHash),
                url = BuildUrl(x, SafeUnprotectToken(x))
            }).ToList();
        }

        private string ResolveResourceName(ShareRecord share)
        {
            if (share.ResourceType == "file")
                return _db.Files.FindById(share.ResourceId)?.OriginalName ?? "404";
            return _db.Folders.FindById(share.ResourceId)?.Name ?? "404";
        }

        public ShareRecord ResolveToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return null;
            var share = _db.Shares.FindOne(x => x.TokenHash == _crypto.Sha256Hex(token));
            if (share == null || !share.Enabled)
                return null;
            if (share.ExpiresUtc.HasValue && share.ExpiresUtc.Value <= DateTime.UtcNow)
                return null;
            if (share.ResourceType == "file")
            {
                var file = _db.Files.FindById(share.ResourceId);
                if (file == null || file.IsTrashed || (file.ExpiresUtc.HasValue && file.ExpiresUtc.Value <= DateTime.UtcNow))
                    return null;
            }
            else
            {
                var sharedFolder = _db.Folders.FindById(share.ResourceId);
                if (sharedFolder == null || sharedFolder.IsTrashed)
                    return null;
            }
            return share;
        }

        public bool HasAccess(HttpListenerRequest request, ShareRecord share)
        {
            if (string.IsNullOrWhiteSpace(share.PasswordHash))
                return true;
            var name = GrantCookieName(share.TokenHash);
            return _crypto.ValidateShareGrant(request.Cookies[name]?.Value, share.TokenHash);
        }

        public bool AuthenticatePassword(HttpListenerResponse response, ShareRecord share, string password, bool secure)
        {
            if (share == null || !_passwords.Verify(share.PasswordHash, password))
                return false;
            var expires = DateTime.UtcNow.AddHours(8);
            var grant = _crypto.CreateShareGrant(share.TokenHash, expires);
            response.SetCookie(new Cookie(GrantCookieName(share.TokenHash), grant, "/d/")
            {
                HttpOnly = true,
                Secure = secure,
                Expires = expires
            });
            return true;
        }

        public object PublicInfo(ShareRecord share, string folderId)
        {
            if (share.ResourceType == "file")
            {
                var file = _db.Files.FindById(share.ResourceId);
                if (file == null || file.IsTrashed)
                    throw new FileNotFoundException();
                return new
                {
                    type = "file",
                    id = file.Id,
                    name = file.OriginalName,
                    size = file.Size,
                    mime = file.MimeType,
                    downloads = file.DownloadCount,
                    thumbnail = !string.IsNullOrWhiteSpace(file.ThumbnailRelativePath) ? "/d/" + SafeUnprotectToken(share) + "/thumb/" + file.Id : null,
                    expiresUtc = share.ExpiresUtc
                };
            }
            var root = _db.Folders.FindById(share.ResourceId);
            if (root == null || root.IsTrashed)
                throw new DirectoryNotFoundException();
            var currentId = string.IsNullOrWhiteSpace(folderId) ? root.Id : folderId;
            if (!_storage.IsFolderWithin(root.Id, currentId))
                throw new DirectoryNotFoundException();
            var current = _db.Folders.FindById(currentId);
            if (current == null || current.IsTrashed)
                throw new DirectoryNotFoundException();
            var folders = _db.Folders.Find(x => x.ParentId == currentId && x.OwnerId == root.OwnerId).Where(x => !x.IsTrashed).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToList();
            var files = _db.Files.Find(x => x.ParentFolderId == currentId && x.OwnerId == root.OwnerId).Where(x => !x.IsTrashed && (!x.ExpiresUtc.HasValue || x.ExpiresUtc.Value > DateTime.UtcNow)).OrderBy(x => x.OriginalName).Select(x => new { x.Id, name = x.OriginalName, size = x.Size, downloads = x.DownloadCount, mime = x.MimeType, thumbnail = !string.IsNullOrWhiteSpace(x.ThumbnailRelativePath) ? "/d/" + SafeUnprotectToken(share) + "/thumb/" + x.Id : null }).ToList();
            return new
            {
                type = "folder",
                id = root.Id,
                name = root.Name,
                currentFolderId = current.Id,
                currentName = current.Name,
                parentId = current.Id == root.Id ? null : current.ParentId,
                folders,
                files,
                expiresUtc = share.ExpiresUtc,
                downloads = share.DownloadCount
            };
        }

        public async Task<long> DownloadFileAsync(HttpListenerContext context, ShareRecord share, string fileId)
        {
            FileRecord file;
            if (share.ResourceType == "file")
            {
                if (!string.Equals(share.ResourceId, fileId, StringComparison.Ordinal))
                    throw new FileNotFoundException();
                file = _db.Files.FindById(fileId);
            }
            else
            {
                file = _db.Files.FindById(fileId);
                if (file == null)
                    throw new FileNotFoundException();
                var root = _db.Folders.FindById(share.ResourceId);
                if (root == null || root.IsTrashed || !_storage.IsFolderWithin(root.Id, file.ParentFolderId))
                    throw new FileNotFoundException();
            }
            if (file == null || file.IsTrashed || (file.ExpiresUtc.HasValue && file.ExpiresUtc.Value <= DateTime.UtcNow))
                throw new FileNotFoundException();
            if (!await _downloads.EnsureTicketOrRedirectAsync(context, file, share).ConfigureAwait(false))
                return 0;
            return await _downloads.SendFileAsync(context, file, share).ConfigureAwait(false);
        }

        public async Task<long> DownloadFolderZipAsync(HttpListenerContext context, ShareRecord share)
        {
            if (share.ResourceType != "folder")
                throw new DirectoryNotFoundException();
            var root = _db.Folders.FindById(share.ResourceId);
            if (root == null || root.IsTrashed)
                throw new DirectoryNotFoundException();
            var plan = BuildFolderArchivePlan(root);
            var sent = await StreamZip64Async(context, plan, HttpUtil.RepairLegacyUtf8(root.Name) + ".zip").ConfigureAwait(false);
            var current = _db.Shares.FindById(share.Id);
            if (current != null)
            {
                current.DownloadCount++;
                _db.Shares.Update(current);
            }
            return sent;
        }

        public async Task<long> DownloadPrivateFolderZipAsync(HttpListenerContext context, UserRecord actor, string folderId)
        {
            var root = _storage.GetFolderForActor(actor, folderId);
            var plan = BuildFolderArchivePlan(root);
            return await StreamZip64Async(context, plan, HttpUtil.RepairLegacyUtf8(root.Name) + ".zip").ConfigureAwait(false);
        }

        public string CreatePrivateArchiveTicket(UserRecord actor, IList<ArchiveSelectionItem> items)
        {
            if (actor == null)
                throw new UnauthorizedAccessException();
            if (items == null || items.Count == 0 || items.Count > 10000)
                throw new InvalidOperationException("Archive selection is empty or too large.");
            CleanupPrivateArchiveTickets();
            var now = DateTime.UtcNow;
            var normalized = new List<ArchiveSelectionItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var type = (item?.Type ?? string.Empty).Trim().ToLowerInvariant();
                var id = (item?.Id ?? string.Empty).Trim();
                if (id.Length == 0 || (type != "file" && type != "folder"))
                    throw new InvalidOperationException("Invalid archive item.");
                var key = type + ":" + id;
                if (!seen.Add(key))
                    continue;
                if (type == "folder")
                {
                    _storage.GetFolderForActor(actor, id);
                }
                else
                {
                    var file = _storage.GetFileForActor(actor, id);
                    if (file.ExpiresUtc.HasValue && file.ExpiresUtc.Value <= now)
                        throw new FileNotFoundException();
                }
                normalized.Add(new ArchiveSelectionItem { Type = type, Id = id });
            }
            if (normalized.Count == 0)
                throw new InvalidOperationException("Archive selection is empty.");
            var plan = BuildSelectionArchivePlan(actor, normalized);
            PreflightArchivePlan(plan);
            var token = _crypto.RandomToken(24);
            _archiveTickets[token] = new PrivateArchiveTicket
            {
                OwnerId = actor.Id,
                CreatedUtc = now,
                Plan = plan
            };
            return token;
        }

        public async Task<long> DownloadPrivateArchiveAsync(HttpListenerContext context, UserRecord actor, string token)
        {
            CleanupPrivateArchiveTickets();
            if (string.IsNullOrWhiteSpace(token))
                throw new FileNotFoundException();
            if (!_archiveTickets.TryRemove(token, out var ticket) ||
                ticket == null ||
                !string.Equals(ticket.OwnerId, actor.Id, StringComparison.Ordinal) ||
                ticket.CreatedUtc < DateTime.UtcNow.AddMinutes(-15))
                throw new FileNotFoundException();
            var plan = ticket.Plan;
            if (plan == null || plan.Entries.Count == 0)
                throw new FileNotFoundException();
            var fileName = "APTOFI-selected-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip";
            return await StreamZip64Async(context, plan, fileName).ConfigureAwait(false);
        }

        public void RevokePrivateArchiveTickets(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId))
                return;
            foreach (var pair in _archiveTickets)
            {
                if (pair.Value != null && string.Equals(pair.Value.OwnerId, ownerId, StringComparison.Ordinal))
                    _archiveTickets.TryRemove(pair.Key, out _);
            }
        }

        private void CleanupPrivateArchiveTickets()
        {
            var threshold = DateTime.UtcNow.AddMinutes(-15);
            foreach (var pair in _archiveTickets)
            {
                if (pair.Value == null || pair.Value.CreatedUtc < threshold)
                    _archiveTickets.TryRemove(pair.Key, out _);
            }
        }

        private ArchivePlan BuildFolderArchivePlan(FolderRecord root)
        {
            if (root == null || root.IsTrashed)
                throw new DirectoryNotFoundException();
            var tree = LoadOwnerTree(root.OwnerId);
            var plan = new ArchivePlan();
            var visitedFolders = new HashSet<string>(StringComparer.Ordinal);
            var visitedFiles = new HashSet<string>(StringComparer.Ordinal);
            var topName = SanitizeZipSegment(HttpUtil.RepairLegacyUtf8(root.Name));
            AddFolderTreeToPlan(plan, root, topName, tree, visitedFolders, visitedFiles);
            FinalizeArchivePlan(plan);
            return plan;
        }

        private ArchivePlan BuildSelectionArchivePlan(UserRecord actor, IList<ArchiveSelectionItem> items)
        {
            var tree = LoadOwnerTree(actor.Id);
            var plan = new ArchivePlan();
            var visitedFolders = new HashSet<string>(StringComparer.Ordinal);
            var visitedFiles = new HashSet<string>(StringComparer.Ordinal);
            var usedTopNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.Where(x => string.Equals(x.Type, "folder", StringComparison.OrdinalIgnoreCase)))
            {
                if (!tree.FolderById.TryGetValue(item.Id, out var folder) || folder.IsTrashed || visitedFolders.Contains(folder.Id))
                    continue;
                var topName = UniqueTopLevelName(SanitizeZipSegment(HttpUtil.RepairLegacyUtf8(folder.Name)), true, usedTopNames);
                AddFolderTreeToPlan(plan, folder, topName, tree, visitedFolders, visitedFiles);
            }
            foreach (var item in items.Where(x => string.Equals(x.Type, "file", StringComparison.OrdinalIgnoreCase)))
            {
                if (!tree.FileById.TryGetValue(item.Id, out var file) || file.IsTrashed || visitedFiles.Contains(file.Id))
                    continue;
                if (file.ExpiresUtc.HasValue && file.ExpiresUtc.Value <= DateTime.UtcNow)
                    continue;
                var topName = UniqueTopLevelName(SanitizeZipSegment(HttpUtil.RepairLegacyUtf8(file.OriginalName)), false, usedTopNames);
                AddFileToPlan(plan, file, topName, visitedFiles);
            }
            if (plan.Entries.Count == 0)
                throw new FileNotFoundException();
            FinalizeArchivePlan(plan);
            return plan;
        }

        private OwnerTree LoadOwnerTree(string ownerId)
        {
            var folders = _db.Folders.Find(x => x.OwnerId == ownerId).Where(x => !x.IsTrashed).ToList();
            var files = _db.Files.Find(x => x.OwnerId == ownerId).Where(x => !x.IsTrashed).ToList();
            return new OwnerTree
            {
                FolderById = folders.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal),
                FileById = files.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal),
                ChildFolders = folders.GroupBy(x => x.ParentId ?? string.Empty).ToDictionary(x => x.Key, x => x.OrderBy(y => y.Name, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.Ordinal),
                ChildFiles = files.GroupBy(x => x.ParentFolderId ?? string.Empty).ToDictionary(x => x.Key, x => x.OrderBy(y => y.OriginalName, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.Ordinal)
            };
        }

        private void AddFolderTreeToPlan(ArchivePlan plan, FolderRecord root, string rootPath, OwnerTree tree, HashSet<string> visitedFolders, HashSet<string> visitedFiles)
        {
            if (!visitedFolders.Add(root.Id))
                return;
            AddDirectoryToPlan(plan, rootPath, root.ModifiedUtc);
            if (tree.ChildFiles.TryGetValue(root.Id, out var files))
            {
                foreach (var file in files)
                {
                    if (file.ExpiresUtc.HasValue && file.ExpiresUtc.Value <= DateTime.UtcNow)
                        continue;
                    AddFileToPlan(plan, file, rootPath + "/" + SanitizeZipSegment(HttpUtil.RepairLegacyUtf8(file.OriginalName)), visitedFiles);
                }
            }
            if (tree.ChildFolders.TryGetValue(root.Id, out var folders))
            {
                foreach (var child in folders)
                    AddFolderTreeToPlan(plan, child, rootPath + "/" + SanitizeZipSegment(HttpUtil.RepairLegacyUtf8(child.Name)), tree, visitedFolders, visitedFiles);
            }
        }

        private void AddDirectoryToPlan(ArchivePlan plan, string path, DateTime modifiedUtc)
        {
            var name = NormalizeZipPath(path).TrimEnd('/') + "/";
            plan.Entries.Add(NewPlanEntry(name, null, true, 0, modifiedUtc));
        }

        private void AddFileToPlan(ArchivePlan plan, FileRecord file, string path, HashSet<string> visitedFiles)
        {
            if (file == null || !visitedFiles.Add(file.Id))
                return;
            var physicalPath = _storage.ResolvePhysicalPath(file.PhysicalRelativePath, file.StorageLocationId);
            if (!File.Exists(physicalPath))
                return;
            var actualSize = new FileInfo(physicalPath).Length;
            var name = NormalizeZipPath(path).Trim('/');
            plan.Entries.Add(NewPlanEntry(name, file, false, actualSize, file.ModifiedUtc));
            plan.SourceBytes = checked(plan.SourceBytes + actualSize);
        }

        private static ArchivePlanEntry NewPlanEntry(string name, FileRecord file, bool isDirectory, long size, DateTime modifiedUtc)
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            if (bytes.Length == 0 || bytes.Length > ushort.MaxValue)
                throw new InvalidOperationException("ZIP entry path is too long.");
            return new ArchivePlanEntry
            {
                Name = name,
                NameBytes = bytes,
                File = file,
                IsDirectory = isDirectory,
                Size = size,
                ModifiedUtc = modifiedUtc
            };
        }

        private static string NormalizeZipPath(string value)
        {
            return (value ?? "item").Replace('\\', '/');
        }

        private static string UniqueTopLevelName(string value, bool folder, HashSet<string> used)
        {
            var candidate = string.IsNullOrWhiteSpace(value) ? "item" : value;
            if (used.Add(candidate))
                return candidate;
            var extension = folder ? string.Empty : Path.GetExtension(candidate);
            var stem = folder ? candidate : Path.GetFileNameWithoutExtension(candidate);
            for (var i = 2; i < 100000; i++)
            {
                var next = stem + " (" + i + ")" + extension;
                if (used.Add(next))
                    return next;
            }
            return Guid.NewGuid().ToString("N") + extension;
        }

        private static void FinalizeArchivePlan(ArchivePlan plan)
        {
            long length = 98;
            foreach (var entry in plan.Entries)
            {
                var nameLength = entry.NameBytes.Length;
                length = checked(length + 30L + nameLength + 20L);
                if (!entry.IsDirectory)
                    length = checked(length + entry.Size + 24L);
                length = checked(length + 46L + nameLength + 28L);
            }
            plan.ArchiveBytes = length;
        }

        private async Task<long> StreamZip64Async(HttpListenerContext context, ArchivePlan plan, string archiveFileName)
        {
            using (await _transfers.EnterAsync().ConfigureAwait(false))
            {
                PreflightArchivePlan(plan);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/zip";
                context.Response.SendChunked = false;
                context.Response.ContentLength64 = plan.ArchiveBytes;
                context.Response.Headers["Content-Disposition"] = HttpUtil.SafeFileNameHeader(archiveFileName);
                context.Response.Headers["Cache-Control"] = "no-store";
                var output = context.Response.OutputStream;
                long position = 0;
                foreach (var entry in plan.Entries)
                {
                    entry.LocalOffset = position;
                    var localHeader = BuildLocalHeader(entry);
                    await output.WriteAsync(localHeader, 0, localHeader.Length).ConfigureAwait(false);
                    position += localHeader.Length;
                    if (entry.IsDirectory)
                    {
                        entry.Crc32 = 0;
                        continue;
                    }
                    var crc = 0xFFFFFFFFu;
                    var buffer = _buffers.Rent();
                    long copied = 0;
                    try
                    {
                        var physicalPath = _storage.ResolvePhysicalPath(entry.File.PhysicalRelativePath, entry.File.StorageLocationId);
                        using (var source = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            while (copied < entry.Size)
                            {
                                var wanted = (int)Math.Min(buffer.Length, entry.Size - copied);
                                var read = await source.ReadAsync(buffer, 0, wanted).ConfigureAwait(false);
                                if (read <= 0)
                                    throw new IOException("Physical file ended before the recorded size.");
                                crc = UpdateCrc32(crc, buffer, read);
                                await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                                copied += read;
                                position += read;
                            }
                        }
                    }
                    finally
                    {
                        _buffers.Return(buffer);
                    }
                    entry.Crc32 = crc ^ 0xFFFFFFFFu;
                    var descriptor = BuildDataDescriptor(entry);
                    await output.WriteAsync(descriptor, 0, descriptor.Length).ConfigureAwait(false);
                    position += descriptor.Length;
                }
                var centralOffset = position;
                foreach (var entry in plan.Entries)
                {
                    var central = BuildCentralHeader(entry);
                    await output.WriteAsync(central, 0, central.Length).ConfigureAwait(false);
                    position += central.Length;
                }
                var centralSize = position - centralOffset;
                var zip64Offset = position;
                var end = BuildEndRecords(plan.Entries.Count, centralSize, centralOffset, zip64Offset);
                await output.WriteAsync(end, 0, end.Length).ConfigureAwait(false);
                position += end.Length;
                if (position != plan.ArchiveBytes)
                    throw new IOException("ZIP archive size calculation mismatch.");
                await output.FlushAsync().ConfigureAwait(false);
                return position;
            }
        }

        private void PreflightArchivePlan(ArchivePlan plan)
        {
            foreach (var entry in plan.Entries)
            {
                if (entry.IsDirectory)
                    continue;
                var path = _storage.ResolvePhysicalPath(entry.File.PhysicalRelativePath, entry.File.StorageLocationId);
                if (!File.Exists(path))
                    throw new FileNotFoundException();
                var actual = new FileInfo(path).Length;
                if (actual != entry.Size)
                    throw new IOException("Physical file size does not match metadata.");
            }
        }

        private static byte[] BuildLocalHeader(ArchivePlanEntry entry)
        {
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                var flags = (ushort)(0x0800 | (entry.IsDirectory ? 0 : 0x0008));
                GetDosDateTime(entry.ModifiedUtc, out var dosTime, out var dosDate);
                writer.Write(0x04034b50u);
                writer.Write((ushort)45);
                writer.Write(flags);
                writer.Write((ushort)0);
                writer.Write(dosTime);
                writer.Write(dosDate);
                writer.Write(entry.IsDirectory ? 0u : 0u);
                writer.Write(0xFFFFFFFFu);
                writer.Write(0xFFFFFFFFu);
                writer.Write((ushort)entry.NameBytes.Length);
                writer.Write((ushort)20);
                writer.Write(entry.NameBytes);
                writer.Write((ushort)0x0001);
                writer.Write((ushort)16);
                writer.Write((ulong)entry.Size);
                writer.Write((ulong)entry.Size);
                writer.Flush();
                return memory.ToArray();
            }
        }

        private static byte[] BuildDataDescriptor(ArchivePlanEntry entry)
        {
            using (var memory = new MemoryStream(24))
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                writer.Write(0x08074b50u);
                writer.Write(entry.Crc32);
                writer.Write((ulong)entry.Size);
                writer.Write((ulong)entry.Size);
                writer.Flush();
                return memory.ToArray();
            }
        }

        private static byte[] BuildCentralHeader(ArchivePlanEntry entry)
        {
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                var flags = (ushort)(0x0800 | (entry.IsDirectory ? 0 : 0x0008));
                GetDosDateTime(entry.ModifiedUtc, out var dosTime, out var dosDate);
                writer.Write(0x02014b50u);
                writer.Write((ushort)45);
                writer.Write((ushort)45);
                writer.Write(flags);
                writer.Write((ushort)0);
                writer.Write(dosTime);
                writer.Write(dosDate);
                writer.Write(entry.Crc32);
                writer.Write(0xFFFFFFFFu);
                writer.Write(0xFFFFFFFFu);
                writer.Write((ushort)entry.NameBytes.Length);
                writer.Write((ushort)28);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((uint)(entry.IsDirectory ? 0x10 : 0));
                writer.Write(0xFFFFFFFFu);
                writer.Write(entry.NameBytes);
                writer.Write((ushort)0x0001);
                writer.Write((ushort)24);
                writer.Write((ulong)entry.Size);
                writer.Write((ulong)entry.Size);
                writer.Write((ulong)entry.LocalOffset);
                writer.Flush();
                return memory.ToArray();
            }
        }

        private static byte[] BuildEndRecords(int entryCount, long centralSize, long centralOffset, long zip64Offset)
        {
            using (var memory = new MemoryStream(98))
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                writer.Write(0x06064b50u);
                writer.Write((ulong)44);
                writer.Write((ushort)45);
                writer.Write((ushort)45);
                writer.Write(0u);
                writer.Write(0u);
                writer.Write((ulong)entryCount);
                writer.Write((ulong)entryCount);
                writer.Write((ulong)centralSize);
                writer.Write((ulong)centralOffset);
                writer.Write(0x07064b50u);
                writer.Write(0u);
                writer.Write((ulong)zip64Offset);
                writer.Write(1u);
                writer.Write(0x06054b50u);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)Math.Min(entryCount, ushort.MaxValue));
                writer.Write((ushort)Math.Min(entryCount, ushort.MaxValue));
                writer.Write(0xFFFFFFFFu);
                writer.Write(0xFFFFFFFFu);
                writer.Write((ushort)0);
                writer.Flush();
                return memory.ToArray();
            }
        }

        private static void GetDosDateTime(DateTime value, out ushort dosTime, out ushort dosDate)
        {
            var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
            if (local.Year < 1980)
                local = new DateTime(1980, 1, 1, 0, 0, 0);
            if (local.Year > 2107)
                local = new DateTime(2107, 12, 31, 23, 59, 58);
            dosTime = (ushort)((local.Hour << 11) | (local.Minute << 5) | (local.Second / 2));
            dosDate = (ushort)(((local.Year - 1980) << 9) | (local.Month << 5) | local.Day);
        }

        private static uint UpdateCrc32(uint crc, byte[] buffer, int count)
        {
            for (var i = 0; i < count; i++)
                crc = Crc32Table[(int)((crc ^ buffer[i]) & 0xFF)] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (var i = 0; i < table.Length; i++)
            {
                var value = (uint)i;
                for (var bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }

        private ShareRecord GetForActor(UserRecord actor, string shareId)
        {
            var share = _db.Shares.FindById(shareId);
            if (share == null || share.OwnerId != actor.Id)
                throw new FileNotFoundException();
            return share;
        }

        private string ResolveOwner(UserRecord actor, string type, string resourceId)
        {
            if (type == "file")
                return _storage.GetFileForActor(actor, resourceId).OwnerId;
            if (type == "folder")
                return _storage.GetFolderForActor(actor, resourceId).OwnerId;
            throw new InvalidOperationException("Invalid resource type.");
        }

        private ShareResult ToResult(ShareRecord record, string token)
        {
            return new ShareResult
            {
                Id = record.Id,
                Url = BuildUrl(record, token),
                ExpiresUtc = record.ExpiresUtc,
                HasPassword = !string.IsNullOrWhiteSpace(record.PasswordHash)
            };
        }

        private string BuildUrl(ShareRecord record, string token)
        {
            var settings = _db.GetSettings();
            var baseUrl = string.IsNullOrWhiteSpace(settings?.PublicBaseUrl) ? "http://127.0.0.1:" + (settings?.HttpPort ?? 80) : settings.PublicBaseUrl.TrimEnd('/');
            return baseUrl + "/d/" + token;
        }

        private string SafeUnprotectToken(ShareRecord share)
        {
            try
            {
                return _crypto.UnprotectString(share.TokenProtected);
            }
            catch
            {
                return null;
            }
        }

        private static string GrantCookieName(string tokenHash)
        {
            return "afs_share_" + tokenHash.Substring(0, 12);
        }

        private static string SanitizeZipSegment(string value)
        {
            var source = HttpUtil.RepairLegacyUtf8(value ?? "item");
            var builder = new StringBuilder(source.Length);
            foreach (var c in source)
            {
                if (c < 32 || c == '/' || c == '\\' || c == ':')
                    builder.Append('_');
                else
                    builder.Append(c);
            }
            var s = builder.ToString().Trim();
            while (s.Contains(".."))
                s = s.Replace("..", "_");
            return string.IsNullOrWhiteSpace(s) ? "item" : s;
        }

        private sealed class ArchivePlan
        {
            public List<ArchivePlanEntry> Entries { get; } = new List<ArchivePlanEntry>();
            public long SourceBytes { get; set; }
            public long ArchiveBytes { get; set; }
        }

        private sealed class ArchivePlanEntry
        {
            public string Name { get; set; }
            public byte[] NameBytes { get; set; }
            public FileRecord File { get; set; }
            public bool IsDirectory { get; set; }
            public long Size { get; set; }
            public DateTime ModifiedUtc { get; set; }
            public long LocalOffset { get; set; }
            public uint Crc32 { get; set; }
        }

        private sealed class OwnerTree
        {
            public Dictionary<string, FolderRecord> FolderById { get; set; }
            public Dictionary<string, FileRecord> FileById { get; set; }
            public Dictionary<string, List<FolderRecord>> ChildFolders { get; set; }
            public Dictionary<string, List<FileRecord>> ChildFiles { get; set; }
        }

        private sealed class PrivateArchiveTicket
        {
            public string OwnerId { get; set; }
            public DateTime CreatedUtc { get; set; }
            public ArchivePlan Plan { get; set; }
        }
    }

    internal sealed class ArchiveSelectionItem
    {
        public string Type { get; set; }
        public string Id { get; set; }
    }

    internal sealed class ShareResult
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public DateTime? ExpiresUtc { get; set; }
        public bool HasPassword { get; set; }
    }
}

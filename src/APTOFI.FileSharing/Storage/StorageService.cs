using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Security;

namespace APTOFI.FileSharing.Storage
{
    internal sealed class StorageService
    {
        private readonly Database _db;
        private readonly CryptoService _crypto;
        private readonly QuotaService _quota;

        public StorageService(Database db, CryptoService crypto, QuotaService quota)
        {
            _db = db;
            _crypto = crypto;
            _quota = quota;
        }

        public void EnsureStorage()
        {
            var settings = _db.GetSettings();
            var locations = GetLocations(settings);
            if (locations.Count == 0)
                throw new InvalidOperationException("Storage is not configured.");
            foreach (var location in locations.Where(x => x.Enabled))
            {
                Directory.CreateDirectory(location.Path);
                Directory.CreateDirectory(Path.Combine(location.Path, "storage"));
            }
        }

        public string AllocatePhysicalRelativePath(string storageLocationId)
        {
            var id = _crypto.RandomHex(24);
            var ext = _crypto.RandomHex(3);
            var relative = Path.Combine("storage", id.Substring(0, 2), id.Substring(2, 2), id + "." + ext);
            var full = ResolvePhysicalPath(relative, storageLocationId);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            return relative;
        }

        public string ResolvePhysicalPath(string relative, string storageLocationId)
        {
            var settings = _db.GetSettings();
            var location = ResolveLocation(settings, storageLocationId);
            var root = Path.GetFullPath(location.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, relative ?? string.Empty));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid storage path.");
            return full;
        }

        public string ResolvePhysicalPath(string relative)
        {
            return ResolvePhysicalPath(relative, null);
        }

        public static IList<StorageLocationSetting> GetLocations(AppSettings settings)
        {
            if (settings == null)
                return new List<StorageLocationSetting>();
            var locations = settings.StorageLocations == null ? new List<StorageLocationSetting>() : settings.StorageLocations.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path)).ToList();
            if (locations.Count == 0 && !string.IsNullOrWhiteSpace(settings.StorageRoot))
                locations.Add(new StorageLocationSetting { Id = "primary", Path = settings.StorageRoot, Enabled = true });
            foreach (var location in locations)
            {
                if (string.IsNullOrWhiteSpace(location.Id))
                    location.Id = Guid.NewGuid().ToString("N");
                location.Path = Path.GetFullPath(location.Path.Trim());
            }
            return locations;
        }

        public static StorageLocationSetting ResolveLocation(AppSettings settings, string storageLocationId)
        {
            var locations = GetLocations(settings);
            if (locations.Count == 0)
                throw new InvalidOperationException("Storage is not configured.");
            if (!string.IsNullOrWhiteSpace(storageLocationId))
            {
                var exact = locations.FirstOrDefault(x => string.Equals(x.Id, storageLocationId, StringComparison.Ordinal));
                if (exact != null)
                    return exact;
            }
            return locations[0];
        }

        public FolderRecord CreateFolder(UserRecord actor, string parentId, string name)
        {
            var effectiveOwner = actor.Id;
            ValidateParent(effectiveOwner, parentId);
            var safeName = NormalizeName(name, "New folder");
            safeName = UniqueFolderName(effectiveOwner, parentId, safeName);
            var now = DateTime.UtcNow;
            var folder = new FolderRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                OwnerId = effectiveOwner,
                ParentId = NormalizeId(parentId),
                Name = safeName,
                CreatedUtc = now,
                ModifiedUtc = now
            };
            _db.Folders.Insert(folder);
            return folder;
        }

        public void Rename(UserRecord actor, string type, string id, string name)
        {
            if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                var file = GetFileForActor(actor, id);
                file.OriginalName = UniqueFileName(file.OwnerId, file.ParentFolderId, NormalizeName(name, file.OriginalName), file.Id);
                file.OriginalExtension = Path.GetExtension(file.OriginalName);
                file.MimeType = MimeTypes.FromName(file.OriginalName);
                file.ModifiedUtc = DateTime.UtcNow;
                _db.Files.Update(file);
                return;
            }
            var folder = GetFolderForActor(actor, id);
            folder.Name = UniqueFolderName(folder.OwnerId, folder.ParentId, NormalizeName(name, folder.Name), folder.Id);
            folder.ModifiedUtc = DateTime.UtcNow;
            _db.Folders.Update(folder);
        }

        public void Move(UserRecord actor, string type, string id, string parentId)
        {
            if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                var file = GetFileForActor(actor, id);
                ValidateParent(file.OwnerId, parentId);
                file.ParentFolderId = NormalizeId(parentId);
                file.OriginalName = UniqueFileName(file.OwnerId, file.ParentFolderId, file.OriginalName, file.Id);
                file.ModifiedUtc = DateTime.UtcNow;
                _db.Files.Update(file);
                return;
            }
            var folder = GetFolderForActor(actor, id);
            ValidateParent(folder.OwnerId, parentId);
            var target = NormalizeId(parentId);
            if (string.Equals(target, folder.Id, StringComparison.Ordinal) || IsDescendant(folder.Id, target))
                throw new InvalidOperationException("A folder cannot be moved into itself or its descendant.");
            folder.ParentId = target;
            folder.Name = UniqueFolderName(folder.OwnerId, folder.ParentId, folder.Name, folder.Id);
            folder.ModifiedUtc = DateTime.UtcNow;
            _db.Folders.Update(folder);
        }

        public void Delete(UserRecord actor, string type, string id)
        {
            var settings = _db.GetSettings();
            if (settings != null && settings.TrashEnabled)
            {
                MoveToTrash(actor, type, id);
                return;
            }
            if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                DeleteFile(GetFileForActor(actor, id));
                return;
            }
            var folder = GetFolderForActor(actor, id);
            DeleteFolderRecursive(folder);
        }

        public long PurgeOwnerData(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId))
                return 0;
            long released = 0;
            foreach (var file in _db.Files.Find(x => x.OwnerId == ownerId).ToList())
            {
                released += Math.Max(0, file.Size);
                DeleteFile(file);
            }
            _db.Shares.DeleteMany(x => x.OwnerId == ownerId);
            _db.Folders.DeleteMany(x => x.OwnerId == ownerId);
            return released;
        }

        public IList<FolderRecord> ChildFolders(string ownerId, string parentId)
        {
            var normalized = NormalizeId(parentId);
            return _db.Folders.Find(x => x.OwnerId == ownerId && x.ParentId == normalized).Where(x => !x.IsTrashed).OrderBy(x => x.Name).ToList();
        }

        public IList<FileRecord> ChildFiles(string ownerId, string parentId)
        {
            var normalized = NormalizeId(parentId);
            return _db.Files.Find(x => x.OwnerId == ownerId && x.ParentFolderId == normalized).Where(x => !x.IsTrashed).OrderBy(x => x.OriginalName).ToList();
        }

        public FileRecord GetFileForActor(UserRecord actor, string id)
        {
            var file = _db.Files.FindById(id);
            if (file == null || file.IsTrashed || !string.Equals(file.OwnerId, actor.Id, StringComparison.Ordinal))
                throw new FileNotFoundException();
            return file;
        }

        public FolderRecord GetFolderForActor(UserRecord actor, string id)
        {
            var folder = _db.Folders.FindById(id);
            if (folder == null || folder.IsTrashed || !string.Equals(folder.OwnerId, actor.Id, StringComparison.Ordinal))
                throw new DirectoryNotFoundException();
            return folder;
        }

        public FolderStatistics GetFolderStatistics(UserRecord actor, string id)
        {
            var root = GetFolderForActor(actor, id);
            var ownerId = actor.Id;
            var allFolders = _db.Folders.Find(x => x.OwnerId == ownerId).Where(x => !x.IsTrashed).ToList();
            var allFiles = _db.Files.Find(x => x.OwnerId == ownerId).Where(x => !x.IsTrashed).ToList();
            var children = allFolders.GroupBy(x => x.ParentId ?? string.Empty).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
            var folderIds = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            folderIds.Add(root.Id);
            queue.Enqueue(root.Id);
            long folderCount = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                List<FolderRecord> list;
                if (!children.TryGetValue(current, out list))
                    continue;
                foreach (var child in list)
                {
                    if (!folderIds.Add(child.Id))
                        continue;
                    folderCount++;
                    queue.Enqueue(child.Id);
                }
            }
            var now = DateTime.UtcNow;
            long fileCount = 0;
            long totalSize = 0;
            foreach (var file in allFiles)
            {
                if (!folderIds.Contains(file.ParentFolderId ?? string.Empty))
                    continue;
                if (file.ExpiresUtc.HasValue && file.ExpiresUtc.Value <= now)
                    continue;
                fileCount++;
                if (file.Size > 0)
                    totalSize += file.Size;
            }
            return new FolderStatistics
            {
                FileCount = fileCount,
                FolderCount = folderCount,
                TotalSize = totalSize
            };
        }

        public long GetTrashBytes(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId))
                return 0;
            return _db.Files.Find(x => x.OwnerId == ownerId && x.IsTrashed).Sum(x => x.Size);
        }

        public IList<TrashItemInfo> GetTrash(UserRecord actor)
        {
            EnsureTrashEnabled();
            var items = new List<TrashItemInfo>();
            foreach (var file in _db.Files.Find(x => x.OwnerId == actor.Id && x.IsTrashed).Where(x => string.Equals(x.TrashRootId, x.Id, StringComparison.Ordinal)))
            {
                var trashed = file.TrashedUtc ?? file.ModifiedUtc;
                items.Add(new TrashItemInfo
                {
                    Type = "file",
                    Id = file.Id,
                    Name = file.OriginalName,
                    Size = file.Size,
                    FileCount = 1,
                    FolderCount = 0,
                    TrashedUtc = trashed,
                    DeleteUtc = trashed.AddDays(30)
                });
            }
            foreach (var folder in _db.Folders.Find(x => x.OwnerId == actor.Id && x.IsTrashed).Where(x => string.Equals(x.TrashRootId, x.Id, StringComparison.Ordinal)))
            {
                var rootId = folder.Id;
                var trashed = folder.TrashedUtc ?? folder.ModifiedUtc;
                var files = _db.Files.Find(x => x.OwnerId == actor.Id && x.IsTrashed && x.TrashRootId == rootId).ToList();
                var folders = _db.Folders.Find(x => x.OwnerId == actor.Id && x.IsTrashed && x.TrashRootId == rootId).ToList();
                items.Add(new TrashItemInfo
                {
                    Type = "folder",
                    Id = folder.Id,
                    Name = folder.Name,
                    Size = files.Sum(x => x.Size),
                    FileCount = files.Count,
                    FolderCount = Math.Max(0, folders.Count - 1),
                    TrashedUtc = trashed,
                    DeleteUtc = trashed.AddDays(30)
                });
            }
            return items.OrderByDescending(x => x.TrashedUtc).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public void RestoreTrash(UserRecord actor, string type, string id)
        {
            EnsureTrashEnabled();
            if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                var file = _db.Files.FindById(id);
                if (file == null || !file.IsTrashed || file.TrashRootId != file.Id || file.OwnerId != actor.Id)
                    throw new FileNotFoundException();
                var parentId = ActiveParentOrRoot(file.OwnerId, file.ParentFolderId);
                file.ParentFolderId = parentId;
                file.OriginalName = UniqueFileName(file.OwnerId, parentId, file.OriginalName, file.Id);
                file.IsTrashed = false;
                file.TrashedUtc = null;
                file.TrashRootId = null;
                file.ModifiedUtc = DateTime.UtcNow;
                _db.Files.Update(file);
                return;
            }
            var root = _db.Folders.FindById(id);
            if (root == null || !root.IsTrashed || root.TrashRootId != root.Id || root.OwnerId != actor.Id)
                throw new DirectoryNotFoundException();
            var rootId = root.Id;
            var folders = _db.Folders.Find(x => x.OwnerId == actor.Id && x.IsTrashed && x.TrashRootId == rootId).ToList();
            var files = _db.Files.Find(x => x.OwnerId == actor.Id && x.IsTrashed && x.TrashRootId == rootId).ToList();
            var parentIdForRoot = ActiveParentOrRoot(root.OwnerId, root.ParentId);
            root.ParentId = parentIdForRoot;
            root.Name = UniqueFolderName(root.OwnerId, parentIdForRoot, root.Name, root.Id);
            var now = DateTime.UtcNow;
            _db.Raw.BeginTrans();
            try
            {
                foreach (var folder in folders)
                {
                    if (folder.Id == root.Id)
                    {
                        folder.ParentId = root.ParentId;
                        folder.Name = root.Name;
                    }
                    folder.IsTrashed = false;
                    folder.TrashedUtc = null;
                    folder.TrashRootId = null;
                    if (folder.Id == root.Id)
                        folder.ModifiedUtc = now;
                    _db.Folders.Update(folder);
                }
                foreach (var file in files)
                {
                    file.IsTrashed = false;
                    file.TrashedUtc = null;
                    file.TrashRootId = null;
                    _db.Files.Update(file);
                }
                _db.Raw.Commit();
            }
            catch
            {
                _db.Raw.Rollback();
                throw;
            }
        }

        public long DeleteTrashPermanently(UserRecord actor, string type, string id)
        {
            EnsureTrashEnabled();
            if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                var file = _db.Files.FindById(id);
                if (file == null || !file.IsTrashed || file.TrashRootId != file.Id || file.OwnerId != actor.Id)
                    throw new FileNotFoundException();
                var size = file.Size;
                DeleteFile(file);
                return size;
            }
            var root = _db.Folders.FindById(id);
            if (root == null || !root.IsTrashed || root.TrashRootId != root.Id || root.OwnerId != actor.Id)
                throw new DirectoryNotFoundException();
            return DeleteTrashRoot(actor.Id, root.Id);
        }

        public long EmptyTrash(UserRecord actor)
        {
            EnsureTrashEnabled();
            long freed = 0;
            foreach (var file in _db.Files.Find(x => x.OwnerId == actor.Id && x.IsTrashed).ToList())
            {
                freed += Math.Max(0, file.Size);
                DeleteFile(file);
            }
            _db.Folders.DeleteMany(x => x.OwnerId == actor.Id && x.IsTrashed);
            return freed;
        }

        public int CleanupTrash(TimeSpan retention)
        {
            var threshold = DateTime.UtcNow.Subtract(retention);
            var purgedRoots = 0;
            var fileRoots = _db.Files
                .Find(x => x.IsTrashed && x.TrashedUtc != null && x.TrashedUtc <= threshold)
                .Where(x => string.Equals(x.TrashRootId, x.Id, StringComparison.Ordinal))
                .ToList();
            foreach (var file in fileRoots)
            {
                try
                {
                    DeleteFile(file);
                    purgedRoots++;
                }
                catch
                {
                }
            }
            var folderRoots = _db.Folders
                .Find(x => x.IsTrashed && x.TrashedUtc != null && x.TrashedUtc <= threshold)
                .Where(x => string.Equals(x.TrashRootId, x.Id, StringComparison.Ordinal))
                .ToList();
            foreach (var folder in folderRoots)
            {
                try
                {
                    DeleteTrashRoot(folder.OwnerId, folder.Id);
                    purgedRoots++;
                }
                catch
                {
                }
            }
            return purgedRoots;
        }

        private void MoveToTrash(UserRecord actor, string type, string id)
        {
            var now = DateTime.UtcNow;
            if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                var file = GetFileForActor(actor, id);
                file.IsTrashed = true;
                file.TrashedUtc = now;
                file.TrashRootId = file.Id;
                _db.Files.Update(file);
                _db.Shares.DeleteMany(x => x.ResourceType == "file" && x.ResourceId == file.Id);
                _db.DownloadTickets.DeleteMany(x => x.FileId == file.Id);
                return;
            }
            var root = GetFolderForActor(actor, id);
            var folderIds = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            folderIds.Add(root.Id);
            queue.Enqueue(root.Id);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var child in _db.Folders.Find(x => x.OwnerId == actor.Id && x.ParentId == current).Where(x => !x.IsTrashed).ToList())
                {
                    if (folderIds.Add(child.Id))
                        queue.Enqueue(child.Id);
                }
            }
            var folders = _db.Folders.Find(x => x.OwnerId == actor.Id).Where(x => !x.IsTrashed && folderIds.Contains(x.Id)).ToList();
            var files = _db.Files.Find(x => x.OwnerId == actor.Id).Where(x => !x.IsTrashed && folderIds.Contains(x.ParentFolderId)).ToList();
            _db.Raw.BeginTrans();
            try
            {
                foreach (var folder in folders)
                {
                    folder.IsTrashed = true;
                    folder.TrashedUtc = now;
                    folder.TrashRootId = root.Id;
                    _db.Folders.Update(folder);
                    _db.Shares.DeleteMany(x => x.ResourceType == "folder" && x.ResourceId == folder.Id);
                }
                foreach (var file in files)
                {
                    file.IsTrashed = true;
                    file.TrashedUtc = now;
                    file.TrashRootId = root.Id;
                    _db.Files.Update(file);
                    _db.Shares.DeleteMany(x => x.ResourceType == "file" && x.ResourceId == file.Id);
                    _db.DownloadTickets.DeleteMany(x => x.FileId == file.Id);
                }
                _db.Raw.Commit();
            }
            catch
            {
                _db.Raw.Rollback();
                throw;
            }
        }

        private long DeleteTrashRoot(string ownerId, string rootId)
        {
            long freed = 0;
            foreach (var file in _db.Files.Find(x => x.OwnerId == ownerId && x.IsTrashed && x.TrashRootId == rootId).ToList())
            {
                freed += Math.Max(0, file.Size);
                DeleteFile(file);
            }
            foreach (var folder in _db.Folders.Find(x => x.OwnerId == ownerId && x.IsTrashed && x.TrashRootId == rootId).ToList())
            {
                _db.Shares.DeleteMany(x => x.ResourceType == "folder" && x.ResourceId == folder.Id);
                _db.Folders.Delete(folder.Id);
            }
            return freed;
        }

        private string ActiveParentOrRoot(string ownerId, string parentId)
        {
            if (string.IsNullOrWhiteSpace(parentId))
                return null;
            var parent = _db.Folders.FindById(parentId);
            if (parent == null || parent.IsTrashed || !string.Equals(parent.OwnerId, ownerId, StringComparison.Ordinal))
                return null;
            return parent.Id;
        }

        private void EnsureTrashEnabled()
        {
            var settings = _db.GetSettings();
            if (settings == null || !settings.TrashEnabled)
                throw new FileNotFoundException();
        }

        public void CleanupExpired()
        {
            var now = DateTime.UtcNow;
            foreach (var file in _db.Files.Find(x => x.ExpiresUtc != null && x.ExpiresUtc <= now).Where(x => !x.IsTrashed).ToList())
            {
                try
                {
                    DeleteFile(file);
                }
                catch
                {
                }
            }
        }

        public bool IsFolderWithin(string rootId, string candidateId)
        {
            if (string.IsNullOrWhiteSpace(rootId) || string.IsNullOrWhiteSpace(candidateId))
                return false;
            var root = _db.Folders.FindById(rootId);
            if (root == null || root.IsTrashed)
                return false;
            if (string.Equals(rootId, candidateId, StringComparison.Ordinal))
                return true;
            var current = _db.Folders.FindById(candidateId);
            var guard = 0;
            while (current != null && !current.IsTrashed && guard++ < 1024)
            {
                if (string.Equals(current.ParentId, rootId, StringComparison.Ordinal))
                    return true;
                if (string.IsNullOrWhiteSpace(current.ParentId))
                    return false;
                current = _db.Folders.FindById(current.ParentId);
            }
            return false;
        }

        public string UniqueFileName(string ownerId, string parentId, string requested, string excludeId = null)
        {
            var baseName = Path.GetFileNameWithoutExtension(requested);
            var ext = Path.GetExtension(requested);
            var candidate = requested;
            var index = 1;
            var normalizedParentId = NormalizeId(parentId);
            while (_db.Files.Find(x => x.OwnerId == ownerId && x.ParentFolderId == normalizedParentId && x.OriginalName == candidate).Any(x => !x.IsTrashed && x.Id != excludeId))
                candidate = baseName + " (" + index++ + ")" + ext;
            return candidate;
        }

        private string UniqueFolderName(string ownerId, string parentId, string requested, string excludeId = null)
        {
            var candidate = requested;
            var index = 1;
            var normalizedParentId = NormalizeId(parentId);
            while (_db.Folders.Find(x => x.OwnerId == ownerId && x.ParentId == normalizedParentId && x.Name == candidate).Any(x => !x.IsTrashed && x.Id != excludeId))
                candidate = requested + " (" + index++ + ")";
            return candidate;
        }

        private void DeleteFolderRecursive(FolderRecord folder)
        {
            foreach (var child in _db.Folders.Find(x => x.ParentId == folder.Id).Where(x => !x.IsTrashed).ToList())
                DeleteFolderRecursive(child);
            foreach (var file in _db.Files.Find(x => x.ParentFolderId == folder.Id).Where(x => !x.IsTrashed).ToList())
                DeleteFile(file);
            _db.Shares.DeleteMany(x => x.ResourceType == "folder" && x.ResourceId == folder.Id);
            _db.Folders.Delete(folder.Id);
        }

        private void DeleteFile(FileRecord file)
        {
            var physical = ResolvePhysicalPath(file.PhysicalRelativePath, file.StorageLocationId);
            if (File.Exists(physical))
                File.Delete(physical);
            if (!string.IsNullOrWhiteSpace(file.ThumbnailRelativePath))
            {
                var thumb = Path.Combine(AppPaths.BaseDirectory, file.ThumbnailRelativePath);
                if (File.Exists(thumb))
                    File.Delete(thumb);
            }
            _db.Shares.DeleteMany(x => x.ResourceType == "file" && x.ResourceId == file.Id);
            _db.DownloadTickets.DeleteMany(x => x.FileId == file.Id);
            _db.Files.Delete(file.Id);
            _quota.ReleaseFile(file.OwnerId, file.Size, file.StorageLocationId);
        }

        private void ValidateParent(string ownerId, string parentId)
        {
            if (string.IsNullOrWhiteSpace(parentId))
                return;
            var parent = _db.Folders.FindById(parentId);
            if (parent == null || parent.IsTrashed || !string.Equals(parent.OwnerId, ownerId, StringComparison.Ordinal))
                throw new DirectoryNotFoundException();
        }

        private bool IsDescendant(string folderId, string candidateParentId)
        {
            if (string.IsNullOrWhiteSpace(candidateParentId))
                return false;
            var current = _db.Folders.FindById(candidateParentId);
            var guard = 0;
            while (current != null && guard++ < 1024)
            {
                if (string.Equals(current.Id, folderId, StringComparison.Ordinal))
                    return true;
                if (string.IsNullOrWhiteSpace(current.ParentId))
                    return false;
                current = _db.Folders.FindById(current.ParentId);
            }
            return false;
        }

        private static string NormalizeId(string id)
        {
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        private static string NormalizeName(string name, string defaultName)
        {
            var value = (name ?? string.Empty).Trim();
            if (value.Length == 0)
                value = defaultName;
            foreach (var c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            if (value.Length > 240)
                value = value.Substring(0, 240);
            return value;
        }
    }
}

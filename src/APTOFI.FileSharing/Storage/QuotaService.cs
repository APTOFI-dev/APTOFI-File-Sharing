using System;
using System.IO;
using System.Linq;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;

namespace APTOFI.FileSharing.Storage
{
    internal sealed class QuotaService
    {
        private readonly Database _db;
        private readonly object _gate = new object();

        public QuotaService(Database db)
        {
            _db = db;
        }

        public void Reconcile()
        {
            lock (_gate)
            {
                var users = _db.Users.FindAll().ToList();
                foreach (var user in users)
                {
                    user.UsedBytes = _db.Files.Find(x => x.OwnerId == user.Id).Sum(x => x.Size);
                    _db.Users.Update(user);
                }
                var settings = _db.GetSettings();
                if (settings == null)
                    return;
                settings.GlobalUsedBytes = users.Sum(x => x.UsedBytes);
                var locations = StorageService.GetLocations(settings).ToList();
                var primaryId = locations.Count > 0 ? locations[0].Id : null;
                foreach (var location in locations)
                {
                    var id = location.Id;
                    if (id == primaryId)
                        location.UsedBytes = _db.Files.Find(x => x.StorageLocationId == id || x.StorageLocationId == null).Sum(x => x.Size);
                    else
                        location.UsedBytes = _db.Files.Find(x => x.StorageLocationId == id).Sum(x => x.Size);
                }
                settings.StorageLocations = locations;
                settings.StorageRoot = locations.Count > 0 ? locations[0].Path : settings.StorageRoot;
                _db.SaveSettings(settings);
            }
        }

        public QuotaCheck CheckAndReserve(UserRecord user, long size)
        {
            if (user == null)
                return QuotaCheck.Fail("user_not_found", 0);
            if (size < 0)
                return QuotaCheck.Fail("invalid_size", 0);
            Reconcile();
            lock (_gate)
            {
                var settings = _db.GetSettings();
                if (settings == null)
                    return QuotaCheck.Fail("storage_not_configured", 0);
                var locations = StorageService.GetLocations(settings).Where(x => x.Enabled).ToList();
                if (locations.Count == 0)
                    return QuotaCheck.Fail("storage_not_configured", 0);
                var currentUser = _db.Users.FindById(user.Id);
                if (currentUser == null)
                    return QuotaCheck.Fail("user_not_found", 0);
                var reservedByUser = _db.Uploads.Find(x => x.OwnerId == user.Id && x.Status == "active").Sum(x => x.ExpectedSize);
                var reservedGlobal = _db.Uploads.Find(x => x.Status == "active").Sum(x => x.ExpectedSize);
                if (currentUser.QuotaBytes > 0)
                {
                    var personalAvailable = currentUser.QuotaBytes - currentUser.UsedBytes - reservedByUser;
                    if (size > personalAvailable)
                        return QuotaCheck.Fail("personal_quota_exceeded", Math.Max(0, personalAvailable));
                }
                if (settings.ServerQuotaBytes > 0)
                {
                    var serverAvailable = settings.ServerQuotaBytes - settings.GlobalUsedBytes - reservedGlobal;
                    if (size > serverAvailable)
                        return QuotaCheck.Fail("server_quota_exceeded", Math.Max(0, serverAvailable));
                }
                var primaryId = locations[0].Id;
                StorageLocationSetting best = null;
                long bestAvailable = -1;
                foreach (var location in locations)
                {
                    try
                    {
                        var root = Path.GetPathRoot(Path.GetFullPath(location.Path));
                        var drive = new DriveInfo(root);
                        var safety = 64L * 1024L * 1024L;
                        var physicalAvailable = Math.Max(0, drive.AvailableFreeSpace - safety);
                        long reserved;
                        var locationId = location.Id;
                        if (locationId == primaryId)
                            reserved = _db.Uploads.Find(x => x.Status == "active" && (x.StorageLocationId == locationId || x.StorageLocationId == null)).Sum(x => x.ExpectedSize);
                        else
                            reserved = _db.Uploads.Find(x => x.Status == "active" && x.StorageLocationId == locationId).Sum(x => x.ExpectedSize);
                        var quotaAvailable = location.QuotaBytes > 0 ? Math.Max(0, location.QuotaBytes - location.UsedBytes - reserved) : long.MaxValue;
                        var available = Math.Min(physicalAvailable, quotaAvailable);
                        if (available > bestAvailable)
                        {
                            bestAvailable = available;
                            best = location;
                        }
                    }
                    catch
                    {
                    }
                }
                if (best == null || size > bestAvailable)
                    return QuotaCheck.Fail("disk_space_exceeded", Math.Max(0, bestAvailable));
                return QuotaCheck.Ok(best.Id, bestAvailable);
            }
        }

        public void CommitFile(string userId, long size, string storageLocationId)
        {
            lock (_gate)
            {
                var user = _db.Users.FindById(userId);
                var settings = _db.GetSettings();
                if (user != null)
                {
                    user.UsedBytes = checked(user.UsedBytes + size);
                    _db.Users.Update(user);
                }
                if (settings != null)
                {
                    settings.GlobalUsedBytes = checked(settings.GlobalUsedBytes + size);
                    var locations = StorageService.GetLocations(settings).ToList();
                    var location = StorageService.ResolveLocation(settings, storageLocationId);
                    var target = locations.FirstOrDefault(x => x.Id == location.Id);
                    if (target != null)
                        target.UsedBytes = checked(target.UsedBytes + size);
                    settings.StorageLocations = locations;
                    _db.SaveSettings(settings);
                }
            }
        }

        public void ReleaseFile(string userId, long size, string storageLocationId)
        {
            lock (_gate)
            {
                var user = _db.Users.FindById(userId);
                var settings = _db.GetSettings();
                if (user != null)
                {
                    user.UsedBytes = Math.Max(0, user.UsedBytes - size);
                    _db.Users.Update(user);
                }
                if (settings != null)
                {
                    settings.GlobalUsedBytes = Math.Max(0, settings.GlobalUsedBytes - size);
                    var locations = StorageService.GetLocations(settings).ToList();
                    var location = StorageService.ResolveLocation(settings, storageLocationId);
                    var target = locations.FirstOrDefault(x => x.Id == location.Id);
                    if (target != null)
                        target.UsedBytes = Math.Max(0, target.UsedBytes - size);
                    settings.StorageLocations = locations;
                    _db.SaveSettings(settings);
                }
            }
        }
    }

    internal sealed class QuotaCheck
    {
        public bool Allowed { get; private set; }
        public string Error { get; private set; }
        public long AvailableBytes { get; private set; }
        public string StorageLocationId { get; private set; }

        public static QuotaCheck Ok(string storageLocationId, long available)
        {
            return new QuotaCheck { Allowed = true, StorageLocationId = storageLocationId, AvailableBytes = available };
        }

        public static QuotaCheck Fail(string error, long available)
        {
            return new QuotaCheck { Allowed = false, Error = error, AvailableBytes = available };
        }
    }
}

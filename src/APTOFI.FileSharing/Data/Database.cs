using System;
using System.Collections.Generic;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Security;
using LiteDB;

namespace APTOFI.FileSharing.Data
{
    internal sealed class Database : IDisposable
    {
        private readonly LiteDatabase _db;

        public Database(CryptoService crypto)
        {
            AppPaths.EnsureRuntimeDirectories();
            var connection = new ConnectionString
            {
                Filename = AppPaths.DatabasePath,
                Password = crypto.DatabasePassword,
                Connection = ConnectionType.Shared
            };
            _db = new LiteDatabase(connection);
            MigrateTrashMetadata();
            Configure();
            MigrateSettings();
        }

        public ILiteCollection<AppSettings> Settings => _db.GetCollection<AppSettings>("settings");
        public ILiteCollection<UserRecord> Users => _db.GetCollection<UserRecord>("users");
        public ILiteCollection<FolderRecord> Folders => _db.GetCollection<FolderRecord>("folders");
        public ILiteCollection<FileRecord> Files => _db.GetCollection<FileRecord>("files");
        public ILiteCollection<UploadRecord> Uploads => _db.GetCollection<UploadRecord>("uploads");
        public ILiteCollection<ShareRecord> Shares => _db.GetCollection<ShareRecord>("shares");
        public ILiteCollection<SessionRecord> Sessions => _db.GetCollection<SessionRecord>("sessions");
        public ILiteCollection<DownloadTicketRecord> DownloadTickets => _db.GetCollection<DownloadTicketRecord>("download_tickets");
        public ILiteCollection<BanRecord> Bans => _db.GetCollection<BanRecord>("bans");
        public LiteDatabase Raw => _db;

        public AppSettings GetSettings()
        {
            return Settings.FindById("main");
        }

        public void SaveSettings(AppSettings settings)
        {
            Settings.Upsert(settings);
        }

        public void SaveSettingsPersisted(AppSettings settings)
        {
            Settings.Upsert(settings);
            SettingsFileStore.Save(settings);
        }

        private void MigrateTrashMetadata()
        {
            var rawFolders = _db.GetCollection<BsonDocument>("folders");
            foreach (var doc in new List<BsonDocument>(rawFolders.FindAll()))
            {
                var changed = false;
                if (!doc.ContainsKey("IsTrashed"))
                {
                    doc["IsTrashed"] = false;
                    changed = true;
                }
                if (!doc.ContainsKey("TrashedUtc"))
                {
                    doc["TrashedUtc"] = BsonValue.Null;
                    changed = true;
                }
                if (!doc.ContainsKey("TrashRootId"))
                {
                    doc["TrashRootId"] = BsonValue.Null;
                    changed = true;
                }
                if (changed)
                    rawFolders.Update(doc);
            }

            var rawFiles = _db.GetCollection<BsonDocument>("files");
            foreach (var doc in new List<BsonDocument>(rawFiles.FindAll()))
            {
                var changed = false;
                if (!doc.ContainsKey("IsTrashed"))
                {
                    doc["IsTrashed"] = false;
                    changed = true;
                }
                if (!doc.ContainsKey("TrashedUtc"))
                {
                    doc["TrashedUtc"] = BsonValue.Null;
                    changed = true;
                }
                if (!doc.ContainsKey("TrashRootId"))
                {
                    doc["TrashRootId"] = BsonValue.Null;
                    changed = true;
                }
                if (changed)
                    rawFiles.Update(doc);
            }
        }

        private void Configure()
        {
            Users.EnsureIndex(x => x.Email, true);
            Folders.EnsureIndex(x => x.OwnerId);
            Folders.EnsureIndex(x => x.ParentId);
            Folders.EnsureIndex(x => x.IsTrashed);
            Folders.EnsureIndex(x => x.TrashRootId);
            Files.EnsureIndex(x => x.OwnerId);
            Files.EnsureIndex(x => x.ParentFolderId);
            Files.EnsureIndex(x => x.ExpiresUtc);
            Files.EnsureIndex(x => x.StorageLocationId);
            Files.EnsureIndex(x => x.IsTrashed);
            Files.EnsureIndex(x => x.TrashRootId);
            Uploads.EnsureIndex(x => x.OwnerId);
            Uploads.EnsureIndex(x => x.LastActivityUtc);
            Uploads.EnsureIndex(x => x.StorageLocationId);
            Shares.EnsureIndex(x => x.TokenHash, true);
            Shares.EnsureIndex(x => x.OwnerId);
            Shares.EnsureIndex(x => x.ResourceId);
            Sessions.EnsureIndex(x => x.UserId);
            Sessions.EnsureIndex(x => x.ExpiresUtc);
            DownloadTickets.EnsureIndex(x => x.FileId);
            DownloadTickets.EnsureIndex(x => x.CreatedUtc);
            Bans.EnsureIndex(x => x.LastSeenUtc);
        }

        private void MigrateSettings()
        {
            var raw = _db.GetCollection<BsonDocument>("settings");
            var doc = raw.FindById("main");
            if (doc == null)
                return;
            var changed = false;
            if (!doc.ContainsKey("StorageLocations"))
            {
                var root = doc.ContainsKey("StorageRoot") ? doc["StorageRoot"].AsString : null;
                var locations = new BsonArray();
                if (!string.IsNullOrWhiteSpace(root))
                {
                    locations.Add(new BsonDocument
                    {
                        ["Id"] = "primary",
                        ["Path"] = root,
                        ["QuotaBytes"] = 0L,
                        ["UsedBytes"] = doc.ContainsKey("GlobalUsedBytes") ? doc["GlobalUsedBytes"].AsInt64 : 0L,
                        ["Enabled"] = true
                    });
                }
                doc["StorageLocations"] = locations;
                changed = true;
            }
            if (!doc.ContainsKey("SiteName"))
            {
                doc["SiteName"] = AppVersion.ProductName;
                changed = true;
            }
            if (!doc.ContainsKey("TrashEnabled"))
            {
                doc["TrashEnabled"] = false;
                changed = true;
            }
            if (!doc.ContainsKey("DnsUpdateMode") && doc.ContainsKey("Dynv6Zone"))
            {
                var zone = doc["Dynv6Zone"].IsString ? doc["Dynv6Zone"].AsString : null;
                var key = doc.ContainsKey("Dynv6TsigKeyName") && doc["Dynv6TsigKeyName"].IsString ? doc["Dynv6TsigKeyName"].AsString : null;
                var secret = doc.ContainsKey("Dynv6TsigSecretProtected") && doc["Dynv6TsigSecretProtected"].IsString ? doc["Dynv6TsigSecretProtected"].AsString : null;
                if (!string.IsNullOrWhiteSpace(zone) && !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(secret))
                {
                    doc["DnsUpdateMode"] = "Rfc2136";
                    doc["DnsServer"] = "ns1.dynv6.com";
                    doc["DnsZone"] = zone;
                    doc["DnsTsigKeyName"] = key;
                    doc["DnsTsigAlgorithm"] = "hmac-sha256";
                    doc["DnsTsigSecretProtected"] = secret;
                    doc["DnsAutoUpdateAddress"] = true;
                    changed = true;
                }
            }
            if (changed)
                raw.Update(doc);
        }

        public void Dispose()
        {
            _db?.Dispose();
        }
    }
}

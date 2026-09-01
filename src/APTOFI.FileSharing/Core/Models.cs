using System;
using LiteDB;

namespace APTOFI.FileSharing.Core
{
    internal sealed class UserRecord
    {
        [BsonId]
        public string Id { get; set; }
        public string Email { get; set; }
        public string PasswordHash { get; set; }
        public string Role { get; set; }
        public long QuotaBytes { get; set; }
        public long UsedBytes { get; set; }
        public string Language { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? LastLoginUtc { get; set; }
        public string LastLoginIp { get; set; }
        public bool Enabled { get; set; } = true;
    }

    internal sealed class FolderRecord
    {
        [BsonId]
        public string Id { get; set; }
        public string OwnerId { get; set; }
        public string ParentId { get; set; }
        public string Name { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public bool IsTrashed { get; set; }
        public DateTime? TrashedUtc { get; set; }
        public string TrashRootId { get; set; }
    }

    internal sealed class FileRecord
    {
        [BsonId]
        public string Id { get; set; }
        public string OwnerId { get; set; }
        public string ParentFolderId { get; set; }
        public string OriginalName { get; set; }
        public string OriginalExtension { get; set; }
        public string MimeType { get; set; }
        public string PhysicalRelativePath { get; set; }
        public string StorageLocationId { get; set; }
        public long Size { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public DateTime? ExpiresUtc { get; set; }
        public long DownloadCount { get; set; }
        public string ThumbnailRelativePath { get; set; }
        public bool IsTrashed { get; set; }
        public DateTime? TrashedUtc { get; set; }
        public string TrashRootId { get; set; }
    }

    internal sealed class TrashItemInfo
    {
        public string Type { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public long FileCount { get; set; }
        public long FolderCount { get; set; }
        public DateTime TrashedUtc { get; set; }
        public DateTime DeleteUtc { get; set; }
    }

    internal sealed class FolderStatistics
    {
        public long FileCount { get; set; }
        public long FolderCount { get; set; }
        public long TotalSize { get; set; }
    }

    internal sealed class UploadRecord
    {
        [BsonId]
        public string Id { get; set; }
        public string OwnerId { get; set; }
        public string ParentFolderId { get; set; }
        public string OriginalName { get; set; }
        public string OriginalExtension { get; set; }
        public string MimeType { get; set; }
        public string PhysicalRelativePath { get; set; }
        public string StorageLocationId { get; set; }
        public long ExpectedSize { get; set; }
        public long CurrentOffset { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime LastActivityUtc { get; set; }
        public DateTime? ExpiresUtc { get; set; }
        public string Status { get; set; }
        public string ResumeKey { get; set; }
        public string CompletedFileId { get; set; }
    }

    internal sealed class ShareRecord
    {
        [BsonId]
        public string Id { get; set; }
        public string OwnerId { get; set; }
        public string ResourceType { get; set; }
        public string ResourceId { get; set; }
        public string TokenHash { get; set; }
        public string TokenProtected { get; set; }
        public string PasswordHash { get; set; }
        public DateTime? ExpiresUtc { get; set; }
        public bool Enabled { get; set; } = true;
        public DateTime CreatedUtc { get; set; }
        public long DownloadCount { get; set; }
    }

    internal sealed class SessionRecord
    {
        [BsonId]
        public string TokenHash { get; set; }
        public string UserId { get; set; }
        public string CsrfToken { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string Ip { get; set; }
        public string UserAgent { get; set; }
    }

    internal sealed class DownloadTicketRecord
    {
        [BsonId]
        public string Id { get; set; }
        public string FileId { get; set; }
        public string ShareId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public bool Counted { get; set; }
    }

    internal sealed class BanRecord
    {
        [BsonId]
        public string Ip { get; set; }
        public int Stage { get; set; }
        public DateTime? BanUntilUtc { get; set; }
        public bool Permanent { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string LastPath { get; set; }
        public int TotalSuspiciousPaths { get; set; }
        public string Reason { get; set; }
        public bool Manual { get; set; }
        public DateTime? BlockedUtc { get; set; }
    }
}

using System;
using System.Collections.Generic;
using LiteDB;

namespace APTOFI.FileSharing.Core
{
    internal sealed class AppSettings
    {
        [BsonId]
        public string Id { get; set; } = "main";
        public string StorageRoot { get; set; }
        public List<StorageLocationSetting> StorageLocations { get; set; } = new List<StorageLocationSetting>();
        public string BindAddress { get; set; } = "0.0.0.0";
        public int HttpPort { get; set; } = 15745;
        public int HttpsPort { get; set; } = 15746;
        public bool EnableHttps { get; set; } = true;
        public string PublicMode { get; set; } = "Direct";
        public string PublicBaseUrl { get; set; }
        public string Domain { get; set; }
        public string HttpsIdentifier { get; set; }
        public string CertificateThumbprint { get; set; }
        public string AdminPath { get; set; }
        public string UserPath { get; set; } = "/user_login_disk";
        public string Language { get; set; } = "ru";
        public long ServerQuotaBytes { get; set; }
        public long GlobalUsedBytes { get; set; }
        public bool TrashEnabled { get; set; }
        public int MaxConcurrentTransfers { get; set; } = 64;
        public int UploadLogicalBlockMiB { get; set; } = 32;
        public int IoBufferKiB { get; set; } = 256;
        public bool ServiceInstalled { get; set; }
        public bool TrayAutoStartEnabled { get; set; } = true;
        public string VpsHost { get; set; }
        public int VpsPort { get; set; } = 22;
        public string VpsUser { get; set; }
        public string VpsHostKeyFingerprint { get; set; }
        public string VpsPasswordProtected { get; set; }
        public string VpsPrivateKeyPath { get; set; }
        public string VpsPrivateKeyPassphraseProtected { get; set; }
        public uint VpsRemotePort { get; set; } = 18080;
        public string VpsDomain { get; set; }
        public bool VpsUseSudo { get; set; } = true;
        public string AcmeEmail { get; set; }
        public bool AcmeTermsAccepted { get; set; }
        public string AcmeAccountKid { get; set; }
        public string DnsUpdateMode { get; set; } = "Manual";
        public string DnsServer { get; set; }
        public string DnsZone { get; set; }
        public string DnsTsigKeyName { get; set; }
        public string DnsTsigAlgorithm { get; set; } = "hmac-sha256";
        public string DnsTsigSecretProtected { get; set; }
        public bool DnsAutoUpdateAddress { get; set; }
        public DateTime? LastDnsUpdateUtc { get; set; }
        public string LastDnsError { get; set; }
        public string LastCertificateError { get; set; }
        public string LastVpsError { get; set; }
        public string SiteName { get; set; } = AppVersion.ProductName;
        public string BrandingLogoFileName { get; set; }
        public string BrandingFaviconFileName { get; set; }
    }

    internal sealed class StorageLocationSetting
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public long QuotaBytes { get; set; }
        public long UsedBytes { get; set; }
        public bool Enabled { get; set; } = true;
    }
}

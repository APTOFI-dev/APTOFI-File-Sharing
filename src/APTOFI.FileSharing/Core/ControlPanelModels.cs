using System;
using System.Collections.Generic;

namespace APTOFI.FileSharing.Core
{
    internal sealed class SetupWizardData
    {
        public string StoragePath { get; set; }
        public string Mode { get; set; }
        public string Bind { get; set; }
        public string PublicIp { get; set; }
        public string HttpPort { get; set; }
        public string HttpsPort { get; set; }
        public string AdminPath { get; set; }
        public string UserPath { get; set; }
        public string VpsHost { get; set; }
        public string VpsPort { get; set; }
        public string VpsUser { get; set; }
        public string VpsPassword { get; set; }
        public bool VpsUseSudo { get; set; }
        public string Domain { get; set; }
        public string DnsMode { get; set; }
        public string DnsServer { get; set; }
        public string DnsZone { get; set; }
        public string DnsKeyName { get; set; }
        public string DnsAlgorithm { get; set; }
        public string DnsSecret { get; set; }
        public bool DnsAutoAddress { get; set; }
        public string AcmeEmail { get; set; }
        public bool AcmeTerms { get; set; }
        public string Email { get; set; }
        public string Password { get; set; }
        public string RepeatPassword { get; set; }
        public bool TrayAutoStart { get; set; }
    }

    internal enum ControlHealthLevel
    {
        Unknown,
        Ok,
        Warning,
        Error
    }

    internal sealed class ControlHealthItem
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public ControlHealthLevel Level { get; set; }
        public string Message { get; set; }
        public string Recommendation { get; set; }
    }

    internal sealed class ControlHealthReport
    {
        public DateTime CheckedUtc { get; set; }
        public ControlHealthLevel Level { get; set; }
        public string Summary { get; set; }
        public List<ControlHealthItem> Items { get; set; } = new List<ControlHealthItem>();
    }
}

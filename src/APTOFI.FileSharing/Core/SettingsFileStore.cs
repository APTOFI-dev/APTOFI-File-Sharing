using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace APTOFI.FileSharing.Core
{
    internal static class SettingsFileStore
    {
        public static void Save(AppSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            var snapshot = CreateSnapshot(settings);
            var json = snapshot.ToString(Formatting.None);
            var clear = Encoding.UTF8.GetBytes(json);
            var protectedBytes = ProtectedData.Protect(clear, null, DataProtectionScope.LocalMachine);
            var temp = AppPaths.SettingsFilePath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes, 0, protectedBytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(AppPaths.SettingsFilePath))
            {
                var backup = AppPaths.SettingsFilePath + ".replace";
                if (File.Exists(backup))
                    File.Delete(backup);
                File.Replace(temp, AppPaths.SettingsFilePath, backup, true);
                if (File.Exists(backup))
                    File.Delete(backup);
            }
            else
            {
                File.Move(temp, AppPaths.SettingsFilePath);
            }
            if (!Verify(settings, out var error))
                throw new IOException("The settings file was written but verification failed: " + error);
        }

        public static bool Verify(AppSettings settings, out string error)
        {
            try
            {
                if (settings == null)
                {
                    error = "Settings are not available.";
                    return false;
                }
                if (!File.Exists(AppPaths.SettingsFilePath))
                {
                    error = "Settings file does not exist.";
                    return false;
                }
                var protectedBytes = File.ReadAllBytes(AppPaths.SettingsFilePath);
                var clear = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
                var stored = JObject.Parse(Encoding.UTF8.GetString(clear));
                var expected = CreateSnapshot(settings);
                if (!JToken.DeepEquals(expected, stored))
                {
                    error = "Settings file verification found different durable configuration content.";
                    return false;
                }
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static JObject CreateSnapshot(AppSettings settings)
        {
            var snapshot = JObject.FromObject(settings);
            snapshot.Remove(nameof(AppSettings.GlobalUsedBytes));
            snapshot.Remove(nameof(AppSettings.ServiceInstalled));
            snapshot.Remove(nameof(AppSettings.PublicBaseUrl));
            snapshot.Remove(nameof(AppSettings.VpsHostKeyFingerprint));
            snapshot.Remove(nameof(AppSettings.CertificateThumbprint));
            snapshot.Remove(nameof(AppSettings.AcmeAccountKid));
            snapshot.Remove(nameof(AppSettings.LastDnsUpdateUtc));
            snapshot.Remove(nameof(AppSettings.LastDnsError));
            snapshot.Remove(nameof(AppSettings.LastCertificateError));
            snapshot.Remove(nameof(AppSettings.LastVpsError));
            var locations = snapshot[nameof(AppSettings.StorageLocations)] as JArray;
            if (locations != null)
            {
                foreach (var token in locations)
                {
                    var location = token as JObject;
                    if (location != null)
                        location.Remove(nameof(StorageLocationSetting.UsedBytes));
                }
            }
            return snapshot;
        }
    }
}

using System;
using System.IO;

namespace APTOFI.FileSharing.Core
{
    internal static class AppPaths
    {
        public static readonly string BaseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        public static readonly string DatabasePath = Path.Combine(BaseDirectory, "afsharing.db");
        public static readonly string SettingsFilePath = Path.Combine(BaseDirectory, "afsharing.settings");
        public static readonly string SecurityDirectory = Path.Combine(BaseDirectory, "security");
        public static readonly string MasterKeyPath = Path.Combine(SecurityDirectory, "master.dat");
        public static readonly string AcmeKeyPath = Path.Combine(SecurityDirectory, "acme-account.dat");
        public static readonly string LogsDirectory = Path.Combine(BaseDirectory, "logs");
        public static readonly string CertificatesDirectory = Path.Combine(BaseDirectory, "certificates");
        public static readonly string ThumbnailsDirectory = Path.Combine(BaseDirectory, "thumbnails");
        public static readonly string TempDirectory = Path.Combine(BaseDirectory, "temp");
        public static readonly string WebDirectory = Path.Combine(BaseDirectory, "Web");
        public static readonly string ImagesDirectory = Path.Combine(BaseDirectory, "img");

        public static void EnsureRuntimeDirectories()
        {
            Directory.CreateDirectory(SecurityDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(CertificatesDirectory);
            Directory.CreateDirectory(ThumbnailsDirectory);
            Directory.CreateDirectory(TempDirectory);
            Directory.CreateDirectory(ImagesDirectory);
        }

        public static bool IsBaseDirectoryWritable(out string error)
        {
            try
            {
                Directory.CreateDirectory(BaseDirectory);
                var path = Path.Combine(BaseDirectory, ".afsharing-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(path, "ok");
                File.Delete(path);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}

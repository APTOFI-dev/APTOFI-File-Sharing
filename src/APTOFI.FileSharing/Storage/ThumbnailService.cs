using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;

namespace APTOFI.FileSharing.Storage
{
    internal sealed class ThumbnailService
    {
        private readonly Database _db;
        private readonly StorageService _storage;

        public ThumbnailService(Database db, StorageService storage)
        {
            _db = db;
            _storage = storage;
        }

        public Task QueueAsync(FileRecord file)
        {
            return Task.Run(() => Generate(file));
        }

        private void Generate(FileRecord file)
        {
            try
            {
                if (file == null || file.Size <= 0)
                    return;
                var ext = (file.OriginalExtension ?? string.Empty).ToLowerInvariant();
                string thumb = null;
                if (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".gif" || ext == ".bmp")
                    thumb = GenerateImage(file);
                else if (ext == ".mp4" || ext == ".mkv" || ext == ".mov" || ext == ".webm")
                    thumb = GenerateVideo(file);
                else if (ext == ".pdf")
                    thumb = GeneratePdf(file);
                if (string.IsNullOrWhiteSpace(thumb))
                    return;
                var current = _db.Files.FindById(file.Id);
                if (current == null)
                    return;
                current.ThumbnailRelativePath = thumb;
                _db.Files.Update(current);
            }
            catch
            {
            }
        }

        private string GenerateImage(FileRecord file)
        {
            var source = _storage.ResolvePhysicalPath(file.PhysicalRelativePath, file.StorageLocationId);
            var relative = Path.Combine("thumbnails", file.Id + ".jpg");
            var target = Path.Combine(AppPaths.BaseDirectory, relative);
            using (var image = Image.FromFile(source))
            {
                var max = 320;
                var scale = Math.Min((double)max / image.Width, (double)max / image.Height);
                scale = Math.Min(1.0, scale);
                var width = Math.Max(1, (int)(image.Width * scale));
                var height = Math.Max(1, (int)(image.Height * scale));
                using (var bitmap = new Bitmap(width, height))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(image, 0, 0, width, height);
                    bitmap.Save(target, ImageFormat.Jpeg);
                }
            }
            return relative;
        }

        private string GenerateVideo(FileRecord file)
        {
            var tool = Path.Combine(AppPaths.BaseDirectory, "tools", "ffmpeg.exe");
            if (!File.Exists(tool))
                return null;
            var source = _storage.ResolvePhysicalPath(file.PhysicalRelativePath, file.StorageLocationId);
            var relative = Path.Combine("thumbnails", file.Id + ".jpg");
            var target = Path.Combine(AppPaths.BaseDirectory, relative);
            var args = "-hide_banner -loglevel error -ss 1 -i \"" + source + "\" -frames:v 1 -vf \"scale='min(320,iw)':-2\" -y \"" + target + "\"";
            return Run(tool, args, target) ? relative : null;
        }

        private string GeneratePdf(FileRecord file)
        {
            var tool = Path.Combine(AppPaths.BaseDirectory, "tools", "pdftoppm.exe");
            if (!File.Exists(tool))
                return null;
            var source = _storage.ResolvePhysicalPath(file.PhysicalRelativePath, file.StorageLocationId);
            var baseTarget = Path.Combine(AppPaths.ThumbnailsDirectory, file.Id);
            var output = baseTarget + "-1.jpg";
            var args = "-f 1 -singlefile -jpeg -scale-to 320 \"" + source + "\" \"" + baseTarget + "\"";
            if (!Run(tool, args, output))
                return null;
            var target = Path.Combine(AppPaths.ThumbnailsDirectory, file.Id + ".jpg");
            if (File.Exists(target))
                File.Delete(target);
            File.Move(output, target);
            return Path.Combine("thumbnails", file.Id + ".jpg");
        }

        private static bool Run(string tool, string args, string expected)
        {
            using (var process = Process.Start(new ProcessStartInfo(tool, args) { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (process == null)
                    return false;
                if (!process.WaitForExit(30000))
                {
                    process.Kill();
                    return false;
                }
                return process.ExitCode == 0 && File.Exists(expected);
            }
        }
    }
}

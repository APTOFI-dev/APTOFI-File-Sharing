using System;
using System.Collections.Generic;
using System.IO;

namespace APTOFI.FileSharing.Core
{
    internal static class MimeTypes
    {
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".svg"] = "image/svg+xml",
            [".pdf"] = "application/pdf",
            [".txt"] = "text/plain",
            [".json"] = "application/json",
            [".xml"] = "application/xml",
            [".zip"] = "application/zip",
            [".7z"] = "application/x-7z-compressed",
            [".rar"] = "application/vnd.rar",
            [".mp4"] = "video/mp4",
            [".webm"] = "video/webm",
            [".mkv"] = "video/x-matroska",
            [".mov"] = "video/quicktime",
            [".mp3"] = "audio/mpeg",
            [".wav"] = "audio/wav",
            [".ogg"] = "audio/ogg"
        };

        public static string FromName(string name)
        {
            var ext = Path.GetExtension(name ?? string.Empty);
            return Map.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";
        }
    }
}

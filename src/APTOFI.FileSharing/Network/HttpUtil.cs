using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace APTOFI.FileSharing.Network
{
    internal static class HttpUtil
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Include
        };

        public static async Task<T> ReadJsonAsync<T>(HttpListenerRequest request)
        {
            const int maxBytes = 4 * 1024 * 1024;
            if (request.ContentLength64 > maxBytes)
                throw new InvalidOperationException("JSON request body is too large.");
            var contentType = request.ContentType ?? string.Empty;
            if (!contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("JSON Content-Type is required.");
            var encoding = request.ContentEncoding ?? Encoding.UTF8;
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[8192];
                var total = 0;
                while (true)
                {
                    var read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    total += read;
                    if (total > maxBytes)
                        throw new InvalidOperationException("JSON request body is too large.");
                    memory.Write(buffer, 0, read);
                }
                var text = encoding.GetString(memory.ToArray());
                return string.IsNullOrWhiteSpace(text) ? default(T) : JsonConvert.DeserializeObject<T>(text, JsonSettings);
            }
        }

        public static async Task WriteJsonAsync(HttpListenerResponse response, object value, int statusCode = 200)
        {
            var json = JsonConvert.SerializeObject(value, Formatting.None, JsonSettings);
            var bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        public static async Task WriteTextAsync(HttpListenerResponse response, string text, string contentType, int statusCode = 200)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        public static Task ErrorAsync(HttpListenerResponse response, int statusCode, string code, string message = null, object extra = null)
        {
            return WriteJsonAsync(response, new { error = code, message, extra }, statusCode);
        }

        public static Task NotFoundAsync(HttpListenerResponse response)
        {
            return WriteTextAsync(response, "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>404 Not Found</title><link rel=\"icon\" href=\"/favicon.ico\"><style>body{font-family:Segoe UI,Arial,sans-serif;background:#f5f6f8;color:#252832;display:grid;place-items:center;height:100vh;margin:0}.box{text-align:center}.code{font-size:64px;font-weight:700}.text{font-size:18px;color:#737782}</style></head><body><div class=\"box\"><div class=\"code\">404</div><div class=\"text\">Not Found</div></div></body></html>", "text/html; charset=utf-8", 404);
        }

        public static string RemoteIp(HttpListenerRequest request)
        {
            var remote = request.RemoteEndPoint?.Address;
            var forwarded = request.Headers["X-Forwarded-For"];
            if (remote != null && IPAddress.IsLoopback(remote) && !string.IsNullOrWhiteSpace(forwarded))
                return forwarded.Split(',')[0].Trim();
            return remote?.ToString() ?? "unknown";
        }

        public static string Query(HttpListenerRequest request, string key)
        {
            return request.QueryString[key];
        }

        public static string SafeFileNameHeader(string fileName)
        {
            fileName = RepairLegacyUtf8(fileName ?? "download");
            var asciiName = new StringBuilder();
            foreach (var c in fileName)
            {
                if (c >= 32 && c <= 126 && c != '"' && c != '\\' && c != ';')
                    asciiName.Append(c);
                else if (c == ' ')
                    asciiName.Append(' ');
                else
                    asciiName.Append('_');
            }
            var encoded = Uri.EscapeDataString(fileName);
            return "attachment; filename=\"" + asciiName + "\"; filename*=UTF-8''" + encoded;
        }

        public static string NormalizeSecretPath(string value, string defaultPath)
        {
            var path = string.IsNullOrWhiteSpace(value) ? defaultPath : value.Trim();
            if (!path.StartsWith("/"))
                path = "/" + path;
            while (path.EndsWith("/") && path.Length > 1)
                path = path.Substring(0, path.Length - 1);
            return path;
        }

        public static string RepairLegacyUtf8(string value)
        {
            if (string.IsNullOrEmpty(value) || !LooksLikeLegacyUtf8(value))
                return value;
            var repaired = TryRepair(value, 1251);
            if (!string.IsNullOrEmpty(repaired) && !LooksLikeLegacyUtf8(repaired))
                return repaired;
            repaired = TryRepair(value, 28591);
            if (!string.IsNullOrEmpty(repaired) && !LooksLikeLegacyUtf8(repaired))
                return repaired;
            return value;
        }

        private static string TryRepair(string value, int codePage)
        {
            try
            {
                var source = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                var utf8 = new UTF8Encoding(false, true);
                return utf8.GetString(source.GetBytes(value));
            }
            catch
            {
                return null;
            }
        }

        private static bool LooksLikeLegacyUtf8(string value)
        {
            if (value.IndexOf('Ð') >= 0 || value.IndexOf('Ñ') >= 0 || value.IndexOf('Ã') >= 0)
                return true;
            var markers = new[] { "Рџ", "Р°", "Рµ", "Р»", "Рј", "Рѕ", "Рё", "Р№", "РІ", "Рє", "РЅ", "Рґ", "Р±", "Рі", "Р·", "Р¶", "СЃ", "С‚", "СЂ", "СЏ", "С‹", "С‡", "С€", "С‰", "С†", "СЊ", "СЉ", "СЋ", "С‘" };
            var hits = 0;
            foreach (var marker in markers)
            {
                var index = 0;
                while ((index = value.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
                {
                    hits++;
                    index += marker.Length;
                    if (hits >= 2)
                        return true;
                }
            }
            return false;
        }
    }
}

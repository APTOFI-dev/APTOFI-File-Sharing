using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using APTOFI.FileSharing.Core;

namespace APTOFI.FileSharing.Security
{
    internal sealed class CryptoService
    {
        private readonly byte[] _master;

        public CryptoService()
        {
            AppPaths.EnsureRuntimeDirectories();
            if (!File.Exists(AppPaths.MasterKeyPath))
            {
                var random = new byte[48];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(random);
                var protectedBytes = ProtectedData.Protect(random, null, DataProtectionScope.LocalMachine);
                File.WriteAllBytes(AppPaths.MasterKeyPath, protectedBytes);
            }
            _master = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.MasterKeyPath), null, DataProtectionScope.LocalMachine);
        }

        public string DatabasePassword => Convert.ToBase64String(_master);

        public string RandomToken(int bytes = 32)
        {
            var data = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(data);
            return Base64Url(data);
        }

        public string RandomHex(int bytes)
        {
            var data = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(data);
            var sb = new StringBuilder(data.Length * 2);
            foreach (var b in data)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public string ProtectString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            var bytes = Encoding.UTF8.GetBytes(value);
            return Convert.ToBase64String(ProtectedData.Protect(bytes, _master, DataProtectionScope.LocalMachine));
        }

        public string UnprotectString(string protectedValue)
        {
            if (string.IsNullOrEmpty(protectedValue))
                return null;
            var bytes = Convert.FromBase64String(protectedValue);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, _master, DataProtectionScope.LocalMachine));
        }

        public string CreateShareGrant(string tokenHash, DateTime expiresUtc)
        {
            var payload = tokenHash + "|" + expiresUtc.Ticks;
            var signature = Hmac(payload);
            return Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + Base64Url(signature);
        }

        public bool ValidateShareGrant(string grant, string tokenHash)
        {
            if (string.IsNullOrWhiteSpace(grant))
                return false;
            var parts = grant.Split('.');
            if (parts.Length != 2)
                return false;
            try
            {
                var payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
                var signature = Base64UrlDecode(parts[1]);
                var expected = Hmac(payload);
                if (!FixedTimeEquals(signature, expected))
                    return false;
                var values = payload.Split('|');
                if (values.Length != 2 || !string.Equals(values[0], tokenHash, StringComparison.Ordinal))
                    return false;
                if (!long.TryParse(values[1], out var ticks))
                    return false;
                return new DateTime(ticks, DateTimeKind.Utc) > DateTime.UtcNow;
            }
            catch
            {
                return false;
            }
        }

        public static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] Base64UrlDecode(string value)
        {
            var s = value.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private byte[] Hmac(string payload)
        {
            using (var hmac = new HMACSHA256(_master))
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        }
    }
}

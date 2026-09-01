using Isopoh.Cryptography.Argon2;

namespace APTOFI.FileSharing.Security
{
    internal sealed class PasswordService
    {
        public string Hash(string password)
        {
            return Argon2.Hash(password ?? string.Empty);
        }

        public bool Verify(string hash, string password)
        {
            if (string.IsNullOrWhiteSpace(hash))
                return string.IsNullOrEmpty(password);
            try
            {
                return Argon2.Verify(hash, password ?? string.Empty);
            }
            catch
            {
                return false;
            }
        }
    }
}

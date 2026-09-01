using System;
using System.Net;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;

namespace APTOFI.FileSharing.Security
{
    internal sealed class SessionService
    {
        private readonly Database _db;
        private readonly CryptoService _crypto;

        public SessionService(Database db, CryptoService crypto)
        {
            _db = db;
            _crypto = crypto;
        }

        public SessionRecord Create(UserRecord user, HttpListenerRequest request, HttpListenerResponse response, bool secure)
        {
            var token = _crypto.RandomToken();
            var session = new SessionRecord
            {
                TokenHash = _crypto.Sha256Hex(token),
                UserId = user.Id,
                CsrfToken = _crypto.RandomToken(24),
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddDays(30),
                LastSeenUtc = DateTime.UtcNow,
                Ip = request.RemoteEndPoint?.Address.ToString(),
                UserAgent = request.UserAgent
            };
            _db.Sessions.Upsert(session);
            var cookie = new Cookie("afs_session", token, "/")
            {
                HttpOnly = true,
                Secure = secure,
                Expires = DateTime.UtcNow.AddDays(30)
            };
            response.SetCookie(cookie);
            return session;
        }

        public UserRecord Authenticate(HttpListenerRequest request, out SessionRecord session)
        {
            session = null;
            var cookie = request.Cookies["afs_session"];
            if (cookie == null || string.IsNullOrWhiteSpace(cookie.Value))
                return null;
            var hash = _crypto.Sha256Hex(cookie.Value);
            session = _db.Sessions.FindById(hash);
            if (session == null || session.ExpiresUtc <= DateTime.UtcNow)
            {
                if (session != null)
                    _db.Sessions.Delete(hash);
                session = null;
                return null;
            }
            var user = _db.Users.FindById(session.UserId);
            if (user == null || !user.Enabled)
                return null;
            if ((DateTime.UtcNow - session.LastSeenUtc).TotalMinutes >= 5)
            {
                session.LastSeenUtc = DateTime.UtcNow;
                _db.Sessions.Update(session);
            }
            return user;
        }

        public bool ValidateCsrf(HttpListenerRequest request, SessionRecord session)
        {
            if (session == null)
                return false;
            var value = request.Headers["X-AFS-CSRF"] ?? request.Headers["X-CSRF"];
            return !string.IsNullOrWhiteSpace(value) && string.Equals(value, session.CsrfToken, StringComparison.Ordinal);
        }

        public void Logout(HttpListenerRequest request, HttpListenerResponse response)
        {
            var cookie = request.Cookies["afs_session"];
            if (cookie != null && !string.IsNullOrWhiteSpace(cookie.Value))
                _db.Sessions.Delete(_crypto.Sha256Hex(cookie.Value));
            response.SetCookie(new Cookie("afs_session", string.Empty, "/") { Expires = DateTime.UtcNow.AddDays(-1), HttpOnly = true });
        }

        public void Cleanup()
        {
            _db.Sessions.DeleteMany(x => x.ExpiresUtc <= DateTime.UtcNow);
        }
    }
}

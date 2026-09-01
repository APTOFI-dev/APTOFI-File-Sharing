using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Logging;
using APTOFI.FileSharing.Security;
using APTOFI.FileSharing.Storage;
using Newtonsoft.Json.Linq;

namespace APTOFI.FileSharing.Network
{
    internal sealed class ApiRouter
    {
        private readonly Database _db;
        private readonly CryptoService _crypto;
        private readonly PasswordService _passwords;
        private readonly SessionService _sessions;
        private readonly StorageService _storage;
        private readonly UploadService _uploads;
        private readonly DownloadService _downloads;
        private readonly ShareService _shares;
        private readonly AntiScannerService _antiScanner;
        private readonly DiagnosticsService _diagnostics;
        private readonly WindowsNetworkService _windows;
        private readonly AcmeService _acme;
        private readonly VpsTunnelService _vps;
        private readonly LogService _log;
        private readonly Func<Task> _restartWeb;
        private readonly Action _restartVps;

        public ApiRouter(Database db, CryptoService crypto, PasswordService passwords, SessionService sessions, StorageService storage, UploadService uploads, DownloadService downloads, ShareService shares, AntiScannerService antiScanner, DiagnosticsService diagnostics, WindowsNetworkService windows, AcmeService acme, VpsTunnelService vps, LogService log, Func<Task> restartWeb, Action restartVps)
        {
            _db = db;
            _crypto = crypto;
            _passwords = passwords;
            _sessions = sessions;
            _storage = storage;
            _uploads = uploads;
            _downloads = downloads;
            _shares = shares;
            _antiScanner = antiScanner;
            _diagnostics = diagnostics;
            _windows = windows;
            _acme = acme;
            _vps = vps;
            _log = log;
            _restartWeb = restartWeb;
            _restartVps = restartVps;
        }

        public async Task<RequestResult> HandleAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            var path = request.Url.AbsolutePath;
            var settings = _db.GetSettings();
            var adminPath = HttpUtil.NormalizeSecretPath(settings.AdminPath, "/admin_secret");
            var userPath = HttpUtil.NormalizeSecretPath(settings.UserPath, "/user_login_disk");
            var normalizedPath = path.Length > 1 ? path.TrimEnd('/') : path;

            if (request.HttpMethod == "GET" && normalizedPath == "/")
            {
                response.StatusCode = 302;
                response.RedirectLocation = userPath;
                response.ContentLength64 = 0;
                return Result(302);
            }
            if (request.HttpMethod == "GET" && (normalizedPath == adminPath || normalizedPath == userPath))
                return await ServeAppAsync(response, normalizedPath == adminPath ? "admin" : "user").ConfigureAwait(false);
            if (request.HttpMethod == "POST" && path == adminPath + "/login")
                return await LoginAsync(context, true).ConfigureAwait(false);
            if (request.HttpMethod == "POST" && path == userPath + "/login")
                return await LoginAsync(context, false).ConfigureAwait(false);
            if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                return await ServeAssetAsync(response, path).ConfigureAwait(false);
            if (path == "/branding/logo")
                return await ServeBrandingFileAsync(response, "logo").ConfigureAwait(false);
            if (path == "/favicon.ico")
                return await ServeBrandingFileAsync(response, "favicon").ConfigureAwait(false);
            if (path.StartsWith("/d/", StringComparison.OrdinalIgnoreCase))
                return await HandleShareAsync(context).ConfigureAwait(false);
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                return await HandleApiAsync(context).ConfigureAwait(false);
            await HttpUtil.NotFoundAsync(response).ConfigureAwait(false);
            return new RequestResult { StatusCode = 404, UnknownPath = true };
        }

        private async Task<RequestResult> LoginAsync(HttpListenerContext context, bool adminOnly)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var email = ((string)body?["email"] ?? string.Empty).Trim().ToLowerInvariant();
            var password = (string)body?["password"] ?? string.Empty;
            var user = _db.Users.FindOne(x => x.Email == email);
            var roleOk = user != null && (!adminOnly || user.Role == "admin");
            if (!roleOk || !user.Enabled || !_passwords.Verify(user.PasswordHash, password))
            {
                _antiScanner.RecordFailedLogin(HttpUtil.RemoteIp(context.Request), context.Request.Url.AbsolutePath, email);
                await HttpUtil.ErrorAsync(context.Response, 401, "invalid_credentials", "Invalid email or password.").ConfigureAwait(false);
                return Result(401);
            }
            user.LastLoginUtc = DateTime.UtcNow;
            user.LastLoginIp = HttpUtil.RemoteIp(context.Request);
            _db.Users.Update(user);
            var session = _sessions.Create(user, context.Request, context.Response, IsSecureExternal(context.Request));
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true, csrf = session.CsrfToken, role = user.Role }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> HandleApiAsync(HttpListenerContext context)
        {
            var path = context.Request.Url.AbsolutePath;
            var method = context.Request.HttpMethod;
            var user = _sessions.Authenticate(context.Request, out var session);
            if (user == null)
            {
                await HttpUtil.ErrorAsync(context.Response, 401, "not_authenticated").ConfigureAwait(false);
                return Result(401);
            }
            var write = method == "POST" || method == "PUT" || method == "DELETE" || method == "PATCH";
            if (write && !_sessions.ValidateCsrf(context.Request, session))
            {
                await HttpUtil.ErrorAsync(context.Response, 403, "csrf_failed").ConfigureAwait(false);
                return Result(403, user.Id);
            }

            try
            {
                if (method == "GET" && path == "/api/state")
                    return await StateAsync(context, user, session).ConfigureAwait(false);
                if (method == "POST" && path == "/api/logout")
                {
                    _sessions.Logout(context.Request, context.Response);
                    await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
                    return Result(200, user.Id);
                }
                if (method == "GET" && path == "/api/items")
                    return await ItemsAsync(context, user).ConfigureAwait(false);
                if (method == "POST" && path == "/api/folders")
                    return await CreateFolderAsync(context, user).ConfigureAwait(false);
                if (method == "POST" && path == "/api/items/rename")
                    return await RenameAsync(context, user).ConfigureAwait(false);
                if (method == "POST" && path == "/api/items/move")
                    return await MoveAsync(context, user).ConfigureAwait(false);
                if (method == "POST" && path == "/api/items/delete")
                    return await DeleteAsync(context, user).ConfigureAwait(false);
                if (method == "GET" && path == "/api/trash")
                    return await TrashListAsync(context, user).ConfigureAwait(false);
                if (method == "POST" && path == "/api/trash/restore")
                    return await TrashRestoreAsync(context, user).ConfigureAwait(false);
                if (method == "POST" && path == "/api/trash/delete")
                    return await TrashDeleteAsync(context, user).ConfigureAwait(false);
                if (method == "POST" && path == "/api/trash/empty")
                    return await TrashEmptyAsync(context, user).ConfigureAwait(false);
                if (method == "POST" && path == "/api/uploads/start")
                    return await StartUploadAsync(context, user).ConfigureAwait(false);
                if (path.StartsWith("/api/uploads/", StringComparison.OrdinalIgnoreCase))
                    return await HandleUploadAsync(context, user, path).ConfigureAwait(false);
                if (method == "GET" && path.StartsWith("/api/files/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/download", StringComparison.OrdinalIgnoreCase))
                    return await PrivateDownloadAsync(context, user, path).ConfigureAwait(false);
                if (method == "GET" && path.StartsWith("/api/folders/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/properties", StringComparison.OrdinalIgnoreCase))
                    return await PrivateFolderPropertiesAsync(context, user, path).ConfigureAwait(false);
                if (method == "GET" && path.StartsWith("/api/folders/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/download", StringComparison.OrdinalIgnoreCase))
                    return await PrivateFolderDownloadAsync(context, user, path).ConfigureAwait(false);
                if (method == "POST" && path == "/api/downloads/archive")
                    return await CreateArchiveDownloadAsync(context, user).ConfigureAwait(false);
                if (method == "GET" && path.StartsWith("/api/downloads/archive/", StringComparison.OrdinalIgnoreCase))
                    return await PrivateArchiveDownloadAsync(context, user, path).ConfigureAwait(false);
                if (method == "GET" && path.StartsWith("/api/thumbnail/", StringComparison.OrdinalIgnoreCase))
                    return await PrivateThumbnailAsync(context, user, path).ConfigureAwait(false);
                if (path == "/api/shares" && method == "POST")
                    return await CreateShareAsync(context, user).ConfigureAwait(false);
                if (path == "/api/shares" && method == "GET")
                    return await ListSharesAsync(context, user).ConfigureAwait(false);
                if (path.StartsWith("/api/shares/", StringComparison.OrdinalIgnoreCase))
                    return await ShareMutationAsync(context, user, path).ConfigureAwait(false);
                if (path == "/api/account/email" && method == "POST")
                    return await ChangeOwnEmailAsync(context, user).ConfigureAwait(false);
                if (path == "/api/account/password" && method == "POST")
                    return await ChangeOwnPasswordAsync(context, user).ConfigureAwait(false);
                if (path == "/api/account/language" && method == "POST")
                    return await ChangeOwnLanguageAsync(context, user).ConfigureAwait(false);
                if (path.StartsWith("/api/admin/", StringComparison.OrdinalIgnoreCase))
                    return await HandleAdminAsync(context, user, path).ConfigureAwait(false);
            }
            catch (QuotaException ex)
            {
                _log.App("quota-block user=" + user.Id + " code=" + ex.Code + " availableBytes=" + ex.AvailableBytes);
                await HttpUtil.ErrorAsync(context.Response, 409, ex.Code, null, new { availableBytes = ex.AvailableBytes }).ConfigureAwait(false);
                return Result(409, user.Id);
            }
            catch (OffsetMismatchException ex)
            {
                await HttpUtil.ErrorAsync(context.Response, 409, "offset_mismatch", null, new { expectedOffset = ex.ExpectedOffset }).ConfigureAwait(false);
                return Result(409, user.Id);
            }
            catch (FileNotFoundException)
            {
                await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                return Result(404, user.Id);
            }
            catch (DirectoryNotFoundException)
            {
                await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                return Result(404, user.Id);
            }
            catch (UnauthorizedAccessException)
            {
                await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                return Result(404, user.Id);
            }
            catch (HttpListenerException ex)
            {
                _log.App("transport-interrupted path=" + path + " user=" + user.Id + " code=" + ex.ErrorCode + " message=" + ex.Message);
                throw;
            }
            catch (IOException ex)
            {
                _log.App("api-io-error path=" + path + " user=" + user.Id + " message=" + ex.Message);
                await HttpUtil.ErrorAsync(context.Response, 503, "storage_io_error", user.Role == "admin" ? ex.Message : null).ConfigureAwait(false);
                return Result(503, user.Id);
            }
            catch (Exception ex)
            {
                _log.App("api-error path=" + path + " user=" + user.Id + " type=" + ex.GetType().Name + " message=" + ex.Message);
                await HttpUtil.ErrorAsync(context.Response, 400, "request_failed", user.Role == "admin" ? ex.Message : null).ConfigureAwait(false);
                return Result(400, user.Id);
            }

            await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
            return new RequestResult { StatusCode = 404, UserId = user.Id, UnknownPath = true };
        }

        private async Task<RequestResult> StateAsync(HttpListenerContext context, UserRecord user, SessionRecord session)
        {
            var settings = _db.GetSettings();
            await HttpUtil.WriteJsonAsync(context.Response, new
            {
                product = AppVersion.ProductName,
                version = AppVersion.Version,
                author = AppVersion.Author,
                authorUrl = AppVersion.AuthorUrl,
                role = user.Role,
                userId = user.Id,
                email = user.Email,
                language = user.Language ?? settings.Language,
                quotaBytes = user.QuotaBytes,
                usedBytes = user.UsedBytes,
                trashEnabled = settings.TrashEnabled,
                trashBytes = settings.TrashEnabled ? _storage.GetTrashBytes(user.Id) : 0,
                serverQuotaBytes = user.Role == "admin" ? settings.ServerQuotaBytes : 0,
                globalUsedBytes = user.Role == "admin" ? settings.GlobalUsedBytes : 0,
                publicBaseUrl = settings.PublicBaseUrl,
                publicMode = settings.PublicMode,
                publicReady = IsPublicAccessReady(settings),
                userLoginUrl = user.Role == "admin" && IsPublicAccessReady(settings) && !string.IsNullOrWhiteSpace(settings.PublicBaseUrl) ? settings.PublicBaseUrl.TrimEnd('/') + HttpUtil.NormalizeSecretPath(settings.UserPath, "/user_login_disk") : null,
                csrf = session.CsrfToken
            }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> ItemsAsync(HttpListenerContext context, UserRecord user)
        {
            var owner = user.Id;
            var folderId = NullIfEmpty(context.Request.QueryString["folder"]);
            string parentId = null;
            string folderName = null;
            if (folderId != null)
            {
                var folder = _db.Folders.FindById(folderId);
                if (folder == null || folder.IsTrashed || folder.OwnerId != owner)
                    throw new DirectoryNotFoundException();
                var repairedFolderName = HttpUtil.RepairLegacyUtf8(folder.Name);
                if (!string.Equals(repairedFolderName, folder.Name, StringComparison.Ordinal))
                {
                    folder.Name = repairedFolderName;
                    _db.Folders.Update(folder);
                }
                parentId = folder.ParentId;
                folderName = folder.Name;
            }
            var folderRecords = _storage.ChildFolders(owner, folderId).ToList();
            foreach (var folder in folderRecords)
            {
                var repairedName = HttpUtil.RepairLegacyUtf8(folder.Name);
                if (!string.Equals(repairedName, folder.Name, StringComparison.Ordinal))
                {
                    folder.Name = repairedName;
                    _db.Folders.Update(folder);
                }
            }
            var fileRecords = _storage.ChildFiles(owner, folderId).Where(x => !x.ExpiresUtc.HasValue || x.ExpiresUtc.Value > DateTime.UtcNow).ToList();
            foreach (var file in fileRecords)
            {
                var repairedName = HttpUtil.RepairLegacyUtf8(file.OriginalName);
                if (!string.Equals(repairedName, file.OriginalName, StringComparison.Ordinal))
                {
                    file.OriginalName = repairedName;
                    file.OriginalExtension = Path.GetExtension(repairedName);
                    file.MimeType = MimeTypes.FromName(repairedName);
                    _db.Files.Update(file);
                }
            }
            var folders = folderRecords.Select(x => new { type = "folder", x.Id, name = x.Name, modifiedUtc = x.ModifiedUtc }).ToList();
            var files = fileRecords.Select(x => new
            {
                type = "file",
                x.Id,
                name = x.OriginalName,
                x.Size,
                x.MimeType,
                modifiedUtc = x.ModifiedUtc,
                expiresUtc = x.ExpiresUtc,
                downloads = x.DownloadCount,
                thumbnail = string.IsNullOrWhiteSpace(x.ThumbnailRelativePath) ? null : "/api/thumbnail/" + x.Id
            }).ToList();
            await HttpUtil.WriteJsonAsync(context.Response, new { ownerId = owner, folderId, folderName, parentId, folders, files }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> CreateFolderAsync(HttpListenerContext context, UserRecord user)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var folder = _storage.CreateFolder(user, (string)body?["parentId"], (string)body?["name"]);
            await HttpUtil.WriteJsonAsync(context.Response, folder, 201).ConfigureAwait(false);
            return Result(201, user.Id);
        }

        private async Task<RequestResult> RenameAsync(HttpListenerContext context, UserRecord user)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            _storage.Rename(user, (string)body?["type"], (string)body?["id"], (string)body?["name"]);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> MoveAsync(HttpListenerContext context, UserRecord user)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            _storage.Move(user, (string)body?["type"], (string)body?["id"], (string)body?["parentId"]);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> DeleteAsync(HttpListenerContext context, UserRecord user)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var settings = _db.GetSettings();
            var trashed = settings != null && settings.TrashEnabled;
            _storage.Delete(user, (string)body?["type"], (string)body?["id"]);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true, trashed }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> TrashListAsync(HttpListenerContext context, UserRecord user)
        {
            var items = _storage.GetTrash(user);
            await HttpUtil.WriteJsonAsync(context.Response, new
            {
                retentionDays = 30,
                usedBytes = _storage.GetTrashBytes(user.Id),
                items
            }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> TrashRestoreAsync(HttpListenerContext context, UserRecord user)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            _storage.RestoreTrash(user, (string)body?["type"], (string)body?["id"]);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> TrashDeleteAsync(HttpListenerContext context, UserRecord user)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var freedBytes = _storage.DeleteTrashPermanently(user, (string)body?["type"], (string)body?["id"]);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true, freedBytes }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> TrashEmptyAsync(HttpListenerContext context, UserRecord user)
        {
            var freedBytes = _storage.EmptyTrash(user);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true, freedBytes }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> StartUploadAsync(HttpListenerContext context, UserRecord user)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var upload = _uploads.Start(user, (string)body?["parentId"], (string)body?["name"], ValueLong(body?["size"]), ValueDate(body?["expiresUtc"]), (string)body?["resumeKey"]);
            var settings = _db.GetSettings();
            await HttpUtil.WriteJsonAsync(context.Response, new { uploadId = upload.Id, offset = upload.CurrentOffset, blockBytes = settings.UploadLogicalBlockMiB * 1024L * 1024L }, 201).ConfigureAwait(false);
            return Result(201, user.Id);
        }

        private async Task<RequestResult> HandleUploadAsync(HttpListenerContext context, UserRecord user, string path)
        {
            var tail = path.Substring("/api/uploads/".Length);
            var parts = tail.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                throw new FileNotFoundException();
            var id = parts[0];
            if (context.Request.HttpMethod == "GET" && parts.Length == 1)
            {
                var upload = _uploads.Get(user, id);
                await HttpUtil.WriteJsonAsync(context.Response, new { uploadId = upload.Id, offset = upload.CurrentOffset, size = upload.ExpectedSize, status = upload.Status, fileId = upload.CompletedFileId }).ConfigureAwait(false);
                return Result(200, user.Id);
            }
            if (context.Request.HttpMethod == "PUT" && parts.Length == 2 && parts[1] == "chunk")
            {
                var offsetText = context.Request.Headers["X-Upload-Offset"] ?? context.Request.QueryString["offset"];
                if (!long.TryParse(offsetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
                    throw new InvalidOperationException("Upload offset is missing.");
                var upload = await _uploads.WriteAsync(user, id, offset, context.Request).ConfigureAwait(false);
                await HttpUtil.WriteJsonAsync(context.Response, new { uploadId = upload.Id, offset = upload.CurrentOffset, size = upload.ExpectedSize }).ConfigureAwait(false);
                return Result(200, user.Id, upload.CurrentOffset - offset);
            }
            if (context.Request.HttpMethod == "POST" && parts.Length == 2 && parts[1] == "complete")
            {
                var file = _uploads.Complete(user, id);
                await HttpUtil.WriteJsonAsync(context.Response, new { ok = true, fileId = file.Id, name = file.OriginalName, size = file.Size }).ConfigureAwait(false);
                return Result(200, user.Id);
            }
            if (context.Request.HttpMethod == "DELETE" && parts.Length == 1)
            {
                _uploads.Cancel(user, id);
                await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
                return Result(200, user.Id);
            }
            throw new FileNotFoundException();
        }

        private async Task<RequestResult> PrivateDownloadAsync(HttpListenerContext context, UserRecord user, string path)
        {
            var id = path.Substring("/api/files/".Length);
            id = id.Substring(0, id.Length - "/download".Length).Trim('/');
            var file = _storage.GetFileForActor(user, id);
            if (!await _downloads.EnsureTicketOrRedirectAsync(context, file, null).ConfigureAwait(false))
                return Result(302, user.Id);
            var bytes = await _downloads.SendFileAsync(context, file, null).ConfigureAwait(false);
            return Result(context.Response.StatusCode, user.Id, bytes);
        }

        private async Task<RequestResult> PrivateFolderPropertiesAsync(HttpListenerContext context, UserRecord user, string path)
        {
            var id = path.Substring("/api/folders/".Length);
            id = id.Substring(0, id.Length - "/properties".Length).Trim('/');
            var folder = _storage.GetFolderForActor(user, id);
            var stats = _storage.GetFolderStatistics(user, id);
            await HttpUtil.WriteJsonAsync(context.Response, new
            {
                id = folder.Id,
                name = HttpUtil.RepairLegacyUtf8(folder.Name),
                modifiedUtc = folder.ModifiedUtc,
                fileCount = stats.FileCount,
                folderCount = stats.FolderCount,
                totalSize = stats.TotalSize
            }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> PrivateFolderDownloadAsync(HttpListenerContext context, UserRecord user, string path)
        {
            var id = path.Substring("/api/folders/".Length);
            id = id.Substring(0, id.Length - "/download".Length).Trim('/');
            var bytes = await _shares.DownloadPrivateFolderZipAsync(context, user, id).ConfigureAwait(false);
            return Result(200, user.Id, bytes);
        }

        private async Task<RequestResult> CreateArchiveDownloadAsync(HttpListenerContext context, UserRecord user)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var array = body?["items"] as JArray;
            if (array == null || array.Count == 0)
                throw new InvalidOperationException("Archive selection is empty.");
            var items = new List<ArchiveSelectionItem>();
            foreach (var token in array)
            {
                var item = token as JObject;
                if (item == null)
                    throw new InvalidOperationException("Invalid archive item.");
                items.Add(new ArchiveSelectionItem
                {
                    Type = (string)item["type"],
                    Id = (string)item["id"]
                });
            }
            var ticket = _shares.CreatePrivateArchiveTicket(user, items);
            await HttpUtil.WriteJsonAsync(context.Response, new { url = "/api/downloads/archive/" + Uri.EscapeDataString(ticket) }, 201).ConfigureAwait(false);
            return Result(201, user.Id);
        }

        private async Task<RequestResult> PrivateArchiveDownloadAsync(HttpListenerContext context, UserRecord user, string path)
        {
            var token = path.Substring("/api/downloads/archive/".Length).Trim('/');
            var bytes = await _shares.DownloadPrivateArchiveAsync(context, user, token).ConfigureAwait(false);
            return Result(200, user.Id, bytes);
        }

        private async Task<RequestResult> PrivateThumbnailAsync(HttpListenerContext context, UserRecord user, string path)
        {
            var id = path.Substring("/api/thumbnail/".Length).Trim('/');
            var file = _storage.GetFileForActor(user, id);
            return await SendThumbnailAsync(context, file, user.Id).ConfigureAwait(false);
        }

        private async Task<RequestResult> CreateShareAsync(HttpListenerContext context, UserRecord user)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var result = _shares.Create(user, (string)body?["type"], (string)body?["id"], (string)body?["password"], ValueDate(body?["expiresUtc"]));
            await HttpUtil.WriteJsonAsync(context.Response, result, 201).ConfigureAwait(false);
            return Result(201, user.Id);
        }

        private async Task<RequestResult> ListSharesAsync(HttpListenerContext context, UserRecord user)
        {
            var list = _shares.List(user, context.Request.QueryString["type"], context.Request.QueryString["id"]);
            await HttpUtil.WriteJsonAsync(context.Response, list).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> ShareMutationAsync(HttpListenerContext context, UserRecord user, string path)
        {
            var tail = path.Substring("/api/shares/".Length).Trim('/');
            var parts = tail.Split('/');
            if (parts.Length == 2 && parts[1] == "regenerate" && context.Request.HttpMethod == "POST")
            {
                var result = _shares.Regenerate(user, parts[0]);
                await HttpUtil.WriteJsonAsync(context.Response, result).ConfigureAwait(false);
                return Result(200, user.Id);
            }
            if (parts.Length == 1 && context.Request.HttpMethod == "DELETE")
            {
                _shares.Delete(user, parts[0]);
                await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
                return Result(200, user.Id);
            }
            throw new FileNotFoundException();
        }

        private async Task<RequestResult> ChangeOwnEmailAsync(HttpListenerContext context, UserRecord user)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var password = (string)body?["password"] ?? string.Empty;
            var email = ((string)body?["email"] ?? string.Empty).Trim().ToLowerInvariant();
            if (!_passwords.Verify(user.PasswordHash, password))
                throw new UnauthorizedAccessException();
            ValidateEmail(email);
            if (_db.Users.Exists(x => x.Email == email && x.Id != user.Id))
                throw new InvalidOperationException("Email is already used.");
            user.Email = email;
            _db.Users.Update(user);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true, email }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> ChangeOwnPasswordAsync(HttpListenerContext context, UserRecord user)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var current = (string)body?["currentPassword"] ?? string.Empty;
            var next = (string)body?["newPassword"] ?? string.Empty;
            if (!_passwords.Verify(user.PasswordHash, current))
                throw new UnauthorizedAccessException();
            ValidatePassword(next);
            user.PasswordHash = _passwords.Hash(next);
            _db.Users.Update(user);
            var sessionCookie = context.Request.Cookies["afs_session"];
            var sessionToken = sessionCookie == null ? string.Empty : sessionCookie.Value ?? string.Empty;
            var currentTokenHash = _crypto.Sha256Hex(sessionToken);
            var userId = user.Id;
            _db.Sessions.DeleteMany(x => x.UserId == userId && x.TokenHash != currentTokenHash);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
            return Result(200, user.Id);
        }


        private async Task<RequestResult> ChangeOwnLanguageAsync(HttpListenerContext context, UserRecord user)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var language = ((string)body?["language"] ?? string.Empty).Trim().ToLowerInvariant();
            var allowed = new[] { "ru", "en", "de", "uk", "ko", "zh", "ja", "fr", "pl", "tr" };
            if (!allowed.Contains(language))
                throw new InvalidOperationException("Unsupported language.");
            user.Language = language;
            _db.Users.Update(user);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true, language }).ConfigureAwait(false);
            return Result(200, user.Id);
        }

        private async Task<RequestResult> HandleAdminAsync(HttpListenerContext context, UserRecord user, string path)
        {
            if (user.Role != "admin")
            {
                await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                return Result(404, user.Id);
            }
            var method = context.Request.HttpMethod;
            if (path == "/api/admin/users" && method == "GET")
                return await AdminUsersAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/users" && method == "POST")
                return await AdminCreateUserAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/users/update" && method == "POST")
                return await AdminUpdateUserAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/users/password" && method == "POST")
                return await AdminPasswordAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/users/toggle" && method == "POST")
                return await AdminToggleAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/users/delete" && method == "POST")
                return await AdminDeleteUserAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/settings" && method == "GET")
                return await AdminGetSettingsAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/settings" && method == "POST")
                return await AdminSaveSettingsAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/diagnostics" && method == "POST")
                return await AdminDiagnosticsAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/diagnostics/export" && method == "POST")
                return await AdminDiagnosticsExportAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/security/bans" && method == "GET")
            {
                await HttpUtil.WriteJsonAsync(context.Response, _antiScanner.GetAll()).ConfigureAwait(false);
                return Result(200, user.Id);
            }
            if (path == "/api/admin/security/events" && method == "GET")
            {
                await HttpUtil.WriteJsonAsync(context.Response, _log.ReadRecent(200)).ConfigureAwait(false);
                return Result(200, user.Id);
            }
            if (path == "/api/admin/security/block" && method == "POST")
            {
                var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
                var targetIp = ((string)body?["ip"] ?? string.Empty).Trim();
                if (string.Equals(targetIp, HttpUtil.RemoteIp(context.Request), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The current administrator IP cannot be blocked from its own active session.");
                _antiScanner.Block(targetIp, body?["minutes"]?.Value<int>() ?? 0, (string)body?["reason"]);
                await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
                return Result(200, user.Id);
            }
            if (path == "/api/admin/security/unblock" && method == "POST")
            {
                var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
                _antiScanner.Unblock((string)body?["ip"]);
                await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
                return Result(200, user.Id);
            }
            if (path == "/api/admin/security/reset" && method == "POST")
            {
                var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
                _antiScanner.ResetHistory((string)body?["ip"]);
                await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
                return Result(200, user.Id);
            }
            if (path == "/api/admin/branding" && method == "GET")
                return await AdminBrandingAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/branding/name" && method == "POST")
                return await AdminBrandingNameAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/branding/upload" && method == "POST")
                return await AdminUploadBrandingAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/branding/delete" && method == "POST")
                return await AdminDeleteBrandingAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/certificate/issue" && method == "POST")
                return await AdminIssueCertificateAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/dns/test" && method == "POST")
                return await AdminDnsTestAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/vps/test" && method == "POST")
                return await AdminVpsTestAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/vps/configure" && method == "POST")
                return await AdminVpsConfigureAsync(context, user).ConfigureAwait(false);
            if (path == "/api/admin/vps/status" && method == "GET")
            {
                await HttpUtil.WriteJsonAsync(context.Response, _vps.Status).ConfigureAwait(false);
                return Result(200, user.Id);
            }
            throw new FileNotFoundException();
        }

        private async Task<RequestResult> AdminUsersAsync(HttpListenerContext context, UserRecord admin)
        {
            var users = _db.Users.FindAll().OrderBy(x => x.Email).Select(x => new
            {
                x.Id,
                x.Email,
                x.Role,
                x.QuotaBytes,
                x.UsedBytes,
                x.Language,
                x.Enabled,
                x.CreatedUtc,
                x.LastLoginUtc,
                x.LastLoginIp
            }).ToList();
            await HttpUtil.WriteJsonAsync(context.Response, users).ConfigureAwait(false);
            return Result(200, admin.Id);
        }

        private async Task<RequestResult> AdminCreateUserAsync(HttpListenerContext context, UserRecord admin)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var email = ((string)body?["email"] ?? string.Empty).Trim().ToLowerInvariant();
            var password = (string)body?["password"] ?? string.Empty;
            ValidateEmail(email);
            ValidatePassword(password);
            if (_db.Users.Exists(x => x.Email == email))
                throw new InvalidOperationException("Email is already used.");
            var user = new UserRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Email = email,
                PasswordHash = _passwords.Hash(password),
                Role = "user",
                QuotaBytes = ValueLong(body?["quotaBytes"]),
                Language = (string)body?["language"] ?? "ru",
                CreatedUtc = DateTime.UtcNow,
                Enabled = true
            };
            _db.Users.Insert(user);
            await HttpUtil.WriteJsonAsync(context.Response, new { user.Id, user.Email, user.QuotaBytes }, 201).ConfigureAwait(false);
            return Result(201, admin.Id);
        }

        private async Task<RequestResult> AdminUpdateUserAsync(HttpListenerContext context, UserRecord admin)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var id = (string)body?["id"];
            var target = _db.Users.FindById(id);
            if (target == null)
                throw new FileNotFoundException();
            var email = ((string)body?["email"] ?? target.Email).Trim().ToLowerInvariant();
            ValidateEmail(email);
            if (_db.Users.Exists(x => x.Email == email && x.Id != target.Id))
                throw new InvalidOperationException("Email is already used.");
            target.Email = email;
            if (body?["quotaBytes"] != null)
                target.QuotaBytes = ValueLong(body["quotaBytes"]);
            if (body?["language"] != null)
                target.Language = (string)body["language"];
            _db.Users.Update(target);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
            return Result(200, admin.Id);
        }

        private async Task<RequestResult> AdminPasswordAsync(HttpListenerContext context, UserRecord admin)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var target = _db.Users.FindById((string)body?["id"]);
            if (target == null)
                throw new FileNotFoundException();
            var password = (string)body?["password"] ?? string.Empty;
            ValidatePassword(password);
            target.PasswordHash = _passwords.Hash(password);
            _db.Users.Update(target);
            _db.Sessions.DeleteMany(x => x.UserId == target.Id);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
            return Result(200, admin.Id);
        }

        private async Task<RequestResult> AdminToggleAsync(HttpListenerContext context, UserRecord admin)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var target = _db.Users.FindById((string)body?["id"]);
            if (target == null || target.Role == "admin")
                throw new FileNotFoundException();
            target.Enabled = body?["enabled"]?.Value<bool>() ?? !target.Enabled;
            _db.Users.Update(target);
            if (!target.Enabled)
                _db.Sessions.DeleteMany(x => x.UserId == target.Id);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true, target.Enabled }).ConfigureAwait(false);
            return Result(200, admin.Id);
        }

        private async Task<RequestResult> AdminDeleteUserAsync(HttpListenerContext context, UserRecord admin)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var target = _db.Users.FindById((string)body?["id"]);
            if (target == null || string.Equals(target.Role, "admin", StringComparison.OrdinalIgnoreCase) || target.Id == admin.Id)
                throw new FileNotFoundException();
            target.Enabled = false;
            _db.Users.Update(target);
            _db.Sessions.DeleteMany(x => x.UserId == target.Id);
            _shares.RevokePrivateArchiveTickets(target.Id);
            _uploads.PurgeOwnerUploads(target.Id);
            var releasedBytes = _storage.PurgeOwnerData(target.Id);
            _db.Shares.DeleteMany(x => x.OwnerId == target.Id);
            _db.Sessions.DeleteMany(x => x.UserId == target.Id);
            _db.Users.Delete(target.Id);
            _log.Security("admin-user-deleted actor=" + admin.Id + " target=" + target.Id + " email=" + target.Email + " releasedBytes=" + releasedBytes);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true, releasedBytes = releasedBytes }).ConfigureAwait(false);
            return Result(200, admin.Id);
        }

        private async Task<RequestResult> AdminGetSettingsAsync(HttpListenerContext context, UserRecord admin)
        {
            var s = _db.GetSettings();
            var locations = StorageService.GetLocations(s).Select(x => new { x.Id, x.Path, x.QuotaBytes, x.UsedBytes, x.Enabled }).ToList();
            await HttpUtil.WriteJsonAsync(context.Response, new
            {
                storageLocations = locations,
                s.BindAddress,
                s.HttpPort,
                s.HttpsPort,
                s.EnableHttps,
                s.PublicMode,
                s.PublicBaseUrl,
                s.Domain,
                s.HttpsIdentifier,
                s.CertificateThumbprint,
                s.AdminPath,
                s.UserPath,
                s.Language,
                s.ServerQuotaBytes,
                s.GlobalUsedBytes,
                s.TrashEnabled,
                s.MaxConcurrentTransfers,
                s.UploadLogicalBlockMiB,
                s.IoBufferKiB,
                s.VpsHost,
                s.VpsPort,
                s.VpsUser,
                s.VpsHostKeyFingerprint,
                vpsHasPassword = !string.IsNullOrWhiteSpace(s.VpsPasswordProtected),
                s.VpsPrivateKeyPath,
                vpsHasPrivateKeyPassphrase = !string.IsNullOrWhiteSpace(s.VpsPrivateKeyPassphraseProtected),
                s.VpsRemotePort,
                s.VpsDomain,
                s.VpsUseSudo,
                s.AcmeEmail,
                s.AcmeTermsAccepted,
                s.DnsUpdateMode,
                s.DnsServer,
                s.DnsZone,
                s.DnsTsigKeyName,
                s.DnsTsigAlgorithm,
                dnsHasSecret = !string.IsNullOrWhiteSpace(s.DnsTsigSecretProtected),
                s.DnsAutoUpdateAddress,
                s.LastDnsUpdateUtc,
                s.LastDnsError,
                s.LastCertificateError,
                s.LastVpsError,
                s.SiteName,
                s.BrandingLogoFileName,
                s.BrandingFaviconFileName
            }).ConfigureAwait(false);
            return Result(200, admin.Id);
        }

        private async Task<RequestResult> AdminSaveSettingsAsync(HttpListenerContext context, UserRecord admin)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var s = _db.GetSettings();
            var restartSignatureBefore = BuildRuntimeSettingsSignature(s);
            if (body?["storageLocations"] is JArray storageArray)
            {
                var requested = new List<StorageLocationSetting>();
                foreach (var token in storageArray.Children<JObject>())
                {
                    var path = ((string)token["path"] ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(path))
                        continue;
                    var fullPath = Path.GetFullPath(path);
                    var id = ((string)token["id"] ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(id))
                        id = Guid.NewGuid().ToString("N");
                    requested.Add(new StorageLocationSetting
                    {
                        Id = id,
                        Path = fullPath,
                        QuotaBytes = Math.Max(0, ValueLong(token["quotaBytes"])),
                        Enabled = token["enabled"] == null || token["enabled"].Value<bool>()
                    });
                }
                if (requested.Count == 0)
                    throw new InvalidOperationException("At least one storage location is required.");
                var currentLocations = StorageService.GetLocations(s);
                var currentPrimaryId = currentLocations.Count > 0 ? currentLocations[0].Id : null;
                var ids = new HashSet<string>(requested.Select(x => x.Id), StringComparer.Ordinal);
                var primaryRemoved = !string.IsNullOrWhiteSpace(currentPrimaryId) && !ids.Contains(currentPrimaryId);
                if (_db.Files.FindAll().Any(x => string.IsNullOrWhiteSpace(x.StorageLocationId) ? primaryRemoved : !ids.Contains(x.StorageLocationId)))
                    throw new InvalidOperationException("A storage location containing files cannot be removed.");
                foreach (var location in requested)
                    Directory.CreateDirectory(location.Path);
                s.StorageLocations = requested;
                s.StorageRoot = requested[0].Path;
            }
            if (body?["bindAddress"] != null)
                s.BindAddress = ((string)body["bindAddress"] ?? "0.0.0.0").Trim();
            if (body?["httpPort"] != null)
                s.HttpPort = body["httpPort"].Value<int>();
            if (body?["httpsPort"] != null)
                s.HttpsPort = body["httpsPort"].Value<int>();
            if (s.HttpPort < 1 || s.HttpPort > 65535 || s.HttpsPort < 1 || s.HttpsPort > 65535)
                throw new InvalidOperationException("HTTP and HTTPS ports must be between 1 and 65535.");
            if (body?["publicMode"] != null)
                s.PublicMode = (string)body["publicMode"];
            if (body?["domain"] != null)
            {
                var nextDomain = NullIfEmpty((string)body["domain"]);
                if (!string.IsNullOrWhiteSpace(nextDomain))
                    nextDomain = DnsUpdateService.NormalizeZone(nextDomain);
                if (!string.Equals(s.Domain, nextDomain, StringComparison.OrdinalIgnoreCase))
                    s.CertificateThumbprint = null;
                s.Domain = nextDomain;
            }
            if (body?["publicIp"] != null && string.IsNullOrWhiteSpace(s.Domain))
            {
                var host = HostFromUrlOrHost((string)body["publicIp"]);
                if (!string.Equals(s.HttpsIdentifier, host, StringComparison.OrdinalIgnoreCase))
                    s.CertificateThumbprint = null;
                s.HttpsIdentifier = host;
            }
            if (!string.IsNullOrWhiteSpace(s.Domain))
                s.HttpsIdentifier = s.Domain;
            if (body?["adminPath"] != null)
                s.AdminPath = HttpUtil.NormalizeSecretPath((string)body["adminPath"], s.AdminPath);
            if (body?["userPath"] != null)
                s.UserPath = HttpUtil.NormalizeSecretPath((string)body["userPath"], s.UserPath);
            if (s.AdminPath == s.UserPath)
                throw new InvalidOperationException("Administrator and user secret paths must be different.");
            if (body?["language"] != null)
                s.Language = (string)body["language"];
            if (body?["serverQuotaBytes"] != null)
                s.ServerQuotaBytes = Math.Max(0, ValueLong(body["serverQuotaBytes"]));
            if (body?["trashEnabled"] != null)
                s.TrashEnabled = body["trashEnabled"].Value<bool>();
            if (body?["maxConcurrentTransfers"] != null)
                s.MaxConcurrentTransfers = Math.Max(1, Math.Min(512, body["maxConcurrentTransfers"].Value<int>()));
            if (body?["uploadLogicalBlockMiB"] != null)
                s.UploadLogicalBlockMiB = Math.Max(1, Math.Min(256, body["uploadLogicalBlockMiB"].Value<int>()));
            if (body?["vpsHost"] != null)
            {
                var newVpsHost = NullIfEmpty((string)body["vpsHost"]);
                if (!string.Equals(s.VpsHost, newVpsHost, StringComparison.OrdinalIgnoreCase))
                    s.VpsHostKeyFingerprint = null;
                s.VpsHost = newVpsHost;
            }
            if (body?["vpsPort"] != null)
                s.VpsPort = body["vpsPort"].Value<int>();
            if (body?["vpsUser"] != null)
                s.VpsUser = NullIfEmpty((string)body["vpsUser"]);
            if (!string.IsNullOrWhiteSpace((string)body?["vpsPassword"]))
                s.VpsPasswordProtected = _crypto.ProtectString((string)body["vpsPassword"]);
            if (body?["vpsPrivateKeyPath"] != null)
                s.VpsPrivateKeyPath = NullIfEmpty((string)body["vpsPrivateKeyPath"]);
            if (!string.IsNullOrWhiteSpace((string)body?["vpsPrivateKeyPassphrase"]))
                s.VpsPrivateKeyPassphraseProtected = _crypto.ProtectString((string)body["vpsPrivateKeyPassphrase"]);
            if (body?["vpsRemotePort"] != null)
                s.VpsRemotePort = body["vpsRemotePort"].Value<uint>();
            if (body?["vpsDomain"] != null)
                s.VpsDomain = NullIfEmpty((string)body["vpsDomain"]);
            if (body?["vpsUseSudo"] != null)
                s.VpsUseSudo = body["vpsUseSudo"].Value<bool>();
            if (body?["acmeEmail"] != null)
                s.AcmeEmail = NullIfEmpty((string)body["acmeEmail"]);
            if (body?["acmeTermsAccepted"] != null)
                s.AcmeTermsAccepted = body["acmeTermsAccepted"].Value<bool>();
            if (body?["dnsUpdateMode"] != null)
                s.DnsUpdateMode = (string)body["dnsUpdateMode"];
            if (string.Equals(s.DnsUpdateMode, "Rfc2136", StringComparison.OrdinalIgnoreCase))
            {
                if (body?["dnsServer"] != null)
                    s.DnsServer = DnsUpdateService.NormalizeServer((string)body["dnsServer"]);
                if (body?["dnsZone"] != null)
                    s.DnsZone = DnsUpdateService.NormalizeZone((string)body["dnsZone"]);
                if (body?["dnsTsigKeyName"] != null)
                    s.DnsTsigKeyName = DnsUpdateService.NormalizeKeyName((string)body["dnsTsigKeyName"]);
                if (body?["dnsTsigAlgorithm"] != null)
                    s.DnsTsigAlgorithm = DnsUpdateService.NormalizeAlgorithm((string)body["dnsTsigAlgorithm"]);
                if (!string.IsNullOrWhiteSpace((string)body?["dnsTsigSecret"]))
                    s.DnsTsigSecretProtected = _crypto.ProtectString(((string)body["dnsTsigSecret"]).Trim());
                if (body?["dnsAutoUpdateAddress"] != null)
                    s.DnsAutoUpdateAddress = body["dnsAutoUpdateAddress"].Value<bool>();
                if (string.IsNullOrWhiteSpace(s.Domain) || string.IsNullOrWhiteSpace(s.DnsServer) || string.IsNullOrWhiteSpace(s.DnsZone) || string.IsNullOrWhiteSpace(s.DnsTsigKeyName) || string.IsNullOrWhiteSpace(s.DnsTsigSecretProtected))
                    throw new InvalidOperationException("Domain, DNS server, DNS zone, TSIG key name and TSIG secret are required for RFC2136 DNS-01.");
            }
            else
            {
                s.DnsUpdateMode = "Manual";
                s.DnsServer = null;
                s.DnsZone = null;
                s.DnsTsigKeyName = null;
                s.DnsTsigAlgorithm = "hmac-sha256";
                s.DnsTsigSecretProtected = null;
                s.DnsAutoUpdateAddress = false;
                s.LastDnsError = null;
                s.LastDnsUpdateUtc = null;
            }
            if (!string.Equals(s.PublicMode, "Local", StringComparison.OrdinalIgnoreCase))
            {
                if (!s.AcmeTermsAccepted)
                    throw new InvalidOperationException("Automatic HTTPS is required for Internet publishing. Accept the certificate authority terms or use local network mode.");
                s.EnableHttps = true;
            }
            else
                s.EnableHttps = false;
            NormalizeDirectPublicUrl(s);
            foreach (var location in StorageService.GetLocations(s))
                Directory.CreateDirectory(location.Path);
            _db.SaveSettingsPersisted(s);
            var restartScheduled = !string.Equals(restartSignatureBefore, BuildRuntimeSettingsSignature(s), StringComparison.Ordinal);
            if (restartScheduled)
            {
                _windows.EnsureFirewall(s.HttpPort, s.HttpsPort);
                _restartVps();
                ScheduleWebRestart();
            }
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true, settingsFile = AppPaths.SettingsFilePath, restartScheduled }).ConfigureAwait(false);
            return Result(200, admin.Id);
        }


        private async Task<RequestResult> AdminBrandingAsync(HttpListenerContext context, UserRecord admin)
        {
            var settings = _db.GetSettings();
            var logoFile = ExistingBrandingFile(settings.BrandingLogoFileName);
            var faviconFile = ExistingBrandingFile(settings.BrandingFaviconFileName);
            await HttpUtil.WriteJsonAsync(context.Response, new
            {
                siteName = DisplaySiteName(settings),
                logoFile,
                faviconFile,
                logoUrl = logoFile == null ? null : "/branding/logo?v=" + AppVersion.Version,
                faviconUrl = faviconFile == null ? null : "/favicon.ico?v=" + AppVersion.Version
            }).ConfigureAwait(false);
            return Result(200, admin.Id);
        }

        private async Task<RequestResult> AdminBrandingNameAsync(HttpListenerContext context, UserRecord admin)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var settings = _db.GetSettings();
            settings.SiteName = NormalizeSiteName((string)body?["siteName"]);
            _db.SaveSettingsPersisted(settings);
            _log.App("branding-site-name-updated");
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true, siteName = settings.SiteName }).ConfigureAwait(false);
            return Result(200, admin.Id);
        }

        private async Task<RequestResult> AdminUploadBrandingAsync(HttpListenerContext context, UserRecord admin)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var kind = ((string)body?["kind"] ?? string.Empty).Trim().ToLowerInvariant();
            if (kind != "logo" && kind != "favicon")
                throw new InvalidOperationException("Unsupported branding file type.");
            var fileName = (string)body?["fileName"] ?? string.Empty;
            var data = (string)body?["data"] ?? string.Empty;
            if (data.Length > 3 * 1024 * 1024)
                throw new InvalidOperationException("Branding image is too large.");
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(data);
            }
            catch
            {
                throw new InvalidOperationException("Branding image is invalid.");
            }
            if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024)
                throw new InvalidOperationException("Branding image must be smaller than 2 MiB.");
            var extension = ValidateBrandingImage(fileName, bytes, kind == "favicon");
            Directory.CreateDirectory(AppPaths.ImagesDirectory);
            foreach (var old in Directory.GetFiles(AppPaths.ImagesDirectory, kind + ".*", SearchOption.TopDirectoryOnly))
                File.Delete(old);
            var storedName = kind + extension;
            var fullPath = Path.Combine(AppPaths.ImagesDirectory, storedName);
            using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                stream.Flush(true);
            }
            var settings = _db.GetSettings();
            if (kind == "logo")
                settings.BrandingLogoFileName = storedName;
            else
                settings.BrandingFaviconFileName = storedName;
            _db.SaveSettingsPersisted(settings);
            _log.App("branding-upload kind=" + kind + " bytes=" + bytes.Length);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true, file = storedName }).ConfigureAwait(false);
            return Result(200, admin.Id, bytes.Length);
        }


        private async Task<RequestResult> AdminDeleteBrandingAsync(HttpListenerContext context, UserRecord admin)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var kind = ((string)body?["kind"] ?? string.Empty).Trim().ToLowerInvariant();
            if (kind != "logo" && kind != "favicon")
                throw new InvalidOperationException("Unsupported branding file type.");
            var settings = _db.GetSettings();
            var storedName = kind == "logo" ? settings.BrandingLogoFileName : settings.BrandingFaviconFileName;
            if (!string.IsNullOrWhiteSpace(storedName))
            {
                var fullPath = Path.Combine(AppPaths.ImagesDirectory, Path.GetFileName(storedName));
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
            }
            if (Directory.Exists(AppPaths.ImagesDirectory))
            {
                foreach (var old in Directory.GetFiles(AppPaths.ImagesDirectory, kind + ".*", SearchOption.TopDirectoryOnly))
                    if (File.Exists(old))
                        File.Delete(old);
            }
            if (kind == "logo")
                settings.BrandingLogoFileName = null;
            else
                settings.BrandingFaviconFileName = null;
            _db.SaveSettingsPersisted(settings);
            _log.App("branding-delete kind=" + kind);
            await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
            return Result(200, admin.Id);
        }

        private static string ExistingBrandingFile(string storedName)
        {
            if (string.IsNullOrWhiteSpace(storedName))
                return null;
            var safeName = Path.GetFileName(storedName);
            if (string.IsNullOrWhiteSpace(safeName))
                return null;
            var fullPath = Path.Combine(AppPaths.ImagesDirectory, safeName);
            return File.Exists(fullPath) ? safeName : null;
        }

        private static string ValidateBrandingImage(string fileName, byte[] bytes, bool allowIco)
        {
            var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return ".png";
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return extension == ".jpeg" ? ".jpeg" : ".jpg";
            if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
                return ".gif";
            if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                return ".webp";
            if (allowIco && bytes.Length >= 6 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
                return ".ico";
            throw new InvalidOperationException("Only PNG, JPEG, GIF, WEBP and ICO favicon files are allowed.");
        }

        private async Task<RequestResult> AdminDiagnosticsAsync(HttpListenerContext context, UserRecord admin)
        {
            var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
            var report = await _diagnostics.RunAsync(body?["speedTest"]?.Value<bool>() ?? false).ConfigureAwait(false);
            await HttpUtil.WriteJsonAsync(context.Response, report).ConfigureAwait(false);
            return Result(200, admin.Id);
        }

        private async Task<RequestResult> AdminDiagnosticsExportAsync(HttpListenerContext context, UserRecord admin)
        {
            var report = await _diagnostics.RunAsync(false).ConfigureAwait(false);
            var text = _diagnostics.ExportText(report);
            context.Response.Headers["Content-Disposition"] = HttpUtil.SafeFileNameHeader("AFSharing-Diagnostic-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".txt");
            await HttpUtil.WriteTextAsync(context.Response, text, "text/plain; charset=utf-8").ConfigureAwait(false);
            return Result(200, admin.Id, Encoding.UTF8.GetByteCount(text));
        }

        private async Task<RequestResult> AdminIssueCertificateAsync(HttpListenerContext context, UserRecord admin)
        {
            var result = await _acme.EnsureCertificateAsync(true).ConfigureAwait(false);
            await HttpUtil.WriteJsonAsync(context.Response, result, result.Success ? 200 : 409).ConfigureAwait(false);
            if (result.Success && result.Changed)
                ScheduleWebRestart();
            return Result(result.Success ? 200 : 409, admin.Id);
        }

        private async Task<RequestResult> AdminDnsTestAsync(HttpListenerContext context, UserRecord admin)
        {
            var result = await _acme.SyncPublicDnsAsync().ConfigureAwait(false);
            await HttpUtil.WriteJsonAsync(context.Response, result, result.Success ? 200 : 409).ConfigureAwait(false);
            return Result(result.Success ? 200 : 409, admin.Id);
        }

        private async Task<RequestResult> AdminVpsTestAsync(HttpListenerContext context, UserRecord admin)
        {
            var settings = _db.GetSettings();
            var result = await _vps.TestAsync(settings).ConfigureAwait(false);
            await HttpUtil.WriteJsonAsync(context.Response, result, result.Success ? 200 : 409).ConfigureAwait(false);
            return Result(result.Success ? 200 : 409, admin.Id);
        }

        private async Task<RequestResult> AdminVpsConfigureAsync(HttpListenerContext context, UserRecord admin)
        {
            var settings = _db.GetSettings();
            var result = await _vps.ConfigureRemoteAsync(settings).ConfigureAwait(false);
            if (result.Success)
                _restartVps();
            await HttpUtil.WriteJsonAsync(context.Response, result, result.Success ? 200 : 409).ConfigureAwait(false);
            return Result(result.Success ? 200 : 409, admin.Id);
        }

        private async Task<RequestResult> HandleShareAsync(HttpListenerContext context)
        {
            var path = context.Request.Url.AbsolutePath;
            var tail = path.Substring(3);
            var parts = tail.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                return Result(404);
            }
            var token = parts[0];
            var share = _shares.ResolveToken(token);
            if (share == null)
            {
                await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                return Result(404);
            }
            if (parts.Length == 1 && context.Request.HttpMethod == "GET")
                return await ServeShareAppAsync(context.Response, token).ConfigureAwait(false);
            if (parts.Length == 2 && parts[1] == "info" && context.Request.HttpMethod == "GET")
            {
                if (!_shares.HasAccess(context.Request, share))
                {
                    await HttpUtil.WriteJsonAsync(context.Response, new { needsPassword = true }).ConfigureAwait(false);
                    return Result(200);
                }
                var info = _shares.PublicInfo(share, context.Request.QueryString["folder"]);
                await HttpUtil.WriteJsonAsync(context.Response, new { needsPassword = false, data = info }).ConfigureAwait(false);
                return Result(200);
            }
            if (parts.Length == 2 && parts[1] == "auth" && context.Request.HttpMethod == "POST")
            {
                var body = await HttpUtil.ReadJsonAsync<JObject>(context.Request).ConfigureAwait(false);
                if (!_shares.AuthenticatePassword(context.Response, share, (string)body?["password"], IsSecureExternal(context.Request)))
                {
                    _antiScanner.RecordFailedSharePassword(HttpUtil.RemoteIp(context.Request), context.Request.Url.AbsolutePath);
                    await HttpUtil.ErrorAsync(context.Response, 401, "invalid_share_password").ConfigureAwait(false);
                    return Result(401);
                }
                await HttpUtil.WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
                return Result(200);
            }
            if (!_shares.HasAccess(context.Request, share))
            {
                await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                return Result(404);
            }
            if (parts.Length == 2 && parts[1] == "download" && context.Request.HttpMethod == "GET")
            {
                if (share.ResourceType == "file")
                {
                    var bytes = await _shares.DownloadFileAsync(context, share, share.ResourceId).ConfigureAwait(false);
                    return Result(context.Response.StatusCode, null, bytes);
                }
                var zipBytes = await _shares.DownloadFolderZipAsync(context, share).ConfigureAwait(false);
                return Result(200, null, zipBytes);
            }
            if (parts.Length == 3 && parts[1] == "file" && context.Request.HttpMethod == "GET")
            {
                var bytes = await _shares.DownloadFileAsync(context, share, parts[2]).ConfigureAwait(false);
                return Result(context.Response.StatusCode, null, bytes);
            }
            if (parts.Length == 3 && parts[1] == "thumb" && context.Request.HttpMethod == "GET")
            {
                var file = _db.Files.FindById(parts[2]);
                if (file == null || file.IsTrashed)
                {
                    await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                    return Result(404);
                }
                if (share.ResourceType == "file" && share.ResourceId != file.Id)
                {
                    await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                    return Result(404);
                }
                if (share.ResourceType == "folder")
                {
                    var root = _db.Folders.FindById(share.ResourceId);
                    if (root == null || !_storage.IsFolderWithin(root.Id, file.ParentFolderId))
                    {
                        await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                        return Result(404);
                    }
                }
                return await SendThumbnailAsync(context, file, null).ConfigureAwait(false);
            }
            await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
            return Result(404);
        }

        private async Task<RequestResult> SendThumbnailAsync(HttpListenerContext context, FileRecord file, string userId)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.ThumbnailRelativePath))
            {
                await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                return Result(404, userId);
            }
            var path = Path.Combine(AppPaths.BaseDirectory, file.ThumbnailRelativePath);
            if (!File.Exists(path))
            {
                await HttpUtil.NotFoundAsync(context.Response).ConfigureAwait(false);
                return Result(404, userId);
            }
            var info = new FileInfo(path);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "image/jpeg";
            context.Response.ContentLength64 = info.Length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true))
                await stream.CopyToAsync(context.Response.OutputStream, 65536).ConfigureAwait(false);
            return Result(200, userId, info.Length);
        }


        private async Task<RequestResult> ServeBrandingFileAsync(HttpListenerResponse response, string kind)
        {
            var settings = _db.GetSettings();
            string path = null;
            if (kind == "logo" && !string.IsNullOrWhiteSpace(settings.BrandingLogoFileName))
                path = Path.Combine(AppPaths.ImagesDirectory, Path.GetFileName(settings.BrandingLogoFileName));
            if (kind == "favicon" && !string.IsNullOrWhiteSpace(settings.BrandingFaviconFileName))
                path = Path.Combine(AppPaths.ImagesDirectory, Path.GetFileName(settings.BrandingFaviconFileName));
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                if (kind == "favicon")
                {
                    response.StatusCode = 204;
                    response.ContentLength64 = 0;
                    response.Headers["Cache-Control"] = "no-store";
                    return Result(204);
                }
                await HttpUtil.NotFoundAsync(response).ConfigureAwait(false);
                return Result(404);
            }
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var contentType = extension == ".png" ? "image/png" : extension == ".jpg" || extension == ".jpeg" ? "image/jpeg" : extension == ".gif" ? "image/gif" : extension == ".webp" ? "image/webp" : "image/x-icon";
            var info = new FileInfo(path);
            response.StatusCode = 200;
            response.ContentType = contentType;
            response.ContentLength64 = info.Length;
            response.Headers["Cache-Control"] = kind == "favicon" ? "no-store" : "public, max-age=300";
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true))
                await stream.CopyToAsync(response.OutputStream, 65536).ConfigureAwait(false);
            return Result(200, null, info.Length);
        }

        private static string NormalizeSiteName(string value)
        {
            var raw = value ?? string.Empty;
            var clean = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
            while (clean.Contains("  "))
                clean = clean.Replace("  ", " ");
            if (string.IsNullOrWhiteSpace(clean))
                return AppVersion.ProductName;
            if (clean.Length > 80)
                throw new InvalidOperationException("File sharing name must be 80 characters or fewer.");
            return clean;
        }

        private static string DisplaySiteName(AppSettings settings)
        {
            return string.IsNullOrWhiteSpace(settings?.SiteName) ? AppVersion.ProductName : settings.SiteName.Trim();
        }

        private async Task<RequestResult> ServeAppAsync(HttpListenerResponse response, string mode)
        {
            var path = Path.Combine(AppPaths.WebDirectory, "app.html");
            if (!File.Exists(path))
            {
                await HttpUtil.ErrorAsync(response, 500, "web_assets_missing", "Web/app.html is missing next to afsharing.exe.").ConfigureAwait(false);
                return Result(500);
            }
            var siteName = WebUtility.HtmlEncode(DisplaySiteName(_db.GetSettings()));
            var html = File.ReadAllText(path).Replace("__MODE__", mode).Replace("__VERSION__", AppVersion.Version).Replace("__AUTHOR_URL__", AppVersion.AuthorUrl).Replace("__SITE_NAME__", siteName);
            response.Headers["Cache-Control"] = "no-store";
            await HttpUtil.WriteTextAsync(response, html, "text/html; charset=utf-8").ConfigureAwait(false);
            return Result(200, null, Encoding.UTF8.GetByteCount(html));
        }

        private async Task<RequestResult> ServeShareAppAsync(HttpListenerResponse response, string token)
        {
            var path = Path.Combine(AppPaths.WebDirectory, "share.html");
            if (!File.Exists(path))
            {
                await HttpUtil.ErrorAsync(response, 500, "web_assets_missing").ConfigureAwait(false);
                return Result(500);
            }
            var siteName = WebUtility.HtmlEncode(DisplaySiteName(_db.GetSettings()));
            var html = File.ReadAllText(path).Replace("__TOKEN__", token).Replace("__VERSION__", AppVersion.Version).Replace("__AUTHOR_URL__", AppVersion.AuthorUrl).Replace("__SITE_NAME__", siteName);
            response.Headers["Cache-Control"] = "no-store";
            await HttpUtil.WriteTextAsync(response, html, "text/html; charset=utf-8").ConfigureAwait(false);
            return Result(200, null, Encoding.UTF8.GetByteCount(html));
        }

        private async Task<RequestResult> ServeAssetAsync(HttpListenerResponse response, string path)
        {
            var name = path.Substring("/assets/".Length);
            var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["style.css"] = "text/css; charset=utf-8",
                ["app.js"] = "application/javascript; charset=utf-8",
                ["share.js"] = "application/javascript; charset=utf-8",
                ["locales.js"] = "application/javascript; charset=utf-8"
            };
            if (!allowed.TryGetValue(name, out var contentType))
            {
                await HttpUtil.NotFoundAsync(response).ConfigureAwait(false);
                return Result(404);
            }
            var full = Path.Combine(AppPaths.WebDirectory, name);
            if (!File.Exists(full))
            {
                await HttpUtil.NotFoundAsync(response).ConfigureAwait(false);
                return Result(404);
            }
            var bytes = File.ReadAllBytes(full);
            response.StatusCode = 200;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            response.Headers["Cache-Control"] = "public, max-age=3600";
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            return Result(200, null, bytes.Length);
        }

        private static string BuildRuntimeSettingsSignature(AppSettings settings)
        {
            if (settings == null)
                return string.Empty;
            return string.Join("\n", new[]
            {
                settings.BindAddress ?? string.Empty,
                settings.HttpPort.ToString(CultureInfo.InvariantCulture),
                settings.HttpsPort.ToString(CultureInfo.InvariantCulture),
                settings.EnableHttps ? "1" : "0",
                settings.PublicMode ?? string.Empty,
                settings.Domain ?? string.Empty,
                settings.HttpsIdentifier ?? string.Empty,
                settings.AdminPath ?? string.Empty,
                settings.UserPath ?? string.Empty,
                settings.MaxConcurrentTransfers.ToString(CultureInfo.InvariantCulture),
                settings.UploadLogicalBlockMiB.ToString(CultureInfo.InvariantCulture),
                string.Join("|", StorageService.GetLocations(settings).Select(x => (x.Id ?? string.Empty) + ":" + (x.Path ?? string.Empty) + ":" + x.QuotaBytes.ToString(CultureInfo.InvariantCulture) + ":" + (x.Enabled ? "1" : "0"))),
                settings.AcmeEmail ?? string.Empty,
                settings.AcmeTermsAccepted ? "1" : "0",
                settings.VpsHost ?? string.Empty,
                settings.VpsPort.ToString(CultureInfo.InvariantCulture),
                settings.VpsUser ?? string.Empty,
                settings.VpsPasswordProtected ?? string.Empty,
                settings.VpsPrivateKeyPath ?? string.Empty,
                settings.VpsPrivateKeyPassphraseProtected ?? string.Empty,
                settings.VpsRemotePort.ToString(CultureInfo.InvariantCulture),
                settings.VpsDomain ?? string.Empty,
                settings.DnsUpdateMode ?? string.Empty,
                settings.DnsServer ?? string.Empty,
                settings.DnsZone ?? string.Empty,
                settings.DnsTsigKeyName ?? string.Empty,
                settings.DnsTsigAlgorithm ?? string.Empty,
                settings.DnsTsigSecretProtected ?? string.Empty,
                settings.DnsAutoUpdateAddress ? "1" : "0"
            });
        }

        private void ScheduleWebRestart()
        {
            Task.Run(async () =>
            {
                await Task.Delay(750).ConfigureAwait(false);
                try { await _restartWeb().ConfigureAwait(false); } catch { }
            });
        }

        private static bool IsSecureExternal(HttpListenerRequest request)
        {
            if (request.IsSecureConnection)
                return true;
            var remote = request.RemoteEndPoint?.Address;
            return remote != null && IPAddress.IsLoopback(remote) && string.Equals(request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || !email.Contains("@"))
                throw new InvalidOperationException("Invalid email address.");
        }

        private static void ValidatePassword(string password)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
                throw new InvalidOperationException("Password must contain at least 8 characters.");
        }

        private static long ValueLong(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return 0;
            return token.Value<long>();
        }

        private static DateTime? ValueDate(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || string.IsNullOrWhiteSpace(token.ToString()))
                return null;
            if (DateTime.TryParse(token.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
                return value;
            return null;
        }

        private static void NormalizeDirectPublicUrl(AppSettings settings)
        {
            if (settings == null || !string.Equals(settings.PublicMode, "Direct", StringComparison.OrdinalIgnoreCase))
                return;
            var identifier = !string.IsNullOrWhiteSpace(settings.HttpsIdentifier) ? settings.HttpsIdentifier : HostFromUrlOrHost(settings.PublicBaseUrl);
            if (string.IsNullOrWhiteSpace(identifier))
                return;
            var secure = settings.EnableHttps;
            var builder = new UriBuilder(secure ? "https" : "http", identifier, secure ? settings.HttpsPort : settings.HttpPort);
            settings.PublicBaseUrl = builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }


        private static bool IsPublicAccessReady(AppSettings settings)
        {
            if (settings == null || string.Equals(settings.PublicMode, "Local", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(settings.PublicMode, "Vps", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.PublicBaseUrl) && settings.PublicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            return settings.EnableHttps && !string.IsNullOrWhiteSpace(settings.CertificateThumbprint) && !string.IsNullOrWhiteSpace(settings.PublicBaseUrl) && settings.PublicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static string HostFromUrlOrHost(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return uri.Host;
            var host = value.Trim();
            var slash = host.IndexOf('/');
            if (slash >= 0)
                host = host.Substring(0, slash);
            var colon = host.LastIndexOf(':');
            if (colon > 0 && host.Count(x => x == ':') == 1)
                host = host.Substring(0, colon);
            return host;
        }

        private static string NullIfEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static RequestResult Result(int statusCode, string userId = null, long bytes = 0)
        {
            return new RequestResult { StatusCode = statusCode, UserId = userId, Bytes = bytes };
        }
    }
}

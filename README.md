# APTOFI File Sharing 1.1.36

Self-hosted Windows file sharing server for Windows 7 SP1 through Windows 11.

**Author:** [APTOFI.COM](https://aptofi.com/?utm_source=afsharing&utm_medium=software&utm_campaign=aptofi_file_sharing)

## Build

Open `APTOFI.FileSharing.sln` in Visual Studio 2022 with the .NET Framework 4.8 Developer Pack installed. Restore NuGet packages and build `Release | Any CPU`. The output executable is `afsharing.exe`.

## First run and startup

Run `afsharing.exe`. The executable requires administrator elevation. A brand-new installation opens the **First run and setup** wizard, which walks through storage, network, domain/HTTPS, administrator account and a final health check. Existing 1.1.35 installations keep their current configuration and open the normal control panel; the wizard can be started manually at any time.

The file server runs as the `APTOFIFileSharing` Windows service with immediate automatic startup before user sign-in. The interactive control panel starts elevated in the Windows system tray after sign-in when tray autostart is enabled. Closing or minimizing the configured control window hides it to the tray; exiting the tray icon does not stop the server service.

All program runtime data stays beside `afsharing.exe`. The encrypted `afsharing.settings` file is written atomically and verified after every explicit configuration save. The encrypted LiteDB database, security material, certificates, logs, thumbnails and temporary files also remain beside the executable. User file storage is kept in one or more separately selected storage locations.

## Internet publishing

Internet access is the primary operating mode. LAN access is secondary. APTOFI can publish directly through a public IP, through a domain, or through a VPS/tunnel. Public HTTP and HTTPS ports are configurable and do not have to be 80 or 443.

For a trusted certificate on a high HTTPS port without giving APTOFI public TCP 80 or 443, use a domain whose DNS provider supports RFC2136 with TSIG. In **Domain settings**, enter the domain, DNS update server, authoritative zone, TSIG key name, algorithm and secret. APTOFI can synchronize the A record, create and remove the ACME DNS-01 TXT record, wait for propagation, obtain the certificate and bind it to the configured HTTPS port. After filling those fields, the **Test DNS** and **Configure HTTPS** actions on the **Domain and HTTPS** tab perform DNS synchronization, the real ACME DNS-01 challenge, certificate installation, HTTP.sys binding and service restart. The 1.1.36 RFC2136 client sends replacement A/TXT changes as one RFC2136 update set and prefers IPv4 for the authoritative TCP/53 connection. It does not create an unrelated synthetic TXT test record before ACME.

The RFC2136 implementation is provider-independent. A free DNS provider such as dynv6 is one possible provider, but APTOFI is not tied to it. Domains managed manually or by another external DNS system can also be used; certificate validation must then use a method supported by that DNS/provider arrangement.

A direct raw-IP certificate cannot use DNS-01. Publicly trusted raw-IP ACME validation requires a compatible public validation path. Use a domain with DNS-01 or VPS/tunnel when public TCP 80 and 443 are already occupied by another server.

## Storage and quotas

Multiple storage locations and disks are supported. Each storage location can have its own quota and the server can have a global quota. New uploads are placed only where the personal quota, global quota, storage quota and real free disk space all permit the upload. The control panel shows server state and per-storage disk usage.

Uploaded file contents are stored byte-for-byte without transformation. Physical storage names and extensions are random while original metadata is stored in the encrypted database. Uploads use resumable offset-based streaming with small reusable buffers. Downloads stream directly from disk and support HTTP Range requests.

## Diagnostics and logs

The first-run wizard and the explicit **Diagnostics** window perform the full network, DNS, HTTPS and reachability checks. After configuration, normal operation intentionally avoids continuous external probing: the Overview uses lightweight local state, and automatic RFC2136 A-record synchronization runs only when that option is enabled. Background DNS synchronization is best-effort, quiet on transient route/provider failures, retries later, and writes a log entry only when the A record actually changes. Manual **Test DNS** and certificate actions remain strict and report concrete failures.

Log rotation is built in and does not depend on Windows Task Scheduler or an external log tool. Defaults are **7 days retention**, **20 MiB per log file**, **150 MiB total for the logs folder**, and a **60-minute maintenance pass**. Administrators can change these limits on the **Logs** tab. Oversized daily files continue as numbered segments and the oldest files are removed first when retention or the total folder limit is exceeded.

Access logging defaults to **Compact** mode. HTTP requests are counted in memory and combined into one summary record per client IP after a configurable inactivity interval (30 minutes by default), preserving request count, methods, status-class counts, transferred bytes, timing, representative path and user information without writing every asset/API request to disk. Administrators who need forensic request-by-request data can switch the access log to **Full** mode. Access statistics are never disabled by these settings. Secret administrator/user URL prefixes and public share tokens are redacted from access-log paths.

Clipboard integration is optional convenience only. If the Windows clipboard is unavailable or locked by another process/session, APTOFI silently ignores the copy action; this does not affect the file server.

## Network responsibility and warranty

APTOFI File Sharing is provided **AS IS** and without any guarantee of uninterrupted operation, reachability, DNS propagation, certificate issuance, provider compatibility or fitness for a particular network. Internet routing, ISP behavior, CGNAT, NAT/port-forwarding, firewall rules, proxies, DNS hosting, RFC2136/TSIG permissions, certificate-authority availability, operating-system policy and third-party services are outside APTOFI.COM control. The operator is responsible for configuring and maintaining those components correctly and for keeping independent backups of important data.

The software attempts to tolerate transient network failures and uses multiple independent public-IPv4 detection services, but it cannot guarantee that an external route or third-party provider will be reachable from every server or location. A failed background check must not by itself stop the APTOFI service; use the setup wizard or explicit Diagnostics window when validating a deployment.

The full legal warranty and liability terms are stated in `LICENSE`.

## Repository cleanliness

The supplied `.gitignore` excludes build output and all runtime databases, encrypted settings, secrets, certificates, logs, thumbnails, temporary files and diagnostic exports. Never commit a TSIG secret, SSH key, certificate private key, runtime database or generated settings file.

## Public ports

The configured HTTPS port accepts HTTPS only. With the default ports, use `https://your-domain:15746/...`. The HTTP port `15745` can be published separately and redirects browser requests to the canonical HTTPS address after a certificate is installed.




## 1.1.36

- Fixes RFC2136/TSIG compatibility while keeping the implementation provider-independent and self-contained; no external `nsupdate`/BIND installation is required.
- Sends replacement A and ACME TXT records as atomic `delete + add` RFC2136 update sets and prefers IPv4 for authoritative TCP/53 connections while retaining fallback handling.
- Replaces the single `api.ipify.org` dependency with bounded fallback across independent public-IPv4 services so one unreachable CDN route cannot stall DNS maintenance.
- Makes automatic A-record maintenance low-noise: no 10-minute DNS polling, no repeated transient `TaskCanceledException` log spam, hourly best-effort synchronization only when automatic A updates are enabled, and a short quiet retry after failure.
- Keeps transient background DNS failures out of the normal Overview warning state. Full DNS resolution/RFC2136 validation runs only from the first-run wizard, **Test DNS**, certificate setup, or the explicit Diagnostics window.
- Removes continuous HTTP/HTTPS/DNS request probes from the configured control panel. Normal health display uses local Windows service/listener/certificate/storage state and performs no external DNS lookup.
- Removes UI-driven automatic service restarts based on control-panel probes. The Windows service remains authoritative, and the server runtime performs only a local self-recovery if its own web listener is no longer running.
- Silently ignores Windows clipboard failures for public-link, user-link, log and diagnostics copy actions; clipboard availability never affects server operation and no clipboard error is shown to the user.
- Suppresses internal loopback favicon probe noise from the access log while preserving real client access logging.
- Adds a separate first-run setup wizard with storage, network, DNS/HTTPS, account/startup and final deep health-check stages. Existing configured installations are not forced through the wizard.
- Reorders the control panel into Overview, Storage, Network, Domain and HTTPS, Account and startup, and Logs.
- Adds severity-colored log rows, filters, copy/open/refresh/clear-display actions and visual aggregation of repeated messages.
- Documents that APTOFI.COM cannot guarantee third-party routing, ISP/NAT/firewall/DNS/provider/CA behavior or uninterrupted availability; deployment-specific network configuration remains the operator's responsibility.

## 1.1.35

- Fixes folder-upload stalls at the logical chunk boundary by making the server read exactly the current HTTP request body instead of waiting for the remaining full file size.
- Adds a 45-second server-side receive inactivity watchdog that closes a stalled request, persists the confirmed offset and releases the per-upload lock for a clean resumable retry.
- Replaces the browser's absolute three-minute XHR timeout with a 45-second inactivity watchdog that resets on upload/network activity.
- Uses at most 8 MiB request chunks for recursive folder uploads while retaining the configured block size for ordinary uploads, reducing recovery cost on unstable links.
- Keeps successful chunk access-log suppression but emits concise `upload-read-timeout` / I/O diagnostics only when a transfer actually stalls.

## 1.1.34

- Adds recursive drag-and-drop folder uploads from the desktop while preserving the complete folder/subfolder hierarchy.
- Scans dropped trees without creating one DOM row per queued file, so very large trees do not overload the browser interface.
- Creates remote folders from root to deepest child before file transfer begins, including empty folders.
- Uploads files with the existing resumable offset protocol and a bounded two-worker queue, placing every file into its matching remote folder.
- Shows folder count, file count, total bytes, active transfer progress and a whole-tree Cancel action in the floating transfer window.
- Supports mixed drops containing folders and loose files; plain file picker uploads keep their existing behavior.
- Updates all ten web languages for folder-and-file drop instructions and recursive upload progress.

## 1.1.33

- Adds a permanent Delete button for ordinary users in Administration > Users. The protected administrator account cannot be deleted from this action.
- User deletion immediately disables the target account, terminates all of its sessions and revokes its private archive tickets before data cleanup starts.
- Permanently removes every owned file including recycle-bin contents, thumbnails, folders, public links and download tickets. This operation bypasses Trash and cannot be undone.
- Permanently removes active/incomplete upload records and their temporary physical files so user deletion cannot leave reserved or orphaned upload data.
- File deletion releases the real personal/server/storage-location quota as each physical file is removed.
- Adds a localized irreversible-action confirmation dialog in all 10 web languages and keeps the user-management actions responsive on desktop and mobile.

## 1.1.32

- Removes the selection toolbar, Select all/Clear selection buttons and all per-item checkboxes from the Files UI.
- Adds direct mouse selection with visible item highlighting. File cards/rows select on click; folder names still open with one click, while clicking the folder icon/card body selects the folder.
- Right-clicking any selected file or folder opens a context menu whose primary action downloads the entire current selection as one ZIP archive. Right-clicking an unselected item selects only that item first.
- Keeps multi-selection additive with ordinary mouse clicks and clears the selection by clicking empty space in the Files area.
- Archive download tickets now prebuild and validate the archive plan before the browser download begins, so request errors are returned in the page instead of creating failed token/download.htm entries.
- Folder and selection ZIP creation skips database entries whose physical file is missing, and uses the physical file size when metadata size is stale, instead of failing the whole archive because of one bad record.
- Starts archive attachments without the HTML download attribute so the browser uses the server-provided `.zip` Content-Disposition filename.

## 1.1.30

- Replaces folder ZIP generation with a streaming ZIP64 writer that does not require a seekable HTTP response stream or a temporary full-size archive file.
- Sends a known Content-Length and supports individual files and total archives larger than 4 GiB while keeping CPU overhead low by storing file bytes without recompression.
- Applies the same ZIP64 implementation to private folder downloads and public shared-folder downloads.
- Adds per-item selection in the Files view plus Select all, Clear selection and Download selected ZIP controls.
- Selected files and folders are validated against the authenticated owner; selected folders are archived recursively and empty directories are preserved.
- Selected archive downloads use a short-lived one-use server ticket so large selections do not need to be encoded into the URL.
- Adds responsive selection controls and complete translations for all 10 supported web languages.

## 1.1.26

- Adds an administrator-controlled recycle bin. It is disabled by default and is completely hidden from user navigation until enabled in Administration > Settings.
- When enabled, deleting a file or folder moves it to the owner’s private Trash instead of physically deleting it. Folder trees are moved and restored as one root item.
- Trash items are retained for 30 days from deletion and are then permanently purged automatically. Cleanup runs at server startup and during hourly maintenance, so overdue items are removed after downtime as well.
- Trash keeps consuming personal, server-wide, storage-location and physical disk quota until permanent deletion. Every new upload reservation reconciles quota against the real file database before accepting the upload.
- Adds Restore, Delete permanently and Empty Trash actions. Permanent deletion immediately removes the physical file, thumbnail, stale download tickets and shares and immediately frees quota.
- Restoring returns an item to its original active parent when possible; if that parent no longer exists, the item returns safely to the root with a collision-free name.
- Public share records and active download tickets are invalidated as soon as an item enters Trash. Trashed objects are also blocked defensively from private/public download and browsing routes.
- The Trash screen includes a 30-day retention banner and shows how much quota is currently occupied by Trash.
- Expiration cleanup does not bypass Trash retention: a trashed file is governed by the 30-day Trash timer, not its former file-expiration date.
- Adds complete Trash UI translations for all 10 supported web languages while preserving all 1.1.24 behavior.


## 1.1.24

- Reorders actions in every Properties dialog so Delete sits directly between Rename and Move.
- Adds a dedicated localized Close button at the bottom of file and folder Properties dialogs.
- Stabilizes grid cards so action buttons remain inside the card at all supported widths and languages.
- Gives grid cards a bounded desktop width instead of stretching/shrinking into button overflow, while preserving one-column mobile layout.
- Adds medium-width list safeguards so file rows do not overflow between desktop and mobile breakpoints.
- Includes every change from 1.1.22 and earlier; no intermediate patch is required.


## 1.1.22

- Moves upload progress from the page flow into a compact floating transfer window, shown by default in the lower-right corner and draggable with mouse, pen or touch.
- Keeps transfer progress visible while navigating between Files, Shared, Profile and Administration sections.
- Preserves the transfer window inside the viewport across desktop/tablet/mobile sizes and remembers the user-dragged position.
- Performs a full responsive pass across login, file list/grid, profile, administration, diagnostics, security, branding, modals, context menus and public share/download pages, including narrow 320-380 px and intermediate 621-900 px layouts.
- Includes all resilient upload recovery and live-log fixes from 1.1.17; installing 1.1.22 does not require installing 1.1.17 first.

## 1.1.17

- Makes resumable uploads idempotent: repeated upload-start requests reuse the same active transfer and repeated completion requests return the already-created file instead of creating duplicates or reporting a false failure.
- Keeps completed upload records briefly so a lost completion response can be recovered safely; cleanup no longer deletes the physical file belonging to an already-completed upload.
- Adds automatic retry/backoff for upload start, status/resume and completion requests, plus a three-minute chunk timeout so transient network or service interruptions resume instead of immediately becoming “Upload failed”.
- Returns upload I/O failures as retryable HTTP 503 responses and reduces transport exception logging to concise one-line diagnostics.
- Stops writing successful upload-chunk requests to the access log and suppresses repeated unchanged DNS success messages during the same service run.
- Collapses runtime exception stack traces to one concise log line with the exception type/message instead of filling the control panel with source-path stack frames.
- Removes the manual Logs refresh button. The control-panel log view now loads immediately, follows log-file changes automatically and reads only the tail of large log files.
- Adds a localized “Copy log” button to the Logs tab.
- Includes all 1.1.16 and earlier functionality.


## 1.1.16

- Replaces per-item folder `click` navigation with pointer-based navigation (`pointerdown` / movement threshold / `pointerup`) so a normal single left click opens a folder reliably even when rows are draggable.
- Separates folder navigation from HTML5 drag state and suppresses navigation only after a real drag gesture.
- `loadItems()` now accepts the requested folder directly and only uses request serials to reject stale responses; it no longer rejects a legitimate folder response because `currentFolder` still contains the previous folder.
- Folder Open from Properties and the folder context menu now use the same `openFolder(id)` path; Back also requests its target folder directly.
- Includes all 1.1.15 and earlier functionality.

## 1.1.15

- Fixes folder navigation regression introduced in 1.1.14: no-argument busy navigation now keeps the selected folder instead of treating `undefined` as an explicit request for the root.
- Restores single-click folder opening, Back navigation and Open from folder Properties/context menu while preserving stale-request protection and all 1.1.14 functionality.



## 1.1.14

- Opens folders with a single left click instead of requiring a double click.
- Adds a folder right-click menu with Open, Download, Rename, Move, Delete and Properties.
- Folder Properties now shows recursive file count, subfolder count and total size.
- Preserves background uploads, streaming ZIP folder downloads, account isolation, preloaders and all previous fixes.

## 1.1.13

- Includes all changes from 1.1.12.
- Keeps uploaded file bytes unchanged and continues to use random physical names and extensions.
- Keeps the transfer path asynchronous and sequential with bounded reusable buffers.
- Adds storage write and flush stall diagnostics without disabling or bypassing Windows security scanning.
- Does not add antivirus exclusions, content obfuscation, or security-product bypasses.

## 1.1.12

- Keeps resumable uploads independent from folder and administration navigation.
- Adds a delayed animated preloader for slow navigation, login, administration and long-running operations.
- Removes administrator access to other users’ files and shared links from the web interface and authenticated file APIs.
- Adds interactive per-user quota usage and last successful login time to Administration > Users.
- Preserves streaming ZIP folder downloads introduced in 1.1.11.

## 1.1.11

- Folder properties can download the entire folder tree as a streaming ZIP archive without creating a temporary archive on disk.
- Streaming ZIP preserves nested folders, including empty folders, and uses no-compression entries to minimize CPU load.
- Upload batches now capture their owner and destination folder when the batch starts.
- Uploads continue independently while the user navigates to other folders, Shared, Profile or Administration.
- Queued files no longer inherit a different folder selected later in the interface.
- Upload completion refreshes only the folder that is currently visible and matches the original upload destination.
- Asynchronous item loading ignores stale responses so fast folder navigation cannot jump the interface back to an older folder.
- Resume keys include the upload owner and destination folder so the same file name can be uploaded safely to different folders.
- Finishing a background upload no longer calls the full login/state bootstrap or moves the user away from the current section.

## 1.1.10

- Added a global operation preloader for long-running file, folder, sharing, settings, diagnostics, branding, user-management and security operations.
- Recursive folder deletion now keeps the browser UI visibly busy until physical deletion and database cleanup finish.
- The preloader shows the current operation, a clear not-frozen message and elapsed time.
- Mass uploads keep their existing per-file progress bars and total upload speed and are not covered by the global preloader.
- Added complete preloader translations for all ten supported web languages.

## 1.1.9

- Added a persistent custom file-sharing name configurable in Administration > Settings > Branding.
- The custom name is shown in browser titles, sign-in screens, the sidebar, public share pages, the WPF control window and tray tooltip.
- The configured name is HTML-encoded before template insertion and limited to 80 non-control characters.
- Updated all ten tray translations so the exit action clearly means closing the program completely.

## 1.1.7

- Logo and favicon are now fully independent branding settings.
- Added separate upload and delete controls for logo and favicon.
- Removing the favicon leaves the web interface without a custom browser icon.
- Fixed folder workspace context menu so right-click works across the empty file area, not only the rendered list height.
- Long-press on empty file workspace opens the same menu on touch devices.
- Re-audited source comments, localization parity, runtime secret exclusions and admin/CSRF protection for branding mutations.

## 1.1.6

This release adds adaptive administration layouts, branding and favicon controls, manual IP blocking, bounded system/security event viewing, translated diagnostic labels, compact public-link and user tables, explicit logout controls, mobile folder context actions, and additional HTTP security headers. Runtime branding files, databases, secrets, certificates, logs and settings remain excluded from Git.

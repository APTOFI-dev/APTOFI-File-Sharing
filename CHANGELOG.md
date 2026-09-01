# Changelog

Current release: **1.1.35**

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


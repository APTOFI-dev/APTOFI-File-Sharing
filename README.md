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


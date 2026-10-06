# Changelog

Current test version: **1.1.36**

## 1.1.36

- Fixes a 1.1.36 localization regression: Overview health/DNS/certificate messages, Diagnostics and the setup wizard now use the selected control-panel language instead of hard-coded Russian strings; all 10 supported UI languages include the new runtime texts.
- Keeps RFC2136/TSIG provider-independent and self-contained; no external BIND/nsupdate dependency is required.
- Detects public IPv4 through multiple independent providers with bounded per-provider timeouts, avoiding a single bad CDN route from stalling DNS maintenance.
- Changes configured-mode DNS maintenance from noisy 10-minute polling to quiet best-effort synchronization: one startup attempt, hourly checks only when automatic A updates are enabled, and a silent 15-minute retry after a transient failure.
- Background DNS failures no longer write repeated `TaskCanceledException`/maintenance errors or place the Overview into a warning state; explicit Test DNS / setup / certificate / Diagnostics actions remain strict and visible.
- Normal control-panel health no longer performs HTTP, HTTPS or DNS network probes. It uses local Windows service, listener, certificate and storage state; full checks are reserved for the first-run wizard and explicit Diagnostics.
- Removes control-panel-triggered automatic service restarts from periodic probes and keeps only local runtime self-recovery if the built-in listener actually stops.
- Silently ignores Windows clipboard failures in all Copy actions so RDP/clipboard problems cannot create application errors or affect server operation.
- Suppresses internal loopback favicon probe noise from access logs.
- Adds built-in log rotation with administrator-configurable retention, per-file size, total logs-folder size and maintenance interval; defaults are 7 days, 20 MiB per file, 150 MiB total and a 60-minute maintenance pass.
- Adds Compact/Full access-log modes. Compact is the default and writes one aggregated summary per client IP after a configurable inactivity interval (30 minutes by default) instead of one disk line for every asset/API request, while preserving request counts, status classes, bytes, latency and user/path context.
- Adds the log rotation and access-detail controls directly to the Logs tab so different server administrators can tune retention and verbosity for their own environment without editing source code.
- Compact and full access logging redact administrator/user secret URL prefixes and public share tokens from stored paths.
- Adds repository documentation clarifying that ISP routing, NAT, firewall, proxy, DNS-provider, RFC2136/TSIG, certificate-authority and other third-party network behavior are outside APTOFI.COM control and are not guaranteed.

## 1.1.35

- Fixes folder-upload stalls at the logical chunk boundary by making the server read exactly the current HTTP request body instead of waiting for the remaining full file size.
- Adds a 45-second server-side receive inactivity watchdog that closes a stalled request, persists the confirmed offset and releases the per-upload lock for a clean resumable retry.
- Replaces the browser's absolute three-minute XHR timeout with a 45-second inactivity watchdog that resets on upload/network activity.
- Uses at most 8 MiB request chunks for recursive folder uploads while retaining the configured block size for ordinary uploads, reducing recovery cost on unstable links.
- Keeps successful chunk access-log suppression but emits concise `upload-read-timeout` / I/O diagnostics only when a transfer actually stalls.




# Security

APTOFI File Sharing handles accounts, private files, public links, TLS certificates, DNS credentials and optional VPS credentials.

- Do not publish runtime databases, `afsharing.settings`, logs containing sensitive data, TSIG secrets, SSH keys, certificate private keys or exported diagnostics.
- Change the administrator and user secret paths before exposing a new installation to the Internet.
- Prefer HTTPS for public access.
- Keep Windows, .NET Framework and NuGet dependencies updated.
- Report security issues privately using the contact information at https://aptofi.com rather than posting credentials or exploitable details in a public issue.

Developer: **APTOFI.COM** — https://aptofi.com

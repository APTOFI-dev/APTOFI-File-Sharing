# APTOFI File Sharing 1.1.35

[English](../README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Українська](README.uk.md) · **Français** · [Polski](README.pl.md) · [Türkçe](README.tr.md) · [한국어](README.ko.md) · [中文](README.zh.md) · [日本語](README.ja.md)

**Développeur :** [APTOFI.COM](https://aptofi.com)

Plus de place sur Google Drive, OneDrive ou Yandex Disk, mais vous avez un HDD/SSD libre et un PC Windows connecté ? **APTOFI File Sharing transforme ce PC en serveur de fichiers privé.**

## Points forts

- stockage sur vos propres disques ;
- glisser-déposer de dossiers complets avec toute l’arborescence et les dossiers vides ;
- uploads streaming reprenables pour les gros fichiers ;
- téléchargement de dossiers ou sélections en ZIP64 streaming sans archive temporaire complète ;
- comptes utilisateurs, quotas, IP de dernière connexion, liens publics et suppression complète d’un utilisateur ;
- corbeille optionnelle, désactivée par défaut, rétention 30 jours ;
- LAN, IP/domaine direct ou VPS/tunnel SSH inverse ;
- HTTPS automatique via ACME DNS-01 + RFC2136/TSIG ;
- service Windows, tray et interface responsive en 10 langues.

## Prérequis / compilation

Windows 7 SP1–Windows 11, .NET Framework 4.8. Compilation avec Visual Studio 2022 + .NET Framework 4.8 Developer Pack.

```powershell
msbuild APTOFI.FileSharing.sln /m /restore /p:Configuration=Release /p:Platform="Any CPU"
```

Sortie : `src/APTOFI.FileSharing/bin/Release/afsharing.exe`.

## Premier démarrage

Exécutez l’EXE en administrateur, ajoutez un stockage comme `D:\FileSharing`, configurez l’administrateur, changez les chemins secrets (`/my-admin-9F3x`, `/files-k7P2`), choisissez le mode réseau puis **Save and start**.

## LAN uniquement

```text
Bind: 0.0.0.0
Mode: Local
HTTP: 15745
HTTPS: facultatif sur LAN de confiance
```

Avec le serveur `192.168.1.50` : `http://192.168.1.50:15745/files-k7P2`. Aucun domaine requis.

## Internet / domaine

En mode direct, redirigez le port TCP du routeur vers le serveur. Le HTTP public sur IP seule est déconseillé ; préférez domaine + HTTPS ou VPS. En CGNAT, utilisez le tunnel VPS.

```text
Domain: files.example.net
HTTPS: 15746
DNS: RFC2136 / TSIG
DNS server: ns1.example.net
DNS zone: files.example.net
TSIG key/secret: fournis par le DNS
Algorithm: HMAC-SHA256
Auto A update: enabled
```

Testez le DNS, émettez le certificat puis ouvrez TCP `15746`. DNS-01 ne nécessite pas les ports publics 80/443. Pour dynv6, le serveur est typiquement `ns1.dynv6.com`.

## VPS / tunnel

Configurez un VPS Ubuntu/Debian avec host SSH, port, utilisateur, mot de passe/clé, port distant (ex. `18080`) et domaine. APTOFI maintient un reverse SSH tunnel reconnectable.

## Utilisation / sécurité

Glissez fichiers ou arborescences de dossiers dans le navigateur. Sélectionnez plusieurs éléments à la souris et téléchargez-les par clic droit en un ZIP. Ne commitez jamais `afsharing.settings`, bases, secrets TSIG, clés SSH ou clés privées de certificat.

L’attribution du projet est explicitement documentée : **APTOFI.COM — https://aptofi.com**.

## Licence

Utilisation, modification et redistribution autorisées selon **APTOFI Attribution License 1.0**, avec attribution obligatoire à **APTOFI.COM** et lien **https://aptofi.com**.

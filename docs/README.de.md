# APTOFI File Sharing 1.1.35

[English](../README.md) · [Русский](README.ru.md) · **Deutsch** · [Українська](README.uk.md) · [Français](README.fr.md) · [Polski](README.pl.md) · [Türkçe](README.tr.md) · [한국어](README.ko.md) · [中文](README.zh.md) · [日本語](README.ja.md)

**Entwickler:** [APTOFI.COM](https://aptofi.com)

Kein Platz mehr in Google Drive, OneDrive oder Yandex Disk, aber eine freie HDD/SSD und ein Windows-PC mit Netzwerk? **APTOFI File Sharing macht daraus einen eigenen privaten Dateiserver.**

## Stärken

- eigene lokale Datenträger statt zusätzlichem Cloud-Speicher;
- komplette Ordner per Drag-and-drop inklusive Unterordnern und leeren Ordnern;
- fortsetzbare Streaming-Uploads für große Dateien;
- Ordner oder markierte Objekte als ein Streaming-ZIP64 ohne temporäres Komplettarchiv;
- mehrere Benutzer, Quoten, letzte Login-IP, Freigabelinks und vollständige Benutzerlöschung;
- optionaler Papierkorb, standardmäßig deaktiviert, mit 30 Tagen Aufbewahrung;
- passwortgeschützte und zeitlich begrenzte öffentliche Links;
- LAN, direkte IP/Domain oder VPS/Reverse-SSH-Tunnel;
- automatisches HTTPS über ACME DNS-01 + RFC2136/TSIG;
- Windows-Dienst und Tray-Steuerung;
- responsive Weboberfläche und 10 Sprachen.

## Voraussetzungen / Build

Windows 7 SP1–Windows 11, .NET Framework 4.8. Für Service, HTTP.sys, Firewall und Zertifikate sind Administratorrechte nötig. Build mit Visual Studio 2022 + .NET Framework 4.8 Developer Pack.

```powershell
msbuild APTOFI.FileSharing.sln /m /restore /p:Configuration=Release /p:Platform="Any CPU"
```

Ausgabe: `src/APTOFI.FileSharing/bin/Release/afsharing.exe`.

## Erste Einrichtung

1. `afsharing.exe` als Administrator starten.
2. Speicherpfad hinzufügen, z. B. `D:\FileSharing`.
3. Administratorkonto prüfen/erstellen.
4. Geheime Admin-/Benutzerpfade ändern, z. B. `/my-admin-9F3x` und `/files-k7P2`.
5. Netzwerkmodus wählen und **Speichern und starten**.

## Nur LAN

```text
Bind-Adresse: 0.0.0.0
Modus: Local
HTTP-Port: 15745
HTTPS: in vertrauenswürdigem LAN optional
Benutzerpfad: /files-k7P2
```

Beispiel: `http://192.168.1.50:15745/files-k7P2`. Kein Domainname erforderlich.

## Öffentliche IP / Domain

Für direkten Internetzugriff TCP-Port am Router weiterleiten. Reines öffentliches HTTP über IP ist möglich, wenn HTTPS deaktiviert ist, aber nicht empfohlen. Besser Domain + HTTPS oder VPS/Tunnel. Bei CGNAT den VPS-Modus verwenden.

RFC2136/TSIG-Beispiel:

```text
Domain: files.example.net
HTTPS-Port: 15746
DNS-Modus: RFC2136 / TSIG
DNS-Server: ns1.example.net
DNS-Zone: files.example.net
TSIG-Key-Name: vom DNS-Anbieter
Algorithmus: HMAC-SHA256
TSIG-Secret: vom DNS-Anbieter
A-Record automatisch aktualisieren: ein
```

Danach DNS testen, Zertifikat ausstellen und TCP `15746` weiterleiten. DNS-01 benötigt für die Zertifikatsprüfung keine öffentlichen Ports 80/443.

Für dynv6: Domain/Zone `myfiles.dynv6.net`, Update-Server `ns1.dynv6.com`, TSIG-Schlüssel im dynv6-Konto anlegen.

## VPS / Tunnel

Bei CGNAT einen Ubuntu/Debian-VPS eintragen: Host, SSH-Port, Benutzer, Passwort/Key, Remote-Port und Domain. Beispiel Remote-Port `18080`. APTOFI hält einen reconnectenden Reverse-SSH-Tunnel.

## Nutzung / Sicherheit

Dateien oder ganze Ordnerbäume in den Browser ziehen. Eigenschaften bieten Download, Umbenennen, Löschen und Verschieben. Mehrere Objekte mit der Maus markieren und per Rechtsklick als ZIP herunterladen. Runtime-Daten, `afsharing.settings`, Datenbanken, TSIG-Secrets, SSH-Keys und private Zertifikatsschlüssel niemals committen.

Die Projektattribution ist ausdrücklich dokumentiert: **APTOFI.COM — https://aptofi.com**.

## Lizenz

Nutzung, Änderung und Weitergabe sind unter der **APTOFI Attribution License 1.0** erlaubt. Die Nennung von **APTOFI.COM** mit **https://aptofi.com** ist verpflichtend.

# APTOFI File Sharing 1.1.35

[English](../README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Українська](README.uk.md) · [Français](README.fr.md) · **Polski** · [Türkçe](README.tr.md) · [한국어](README.ko.md) · [中文](README.zh.md) · [日本語](README.ja.md)

**Deweloper:** [APTOFI.COM](https://aptofi.com)

Brakuje miejsca w chmurze, ale masz wolny HDD/SSD i komputer Windows z siecią? **APTOFI File Sharing zmienia go we własny serwer plików.**

## Najważniejsze zalety

- własne dyski zamiast dodatkowego miejsca w chmurze;
- przeciąganie całych folderów z zachowaniem pełnej hierarchii i pustych katalogów;
- wznawialny streaming dużych plików;
- foldery i zaznaczone elementy pobierane jako jeden ZIP64 bez pełnego pliku tymczasowego;
- wielu użytkowników, limity, IP ostatniego logowania, linki publiczne i pełne usuwanie użytkownika;
- opcjonalny Kosz, domyślnie wyłączony, 30 dni retencji;
- LAN, bezpośredni IP/domena albo VPS/reverse SSH;
- HTTPS przez ACME DNS-01 + RFC2136/TSIG;
- usługa Windows, tray i responsywny interfejs w 10 językach.

## Wymagania / build

Windows 7 SP1–Windows 11, .NET Framework 4.8. Visual Studio 2022 + .NET Framework 4.8 Developer Pack.

```powershell
msbuild APTOFI.FileSharing.sln /m /restore /p:Configuration=Release /p:Platform="Any CPU"
```

Plik wynikowy: `src/APTOFI.FileSharing/bin/Release/afsharing.exe`.

## Pierwsze uruchomienie

Uruchom jako administrator, dodaj magazyn np. `D:\FileSharing`, skonfiguruj konto administratora, zmień tajne ścieżki (`/my-admin-9F3x`, `/files-k7P2`), wybierz tryb sieci i zapisz/uruchom.

## Tylko LAN

```text
Bind: 0.0.0.0
Mode: Local
HTTP: 15745
HTTPS: opcjonalny w zaufanej LAN
```

Przykład: `http://192.168.1.50:15745/files-k7P2`. Domena nie jest potrzebna.

## Internet / domena

W trybie Direct przekieruj port TCP routera na serwer. Publiczny HTTP tylko po IP nie jest zalecany; użyj domeny + HTTPS albo VPS. Przy CGNAT wybierz VPS.

```text
Domain: files.example.net
HTTPS: 15746
DNS: RFC2136 / TSIG
DNS server: ns1.example.net
DNS zone: files.example.net
TSIG key/secret: od operatora DNS
Algorithm: HMAC-SHA256
Auto A update: on
```

Przetestuj DNS, wydaj certyfikat i otwórz TCP `15746`. DNS-01 nie wymaga publicznych portów 80/443. Dla dynv6 typowy serwer aktualizacji to `ns1.dynv6.com`.

## VPS / tunel

Wprowadź VPS Ubuntu/Debian: host SSH, port, użytkownik, hasło/klucz, port zdalny (np. `18080`) i domenę. APTOFI utrzymuje reconnecting reverse SSH tunnel.

## Użycie / bezpieczeństwo

Przeciągaj pliki lub całe drzewa folderów do przeglądarki. Kilka obiektów można zaznaczyć myszą i pobrać prawym kliknięciem jako jeden ZIP. Nie commituj `afsharing.settings`, baz danych, sekretów TSIG, kluczy SSH ani prywatnych kluczy certyfikatów.

Atrybucja projektu jest jawnie opisana: **APTOFI.COM — https://aptofi.com**.

## Licencja

Używanie, modyfikacja i dystrybucja są dozwolone zgodnie z **APTOFI Attribution License 1.0** pod warunkiem zachowania autorstwa i linku **https://aptofi.com**.

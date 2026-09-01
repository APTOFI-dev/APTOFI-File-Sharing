# APTOFI File Sharing 1.1.35

[English](../README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · **Українська** · [Français](README.fr.md) · [Polski](README.pl.md) · [Türkçe](README.tr.md) · [한국어](README.ko.md) · [中文](README.zh.md) · [日本語](README.ja.md)

**Розробник:** [APTOFI.COM](https://aptofi.com)

Закінчилося місце у хмарі, але є вільний HDD/SSD і Windows-ПК з мережею? **APTOFI File Sharing перетворює цей ПК на власний файловий сервер.**

## Сильні сторони

- власні диски замість додаткового хмарного місця;
- завантаження цілих папок зі збереженням усієї ієрархії та порожніх підпапок;
- відновлювані потокові завантаження великих файлів;
- ZIP64 для папок або вибраних об’єктів без тимчасового повного архіву;
- користувачі, квоти, IP останнього входу, публічні посилання та повне видалення користувача;
- кошик вимкнений за замовчуванням, при ввімкненні зберігає видалене 30 днів;
- LAN, пряма IP/доменна публікація або VPS/reverse SSH;
- HTTPS через ACME DNS-01 + RFC2136/TSIG;
- Windows-служба, tray та адаптивний WEB на 10 мовах.

## Вимоги та збірка

Windows 7 SP1–Windows 11, .NET Framework 4.8. Для збірки Visual Studio 2022 + .NET Framework 4.8 Developer Pack.

```powershell
msbuild APTOFI.FileSharing.sln /m /restore /p:Configuration=Release /p:Platform="Any CPU"
```

Результат: `src/APTOFI.FileSharing/bin/Release/afsharing.exe`.

## Перший запуск

Запусти EXE від адміністратора, додай сховище `D:\FileSharing`, налаштуй адміністратора, зміни секретні шляхи (наприклад `/my-admin-9F3x`, `/files-k7P2`), вибери режим мережі та натисни **Зберегти і запустити**.

## Локальна мережа

```text
Bind: 0.0.0.0
Режим: Local
HTTP: 15745
HTTPS: необов’язковий у довіреній LAN
```

При IP сервера `192.168.1.50`: `http://192.168.1.50:15745/files-k7P2`.

## Інтернет / домен

Для прямого доступу пробрось TCP-порт роутера. Публічний HTTP по IP не рекомендований; краще домен + HTTPS або VPS. При CGNAT використовуй VPS.

```text
Domain: files.example.net
HTTPS: 15746
DNS mode: RFC2136 / TSIG
DNS server: ns1.example.net
DNS zone: files.example.net
TSIG: ключ і секрет від DNS-провайдера
Algorithm: HMAC-SHA256
Auto A record: on
```

Після збереження перевір DNS, випусти сертифікат і пробрось TCP `15746`. Для DNS-01 порти 80/443 не потрібні. Для dynv6 зазвичай сервер оновлення `ns1.dynv6.com`, зона дорівнює вашому `*.dynv6.net`.

## VPS / тунель

Вкажи Ubuntu/Debian VPS, SSH host/port/user, пароль або ключ, remote port (наприклад `18080`) та домен. APTOFI підтримує reverse SSH tunnel з перепідключенням.

## Використання та безпека

Перетягуй файли або дерева папок у браузер. Кілька об’єктів можна виділити мишею та завантажити правою кнопкою одним ZIP. Не коміть `afsharing.settings`, БД, логи з секретами, TSIG/SSH ключі та приватні ключі сертифікатів.

Атрибуція проєкту явно описана в ліцензії: **APTOFI.COM — https://aptofi.com**.

## Ліцензія

Використання, зміна та розповсюдження дозволені за **APTOFI Attribution License 1.0** з обов’язковим посиланням **https://aptofi.com**.

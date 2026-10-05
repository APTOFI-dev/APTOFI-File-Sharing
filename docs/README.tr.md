# APTOFI File Sharing 1.1.36

[English](../README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Українська](README.uk.md) · [Français](README.fr.md) · [Polski](README.pl.md) · **Türkçe** · [한국어](README.ko.md) · [中文](README.zh.md) · [日本語](README.ja.md)

**Geliştirici:** [APTOFI.COM](https://aptofi.com)

Bulut depolama alanınız bitti ama boş bir HDD/SSD ve ağa bağlı Windows PC’niz var mı? **APTOFI File Sharing bu bilgisayarı kendi özel dosya sunucunuza dönüştürür.**

## Güçlü yönler

- kendi disklerinizi kullanın;
- klasörleri tüm alt klasör yapısı ve boş klasörlerle sürükleyip bırakın;
- büyük dosyalar için devam ettirilebilir streaming upload;
- klasörleri veya seçili öğeleri geçici tam arşiv oluşturmadan tek ZIP64 olarak indirin;
- kullanıcılar, kotalar, son giriş IP’si, genel bağlantılar ve kullanıcıyı tüm verileriyle kalıcı silme;
- isteğe bağlı Çöp Kutusu, varsayılan kapalı, açılırsa 30 gün saklama;
- LAN, doğrudan IP/domain veya VPS/reverse SSH;
- ACME DNS-01 + RFC2136/TSIG ile HTTPS;
- Windows servisi, tray ve 10 dilli responsive web arayüzü.

## Gereksinimler / derleme

Windows 7 SP1–Windows 11, .NET Framework 4.8. Visual Studio 2022 + .NET Framework 4.8 Developer Pack.

```powershell
msbuild APTOFI.FileSharing.sln /m /restore /p:Configuration=Release /p:Platform="Any CPU"
```

Çıktı: `src/APTOFI.FileSharing/bin/Release/afsharing.exe`.

## İlk kurulum

EXE’yi yönetici olarak çalıştırın, `D:\FileSharing` gibi depolama ekleyin, yönetici hesabını ayarlayın, gizli yolları değiştirin (`/my-admin-9F3x`, `/files-k7P2`), ağ modunu seçin ve kaydedip başlatın.

## Yalnızca LAN

```text
Bind: 0.0.0.0
Mode: Local
HTTP: 15745
HTTPS: güvenilir LAN için isteğe bağlı
```

Sunucu `192.168.1.50` ise: `http://192.168.1.50:15745/files-k7P2`. Domain gerekmez.

## İnternet / domain

Direct modunda router TCP portunu sunucuya yönlendirin. Sadece public IP üzerinden HTTP önerilmez; domain + HTTPS veya VPS kullanın. CGNAT varsa VPS modunu seçin.

```text
Domain: files.example.net
HTTPS: 15746
DNS: RFC2136 / TSIG
DNS server: ns1.example.net
DNS zone: files.example.net
TSIG key/secret: DNS sağlayıcısından
Algorithm: HMAC-SHA256
Auto A update: on
```

DNS’i test edin, sertifikayı alın ve TCP `15746` portunu yönlendirin. DNS-01 için public 80/443 gerekli değildir. dynv6 için tipik update server `ns1.dynv6.com`’dur.

## VPS / tünel

Ubuntu/Debian VPS hostu, SSH portu/kullanıcısı, parola veya key, remote port (örn. `18080`) ve domain girin. APTOFI otomatik yeniden bağlanan reverse SSH tunnel kullanır.

## Kullanım / güvenlik

Dosyaları veya tüm klasör ağaçlarını tarayıcıya sürükleyin. Birden fazla öğeyi fareyle seçip sağ tıkla tek ZIP olarak indirin. `afsharing.settings`, veritabanları, TSIG secret, SSH key ve sertifika private key dosyalarını Git’e eklemeyin.

Proje atfı açıkça belgelenmiştir: **APTOFI.COM — https://aptofi.com**.

## Lisans

Kullanım, değiştirme ve dağıtım **APTOFI Attribution License 1.0** kapsamında serbesttir; **APTOFI.COM** ve **https://aptofi.com** atfı zorunludur.

## Günlük döndürme ve erişim günlüğü

1.1.36 sürümü yapılandırılabilir yerleşik günlük döndürme ve IP başına kompakt erişim günlüğü içerir. Varsayılanlar: 7 gün saklama, dosya başına 20 MiB, `logs` klasörü için toplam 150 MiB, her 60 dakikada döndürme kontrolü ve aynı IP için 30 dakikalık hareketsizlikten sonra özetleme. Yönetici **Logs** sekmesinden bu değerleri değiştirebilir veya her HTTP isteğini yazan tam modu açabilir. Yönetici/kullanıcı gizli yolları ve genel paylaşım belirteçleri günlük yollarında maskelenir.

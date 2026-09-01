# APTOFI File Sharing 1.1.35

[English](../README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Українська](README.uk.md) · [Français](README.fr.md) · [Polski](README.pl.md) · [Türkçe](README.tr.md) · [한국어](README.ko.md) · [中文](README.zh.md) · **日本語**

**開発者:** [APTOFI.COM](https://aptofi.com)

Google Drive、OneDrive、Yandex Disk の容量が足りない一方で、空き HDD/SSD とネットワーク接続された Windows PC があるなら、**APTOFI File Sharing で自分専用のファイルサーバーにできます。**

## 強み

- 自分のディスクをそのままストレージに利用;
- フォルダー全体をドラッグし、サブフォルダーと空フォルダーを含む階層を再現;
- 大容量ファイルの再開可能なストリーミングアップロード;
- フォルダーや選択項目を一時フルアーカイブなしで ZIP64 ストリーミングダウンロード;
- 複数ユーザー、クォータ、最終ログイン IP、公開リンク、ユーザーと全データの永久削除;
- ごみ箱は既定で無効、必要時は 30 日保持;
- LAN、直接 IP/ドメイン、VPS/reverse SSH;
- ACME DNS-01 + RFC2136/TSIG による HTTPS;
- Windows サービス、トレイ、10 言語のレスポンシブ WEB。

## 必要環境 / ビルド

Windows 7 SP1–Windows 11、.NET Framework 4.8。Visual Studio 2022 + .NET Framework 4.8 Developer Pack。

```powershell
msbuild APTOFI.FileSharing.sln /m /restore /p:Configuration=Release /p:Platform="Any CPU"
```

出力: `src/APTOFI.FileSharing/bin/Release/afsharing.exe`。

## 初回設定

管理者として EXE を起動 → `D:\FileSharing` などの保存先を追加 → 管理者アカウントを設定 → 秘密パスを変更（`/my-admin-9F3x`, `/files-k7P2`）→ ネットワークモード選択 → 保存して開始。

## LAN のみ

```text
Bind: 0.0.0.0
Mode: Local
HTTP: 15745
HTTPS: 信頼できる LAN では任意
```

サーバー IP が `192.168.1.50` の場合: `http://192.168.1.50:15745/files-k7P2`。ドメイン不要。

## インターネット / ドメイン

Direct モードではルーターの TCP ポートをサーバーへ転送します。公開 IP + HTTP のみは推奨しません。ドメイン + HTTPS または VPS を使用してください。CGNAT では VPS モードを使います。

```text
Domain: files.example.net
HTTPS: 15746
DNS: RFC2136 / TSIG
DNS server: ns1.example.net
DNS zone: files.example.net
TSIG key/secret: DNS プロバイダーから取得
Algorithm: HMAC-SHA256
Auto A update: on
```

DNS テスト後に証明書を発行し TCP `15746` を転送します。DNS-01 では公開 80/443 ポートは不要です。dynv6 の一般的な更新サーバーは `ns1.dynv6.com` です。

## VPS / トンネル

Ubuntu/Debian VPS の SSH host/port/user、パスワードまたは鍵、remote port（例 `18080`）、ドメインを設定します。APTOFI は再接続可能な reverse SSH tunnel を維持します。

## 利用 / セキュリティ

ファイルまたはフォルダーツリー全体をブラウザーへドラッグします。複数項目をマウス選択して右クリックすると 1 つの ZIP として取得できます。`afsharing.settings`、DB、TSIG secret、SSH key、証明書 private key を Git に追加しないでください。

プロジェクトの著作者表示は明確に文書化されています: **APTOFI.COM — https://aptofi.com**。

## ライセンス

**APTOFI Attribution License 1.0** により使用・変更・再配布可能ですが、**APTOFI.COM** と **https://aptofi.com** の表示が必須です。

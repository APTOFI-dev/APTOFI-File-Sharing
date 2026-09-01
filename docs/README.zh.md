# APTOFI File Sharing 1.1.35

[English](../README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Українська](README.uk.md) · [Français](README.fr.md) · [Polski](README.pl.md) · [Türkçe](README.tr.md) · [한국어](README.ko.md) · **中文** · [日本語](README.ja.md)

**开发者：** [APTOFI.COM](https://aptofi.com)

Google Drive、OneDrive 或 Yandex Disk 没空间了，但你有空闲 HDD/SSD 和一台联网的 Windows 电脑？**APTOFI File Sharing 可以把它变成自己的私有文件服务器。**

## 主要优势

- 使用自己的硬盘，不再依赖额外云盘空间；
- 整个文件夹拖放上传，自动保留全部子目录和空目录；
- 大文件可续传、流式上传；
- 文件夹或鼠标选中的项目可直接流式生成 ZIP64 下载，不先创建完整临时压缩包；
- 多用户、配额、最后登录 IP、公共链接、用户及其全部数据永久删除；
- 回收站默认关闭，开启后保留 30 天；
- 支持 LAN、直接 IP/域名、VPS/reverse SSH；
- ACME DNS-01 + RFC2136/TSIG 自动 HTTPS；
- Windows 服务、托盘控制、10 种语言响应式 WEB。

## 环境 / 编译

Windows 7 SP1–Windows 11，.NET Framework 4.8。编译使用 Visual Studio 2022 + .NET Framework 4.8 Developer Pack。

```powershell
msbuild APTOFI.FileSharing.sln /m /restore /p:Configuration=Release /p:Platform="Any CPU"
```

输出：`src/APTOFI.FileSharing/bin/Release/afsharing.exe`。

## 首次运行

管理员运行 EXE → 添加存储目录如 `D:\FileSharing` → 配置管理员 → 修改秘密路径（如 `/my-admin-9F3x`、`/files-k7P2`）→ 选择网络模式 → 保存并启动。

## 仅局域网

```text
Bind: 0.0.0.0
Mode: Local
HTTP: 15745
HTTPS: 可信 LAN 可选
```

服务器 IP 为 `192.168.1.50` 时访问：`http://192.168.1.50:15745/files-k7P2`。无需域名。

## 公网 / 域名

Direct 模式需要在路由器把 TCP 端口转发到服务器。仅使用公网 IP + HTTP 不推荐，建议域名 + HTTPS 或 VPS。CGNAT 环境请用 VPS。

```text
Domain: files.example.net
HTTPS: 15746
DNS: RFC2136 / TSIG
DNS server: ns1.example.net
DNS zone: files.example.net
TSIG key/secret: DNS 服务商提供
Algorithm: HMAC-SHA256
Auto A update: on
```

测试 DNS、签发证书并转发 TCP `15746`。DNS-01 验证不要求公网 80/443。dynv6 常用更新服务器为 `ns1.dynv6.com`。

## VPS / 隧道

填写 Ubuntu/Debian VPS 的 SSH host/port/user、密码或密钥、远程端口（如 `18080`）和域名。APTOFI 会维护可自动重连的 reverse SSH tunnel。

## 使用 / 安全

把文件或完整文件夹树拖进浏览器即可上传。鼠标选择多个对象后右键可打包为一个 ZIP 下载。不要提交 `afsharing.settings`、数据库、TSIG secret、SSH key、证书 private key。

项目署名已明确写入文档和许可证：**APTOFI.COM — https://aptofi.com**。

## 许可证

根据 **APTOFI Attribution License 1.0** 可使用、修改、分发，但必须保留 **APTOFI.COM** 和 **https://aptofi.com** 署名。

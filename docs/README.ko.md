# APTOFI File Sharing 1.1.35

[English](../README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Українська](README.uk.md) · [Français](README.fr.md) · [Polski](README.pl.md) · [Türkçe](README.tr.md) · **한국어** · [中文](README.zh.md) · [日本語](README.ja.md)

**개발자:** [APTOFI.COM](https://aptofi.com)

Google Drive, OneDrive, Yandex Disk 용량은 부족하지만 남는 HDD/SSD와 네트워크에 연결된 Windows PC가 있나요? **APTOFI File Sharing은 그 PC를 개인 파일 공유 서버로 바꿉니다.**

## 주요 장점

- 유료 클라우드 대신 자신의 디스크 사용;
- 폴더 전체를 드래그하면 하위 폴더와 빈 폴더까지 구조 그대로 생성;
- 대용량 파일의 재개 가능한 스트리밍 업로드;
- 임시 전체 압축파일 없이 폴더/선택 항목을 ZIP64로 스트리밍 다운로드;
- 사용자, 할당량, 마지막 로그인 IP, 공개 링크, 사용자와 모든 데이터의 영구 삭제;
- 기본 비활성화된 휴지통, 활성화 시 30일 보관;
- LAN, 직접 IP/도메인, VPS/reverse SSH 지원;
- ACME DNS-01 + RFC2136/TSIG 자동 HTTPS;
- Windows 서비스/트레이, 10개 언어 반응형 WEB.

## 요구사항 / 빌드

Windows 7 SP1–Windows 11, .NET Framework 4.8. Visual Studio 2022 + .NET Framework 4.8 Developer Pack.

```powershell
msbuild APTOFI.FileSharing.sln /m /restore /p:Configuration=Release /p:Platform="Any CPU"
```

출력: `src/APTOFI.FileSharing/bin/Release/afsharing.exe`.

## 첫 실행

관리자 권한으로 EXE 실행 → `D:\FileSharing` 같은 저장소 추가 → 관리자 계정 설정 → 비밀 경로 변경(`/my-admin-9F3x`, `/files-k7P2`) → 네트워크 모드 선택 → 저장 및 시작.

## LAN 전용

```text
Bind: 0.0.0.0
Mode: Local
HTTP: 15745
HTTPS: 신뢰 LAN에서는 선택 사항
```

서버 IP가 `192.168.1.50`이면 `http://192.168.1.50:15745/files-k7P2`. 도메인이 필요 없습니다.

## 인터넷 / 도메인

Direct 모드에서는 라우터 TCP 포트를 서버로 포워딩합니다. 공인 IP의 HTTP만 사용하는 것은 권장하지 않으며 도메인+HTTPS 또는 VPS를 권장합니다. CGNAT이면 VPS 모드를 사용합니다.

```text
Domain: files.example.net
HTTPS: 15746
DNS: RFC2136 / TSIG
DNS server: ns1.example.net
DNS zone: files.example.net
TSIG key/secret: DNS 제공자에서 발급
Algorithm: HMAC-SHA256
Auto A update: on
```

DNS 테스트 후 인증서를 발급하고 TCP `15746`을 포워딩합니다. DNS-01 검증에는 공용 80/443 포트가 필요하지 않습니다. dynv6의 일반적인 update server는 `ns1.dynv6.com`입니다.

## VPS / 터널

Ubuntu/Debian VPS의 SSH host/port/user, password/key, remote port(예: `18080`), domain을 설정합니다. APTOFI는 재연결 가능한 reverse SSH tunnel을 유지합니다.

## 사용 / 보안

파일 또는 전체 폴더 트리를 브라우저로 드래그합니다. 여러 항목을 마우스로 선택한 뒤 오른쪽 클릭으로 하나의 ZIP으로 받을 수 있습니다. `afsharing.settings`, DB, TSIG secret, SSH key, 인증서 private key는 Git에 올리지 마십시오.

프로젝트 저작자 표기는 명확하게 문서화되어 있습니다: **APTOFI.COM — https://aptofi.com**.

## 라이선스

**APTOFI Attribution License 1.0**에 따라 사용/수정/배포할 수 있으며 **APTOFI.COM** 및 **https://aptofi.com** 표기가 필수입니다.

# scripts/ — Bộ script publish & deploy

Tất cả PowerShell (project Windows + dotnet). Chạy từ gốc repo hoặc trong `scripts/`.

| Script | Việc |
|---|---|
| `publish.ps1` | Build + publish ra `./publish/` (framework-dependent hoặc self-contained) |
| `docker-publish.ps1` | Build Docker image, tag, (tuỳ chọn) push lên registry |
| `deploy-iis.ps1` | Copy `./publish/` lên IIS server (local hoặc remote), recycle app pool |
| `release.ps1` | Tag commit + push lên `origin` |

---

## 1. Publish framework-dependent (mặc định)

```powershell
# Linux x64 (cho Docker / Linux host)
scripts\publish.ps1

# Windows x64 (cho IIS)
scripts\publish.ps1 -Runtime win-x64

# Self-contained (bundle .NET runtime, server không cần cài .NET)
scripts\publish.ps1 -Runtime linux-x64 -SelfContained

# Build xong nén luôn ra .zip cho dễ scp
scripts\publish.ps1 -Runtime linux-x64 -Zip
```

Kết quả nằm trong `./publish/`. **Không copy:**
- `appsettings.json` (chứa key thật — gitignore)
- `data/*` runtime state (reviews, mails, sessions, visa, deal cache…)
- `bin/`, `obj/`, `.vs/`, `TourkitAiProxy.Tests/`

**Có copy:**
- DLL/exe + dependencies
- `wwwroot/` (frontend no-build)
- `appsettings.example.json` (server admin đổi tên + điền key)
- `data/customers.seed.json` (seed read-only)

### Trên server sau publish

```bash
# Lần đầu
cp appsettings.example.json appsettings.json
nano appsettings.json   # điền OpenCode + 9routes API key, optional Redis ENC string

# Chạy
dotnet TourkitAiProxy.dll
# → mặc định bind http://localhost:5080
# → đặt sau reverse proxy (nginx/Caddy/IIS) để có HTTPS
```

---

## 2. Docker

Dockerfile sẵn ở gốc. Image expose port 8080.

```powershell
# Build local thôi
scripts\docker-publish.ps1

# Build + tag remote, KHÔNG push
scripts\docker-publish.ps1 -Registry "ghcr.io/trieuthang1991"

# Build + push (cần docker login trước)
scripts\docker-publish.ps1 -Registry "ghcr.io/trieuthang1991" -Push

# Tag riêng cho release
scripts\docker-publish.ps1 -Tag "v1.4.0" -Registry "ghcr.io/trieuthang1991" -Push
```

### Chạy container

```bash
docker run -d --name tourkit-ai \
  -p 5080:8080 \
  -e Providers__OpenCode__ApiKey="sk-..." \
  -e Providers__NineRoutes__ApiKey="..." \
  -e Redis__ConnectionString="ENC:..." \
  -v /srv/tourkit-data:/app/data \
  tourkit-ai-proxy:latest
```

---

## 3. Deploy lên IIS Windows

Yêu cầu: server đã cài [.NET 8 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/8.0/runtime?initial-os=windows).

```powershell
# 1. Publish Windows
scripts\publish.ps1 -Runtime win-x64

# 2. Deploy local (chạy trên IIS server, quyền Admin)
scripts\deploy-iis.ps1 -SitePath "C:\inetpub\wwwroot\tourkit-ai" -AppPool "TourkitAi"

# Test trước (không copy thật, không stop pool)
scripts\deploy-iis.ps1 -SitePath "C:\inetpub\wwwroot\tourkit-ai" -DryRun

# Deploy lên server khác qua share \\server\C$
scripts\deploy-iis.ps1 -Remote -Server "tk-app01" `
  -SitePath "C:\inetpub\wwwroot\tourkit-ai" -AppPool "TourkitAi"
```

Script tự:
- Stop app pool → robocopy mirror → start app pool
- **EXCLUDE tuyệt đối** `appsettings.json` + mọi `data/*.json` PII → không bao giờ đè state thật
- `-KeepData` giữ luôn cả folder `data/` server (lần đầu setup mới copy data lên)

---

## 4. Release tag

```powershell
scripts\release.ps1 -Version "1.4.0"
scripts\release.ps1 -Version "1.4.0" -Message "Cost guardrails + SmartMail v2"
scripts\release.ps1 -Version "1.4.0-rc.1" -NoPush   # chỉ tag local
```

Script tự verify working tree sạch, format `vX.Y.Z`, tag chưa tồn tại → tag + push origin.

---

## Lưu ý bảo mật

- `appsettings.json` **không bao giờ** được include trong publish/deploy — server admin tự tạo từ `appsettings.example.json`.
- Tất cả `data/*.json` chứa PII (mails, sessions, deal-cache, visa, ai-usage…) đã được robocopy EXCLUDE trong `deploy-iis.ps1`.
- `appsettings.json` cũng có thể chứa Redis connection string `ENC:` (Crypton-encrypted) — dùng cho cache shared/persistent.

# scripts/deploy-iis.ps1
# Deploy folder ./publish (đã build bằng publish.ps1) lên IIS Windows.
# Yêu cầu: chạy với quyền Administrator, đã cài .NET 8 Hosting Bundle trên IIS server.
#
# Usage:
#   scripts\deploy-iis.ps1 -SitePath "C:\inetpub\wwwroot\tourkit-ai"
#   scripts\deploy-iis.ps1 -SitePath "C:\inetpub\wwwroot\tourkit-ai" -AppPool "TourkitAi"
#   scripts\deploy-iis.ps1 -Remote -Server "tk-app01" -SitePath "C:\inetpub\wwwroot\tourkit-ai"

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SitePath,                # vd "C:\inetpub\wwwroot\tourkit-ai"
    [string]$AppPool = "DefaultAppPool",
    [string]$Source = "$PSScriptRoot\..\publish",
    [switch]$Remote,                  # deploy qua robocopy remote
    [string]$Server,                  # tên server khi -Remote
    [switch]$KeepData,                # giữ folder data/ trên server (không overwrite)
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

if (-not (Test-Path $Source)) {
    throw "Folder $Source chưa có. Chạy scripts\publish.ps1 -Runtime win-x64 trước."
}

# WebAdministration module — chỉ cần khi local IIS
if (-not $Remote) {
    if (-not (Get-Module -ListAvailable -Name WebAdministration)) {
        throw "Module WebAdministration không có. Cài qua: Enable-WindowsOptionalFeature -Online -FeatureName IIS-ManagementScriptingTools"
    }
    Import-Module WebAdministration -ErrorAction Stop
}

# Tính path đích thực sự
$TargetPath = if ($Remote) {
    if (-not $Server) { throw "Cần -Server khi -Remote" }
    "\\$Server\$($SitePath.Replace(':', '$'))"
} else { $SitePath }

Step "Stop app pool: $AppPool"
if ($DryRun) {
    Write-Host "  [DryRun] would: Stop-WebAppPool -Name $AppPool"
} elseif ($Remote) {
    Write-Host "  Run thủ công trên ${Server}: Stop-WebAppPool -Name $AppPool"
} else {
    if ((Get-WebAppPoolState -Name $AppPool).Value -eq "Started") {
        Stop-WebAppPool -Name $AppPool
        Start-Sleep -Seconds 3
    } else {
        Write-Host "  App pool đã stopped" -ForegroundColor Yellow
    }
}

# Robocopy excludes — TUYỆT ĐỐI không đè data/ chứa PII + appsettings.json prod.
# QUAN TRỌNG: /XF cho FILE, /XD cho FOLDER. Dùng nhầm /XD cho file → exclude KHÔNG hiệu lực,
# và vì có /MIR ở dưới, robocopy sẽ XÓA file đó ở destination (vd: tk-sessions.json → user mất đăng nhập).
$ExcludeFiles = @(
    "appsettings.json", "appsettings.Production.json", "appsettings.Development.json",
    # SmartMail / Visa / Deal / Reviews / Provider keys — runtime state, chứa PII/creds
    "data\mails.json", "data\mail-account.json", "data\mail-sync.json",
    "data\tk-sessions.json", "data\reviews.json", "data\provider-keys.json",
    "data\visa-assessments.json", "data\deal-cache.json",
    # Quota & usage counters per-tenant — đè = mất dữ liệu đếm AI / reset quota đang chạy
    "data\tenant-quota.json",
    # Logs / traces (rotation: *.jsonl.YYYY-MM-DD…)
    "data\ai-usage.jsonl", "data\chat-unresolved.jsonl", "data\workflow-traces.jsonl",
    "*.jsonl.*",   # rotated logs — robocopy /XF có hiểu wildcard cho tên file
    "*.migrated"   # *.json.migrated từ MultiTenantMigration / cache invalidation
)
$ExcludeDirs = @(
    "data\visa-files",    # PII (ảnh hồ sơ visa) per-tenant
    "data\legacy-backup"  # MultiTenantMigration đẩy file JSON cũ vào đây
)
if ($KeepData) { $ExcludeDirs += "data" }  # giữ NGUYÊN folder data/ trên server
$Exclude = @("/XF") + $ExcludeFiles + @("/XD") + $ExcludeDirs

Step "Robocopy → $TargetPath"
$rcArgs = @($Source, $TargetPath, "/MIR", "/MT:8", "/R:2", "/W:2", "/NP", "/NDL", "/NFL") + $Exclude
if ($DryRun) {
    Write-Host "  [DryRun] robocopy $($rcArgs -join ' ')"
} else {
    if (-not (Test-Path $TargetPath)) { New-Item -ItemType Directory -Path $TargetPath -Force | Out-Null }
    & robocopy @rcArgs
    # robocopy exit code 0-7 = OK (xem msdocs); 8+ = lỗi
    if ($LASTEXITCODE -ge 8) { throw "robocopy lỗi (exit $LASTEXITCODE)" }
}

Step "Start app pool: $AppPool"
if ($DryRun) {
    Write-Host "  [DryRun] would: Start-WebAppPool -Name $AppPool"
} elseif ($Remote) {
    Write-Host "  Run thủ công trên ${Server}: Start-WebAppPool -Name $AppPool"
} else {
    Start-WebAppPool -Name $AppPool
}

Step "Done"
Write-Host "  • Path : $TargetPath"
Write-Host "  • Pool : $AppPool"
Write-Host "  • Health: curl http://<host>/healthz để verify"

# scripts/diagnose-iis.ps1
# Chẩn đoán nhanh lỗi 403.14 / 500.* khi deploy ASP.NET Core lên IIS.
# Chạy trên server: PowerShell as Administrator.
#
# Usage:
#   scripts\diagnose-iis.ps1
#   scripts\diagnose-iis.ps1 -SitePath "C:\inetpub\wwwroot\Tour_AI"
#
# Hoặc tải về + chạy trực tiếp (server không clone repo):
#   iwr https://raw.githubusercontent.com/trieuthang1991/tourkit-ai-proxy/main/scripts/diagnose-iis.ps1 -OutFile diag.ps1
#   .\diag.ps1 -SitePath "C:\inetpub\wwwroot\Tour_AI"

[CmdletBinding()]
param(
    [string]$SitePath = "C:\inetpub\wwwroot\Tour_AI"
)

function Section($n, $title) { Write-Host "`n=== $n. $title ===" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "  [OK] $msg"  -ForegroundColor Green }
function Bad($msg)  { Write-Host "  [BAD] $msg" -ForegroundColor Red }
function Warn($msg) { Write-Host "  [WARN] $msg" -ForegroundColor Yellow }

Write-Host "Chẩn đoán IIS deployment cho $SitePath" -ForegroundColor White

# 1. web.config
Section 1 "web.config có không?"
$wc = Join-Path $SitePath "web.config"
if (Test-Path $wc) {
    Ok "Có"
    $aspNetCore = Select-String -Path $wc -Pattern "aspNetCore" -SimpleMatch -Quiet
    if ($aspNetCore) { Ok "Có <aspNetCore> handler" }
    else { Bad "web.config KHONG co <aspNetCore> - khong phai ASP.NET Core publish output. Build lai: scripts\publish.ps1 -Runtime win-x64" }
} else {
    Bad "KHONG CO web.config!"
    Bad "Nguyen nhan: ban build linux-x64 thay vi win-x64."
    Bad "Fix: scripts\publish.ps1 -Runtime win-x64 (tren may dev), roi deploy lai."
}

# 2. DLL chinh
Section 2 "DLL chinh"
$dll = Join-Path $SitePath "TourkitAiProxy.dll"
if (Test-Path $dll) { Ok "TourkitAiProxy.dll co" } else { Bad "Thieu TourkitAiProxy.dll - publish chua chay xong" }

# 3. appsettings
Section 3 "appsettings"
$app = Join-Path $SitePath "appsettings.json"
$example = Join-Path $SitePath "appsettings.example.json"
if (Test-Path $app) { Ok "appsettings.json co" }
elseif (Test-Path $example) { Bad "Chua co appsettings.json - copy appsettings.example.json roi dien API key" }
else { Bad "Khong co ca example - publish thieu" }

# 4. Hosting Bundle (AspNetCoreModuleV2)
Section 4 ".NET 8 Hosting Bundle (AspNetCoreModuleV2)"
try {
    Import-Module WebAdministration -ErrorAction Stop
    $mod = Get-WebGlobalModule | Where-Object { $_.Name -like "*AspNetCore*" }
    if ($mod) {
        Ok "Module AspNetCore co: $($mod.Name -join ', ')"
    } else {
        Bad "KHONG CO AspNetCoreModuleV2!"
        Bad "Fix: Tai .NET 8 Hosting Bundle:"
        Bad "     https://dotnet.microsoft.com/download/dotnet/8.0/runtime"
        Bad "     Chon 'ASP.NET Core Runtime 8.0.x > Hosting Bundle' (~17 MB)"
        Bad "     Cai xong: iisreset"
    }
} catch {
    Bad "Khong load duoc WebAdministration module"
}

# 5. .NET runtimes
Section 5 ".NET runtimes cai tren server"
$rt = dotnet --list-runtimes 2>&1
if ($rt) {
    Write-Host "  $rt" -ForegroundColor Gray
    $hasNet8 = $rt -match "Microsoft.AspNetCore.App 8\."
    if ($hasNet8) { Ok ".NET 8 ASP.NET Core runtime co" }
    else { Bad "Chua co .NET 8 ASP.NET Core runtime - can cai Hosting Bundle" }
} else { Bad "dotnet CLI khong chay - chua cai .NET runtime" }

# 6. Site + AppPool
Section 6 "Site + AppPool"
try {
    Import-Module WebAdministration -ErrorAction Stop
    $sites = Get-Website | Where-Object { $_.PhysicalPath -like "*$(Split-Path $SitePath -Leaf)*" }
    if ($sites) {
        foreach ($s in $sites) {
            Write-Host "  Site: $($s.Name) | State: $($s.State) | Pool: $($s.applicationPool)" -ForegroundColor White
            $poolPath = "IIS:\AppPools\$($s.applicationPool)"
            if (Test-Path $poolPath) {
                $p = Get-Item $poolPath
                Write-Host "  Pool: $($p.Name) | State: $($p.State) | Runtime: '$($p.managedRuntimeVersion)' | Mode: $($p.managedPipelineMode)" -ForegroundColor White
                if ($p.managedRuntimeVersion -eq "v4.0" -or $p.managedRuntimeVersion -eq "v2.0") {
                    Bad "App pool dang chay .NET Framework, can chuyen 'No Managed Code'"
                    Bad "Fix: IIS Manager > Application Pools > $($p.Name) > Basic Settings > .NET CLR version: No Managed Code"
                } elseif (-not $p.managedRuntimeVersion) {
                    Ok "App pool dung mode 'No Managed Code'"
                }
            }
        }
    } else { Warn "Khong tim thay site nao tro toi $SitePath" }
} catch { Bad "WebAdministration loi: $_" }

# 7. Phan quyen folder
Section 7 "Phan quyen folder"
try {
    $acl = Get-Acl $SitePath
    $iisRead = $acl.Access | Where-Object { $_.IdentityReference -like "*IIS_IUSRS*" -or $_.IdentityReference -like "*IIS AppPool*" }
    if ($iisRead) { Ok "IIS account co quyen tren folder" }
    else { Warn "Khong thay IIS_IUSRS hay IIS AppPool trong ACL — co the can:" }
    Warn "  icacls $SitePath /grant `"IIS_IUSRS:(OI)(CI)R`" /T"
} catch { Warn "Khong doc duoc ACL" }

# 8. Test chay truc tiep
Section 8 "Test chay app truc tiep (bypass IIS)"
Write-Host "  Chay thu cong de xem app khoi dong dươc khong:" -ForegroundColor Yellow
Write-Host "    cd $SitePath" -ForegroundColor Gray
Write-Host "    dotnet TourkitAiProxy.dll" -ForegroundColor Gray
Write-Host "  Neu chay duoc + ket noi http://localhost:5080/healthz duoc → loi nam o IIS" -ForegroundColor Gray
Write-Host "  Neu loi → app/config sai, doc error message tren console" -ForegroundColor Gray

Write-Host "`n=== Tom tat ===" -ForegroundColor Cyan
Write-Host "Gui screenshot toan bo output nay cho dev de fix nhanh." -ForegroundColor White

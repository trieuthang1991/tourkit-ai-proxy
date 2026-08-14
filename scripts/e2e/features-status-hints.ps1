# scripts/e2e/features-status-hints.ps1
# E2E TINH NANG (DOC) - goi y "trang thai nao con phai lam" cho form cau hinh ban tin.
#
# Chay:
#   .\scripts\e2e\features-status-hints.ps1 -SessionId <sid>
#   .\scripts\e2e\features-status-hints.ps1 -SidFile $env:TEMP\tk_sid.txt
#
# Lay SessionId: dang nhap app roi mo DevTools Console:
#   localStorage.getItem('tourkit_tk_session')
#
# AN TOAN - script nay CHI GOI GET, khong sua cau hinh nao. Lan chay dau co the ton 1-2 luot AI
# (moi loai trang thai 1 luot) neu cache chua co; nhung lan sau doc tu cache, khong ton them.
#
# ASCII-only co y: PowerShell 5.1 doc .ps1 khong BOM se lam meo tieng Viet -> so khop chuoi
# tieng Viet luon that bai du API tra dung. Moi assertion o day so theo MA trang thai.
#
# Vi sao can E2E rieng: day la cho DUY NHAT trong he thong ma mot cau tra loi cua AI di thang
# vao cau hinh mac dinh. Sai o day khong bao loi gi - chi la sang hom sau ban tin nhac goi lai
# don da huy. Kiem bang mat thi chi thay "co danh sach roi", khong thay danh sach do dung hay sai.

param(
  [string] $BaseUrl   = 'http://localhost:5080',
  [string] $SessionId = '',
  [string] $SidFile   = '',
  [switch] $ShowList
)

$ErrorActionPreference = 'Stop'

if (-not $SessionId -and $SidFile -and (Test-Path $SidFile)) {
  $SessionId = (Get-Content $SidFile -Raw).Trim()
}
if (-not $SessionId) {
  Write-Host "THIEU SessionId. Dung -SessionId <sid> hoac -SidFile <duong-dan>." -ForegroundColor Red
  exit 2
}

$headers = @{ 'X-Session-Id' = $SessionId }
$pass = 0; $fail = 0
function Ok  ($m) { $script:pass++; Write-Host ("  PASS  " + $m) -ForegroundColor Green }
function Bad ($m) { $script:fail++; Write-Host ("  FAIL  " + $m) -ForegroundColor Red }
function Info($m) { Write-Host ("  ..    " + $m) -ForegroundColor DarkGray }
function Check($label, $cond, $detail) { if ($cond) { Ok $label } else { Bad ("$label -> $detail") } }

function Get-Statuses ($path) {
  Invoke-RestMethod -Uri "$BaseUrl/api/v1/workflows/$path" -Headers $headers -Method Get -TimeoutSec 120
}

Write-Host ""
Write-Host "=== E2E: goi y trang thai cho form ban tin ===" -ForegroundColor Cyan
Write-Host ("    base: {0}" -f $BaseUrl)
Write-Host ""

# -- 0. Phien con song khong ----------------------------------------------------
try {
  $null = Invoke-RestMethod -Uri "$BaseUrl/api/v1/session" -Headers $headers -Method Get
  Ok "Phien dang nhap hop le"
} catch {
  Bad "Phien khong hop le (401?) - dang nhap lai roi lay sid moi"
  exit 1
}

# -- 1. Chua dang nhap thi phai 401, khong duoc tra danh sach cua cong ty khac ---
try {
  $null = Invoke-RestMethod -Uri "$BaseUrl/api/v1/workflows/deal-statuses" -Method Get -TimeoutSec 30
  Bad "Goi khong kem phien van tra du lieu (phai 401)"
} catch {
  $code = 0
  if ($_.Exception.Response) { $code = [int] $_.Exception.Response.StatusCode }
  Check "Khong co phien -> 401" ($code -eq 401) ("nhan status $code")
}

# -- 2..N. Kiem tung loai trang thai --------------------------------------------
# alwaysClosed: ma ma CRM tu coi la ket thuc (chinh CRM dung dung 2 ma nay cho tab "tre han"
# va co IsLate), nen goi y ma xep chung vao "con phai lam" la sai chac chan.
$kinds = @(
  @{ Name = 'Co hoi ban hang'; Path = 'deal-statuses'; AlwaysClosed = @() },
  @{ Name = 'Cong viec';       Path = 'task-statuses'; AlwaysClosed = @(4, 5) }
)

foreach ($k in $kinds) {
  Write-Host ""
  Write-Host ("-- {0} ({1})" -f $k.Name, $k.Path) -ForegroundColor Yellow

  $sw = [Diagnostics.Stopwatch]::StartNew()
  try { $r = Get-Statuses $k.Path } catch { Bad ("Goi that bai: " + $_.Exception.Message); continue }
  $sw.Stop()
  $firstMs = $sw.ElapsedMilliseconds

  if ($r.error) { Info ("server bao loi phu: " + $r.error) }

  $items = @($r.items)
  Check "Tra ve danh sach trang thai" ($items.Count -gt 0) "items rong - CRM chua tra ve gi"
  if ($items.Count -eq 0) { continue }

  $values = @($items | ForEach-Object { [int] $_.value })
  $badItem = @($items | Where-Object { -not $_.label -or [int]$_.value -le 0 }).Count
  Check "Moi trang thai deu co ma > 0 va co ten" ($badItem -eq 0) "$badItem dong thieu ma hoac ten"
  Check "Ma trang thai khong trung nhau" (($values | Select-Object -Unique).Count -eq $values.Count) "co ma lap lai"

  if ($ShowList) { $items | ForEach-Object { Info ("{0,-4} {1}" -f $_.value, $_.label) } }

  $open = @($r.openSuggested)
  if ($open.Count -eq 0) {
    # KHONG phai loi: AI hong / chua khai khoa model thi client tu doan theo ten. Nhung phai
    # noi ro ra, vi im lang o day nghia la ca form dang chay bang luoi do ma khong ai biet.
    Info "Khong co openSuggested - giao dien se tu doan theo TEN trang thai (luoi do)"
    Bad "Thieu goi y cua may chu (kiem Models:StatusSemantics + khoa provider)"
    continue
  }

  Ok ("Co goi y cua may chu: {0}/{1} trang thai con phai lam ({2} ms)" -f $open.Count, $items.Count, $firstMs)

  $unknown = @($open | Where-Object { $values -notcontains [int] $_ })
  Check "Goi y khong bia ma la" ($unknown.Count -eq 0) ("ma khong co trong danh sach: " + ($unknown -join ','))
  Check "Goi y khong lap ma" ((@($open) | Select-Object -Unique).Count -eq $open.Count) "co ma lap lai"
  Check "Goi y khong om het ca danh sach" ($open.Count -lt $items.Count) `
        "moi ma deu duoc coi la 'con phai lam' - bo loc nay coi nhu vo tac dung"

  foreach ($mustClose in $k.AlwaysClosed) {
    if ($values -contains $mustClose) {
      Check ("Ma {0} phai duoc coi la DA XONG" -f $mustClose) ($open -notcontains $mustClose) `
            "AI xep nham vao 'con phai lam' - ban tin se nhac ca viec da hoan thanh/da huy"
    }
  }

  # Lan goi thu 2: phai lay tu cache (khong hoi AI lai). Nguong 1500ms rong rai de khong
  # gay bao dong gia khi CRM cham - AI that su thi thuong 2-10 giay.
  $sw2 = [Diagnostics.Stopwatch]::StartNew()
  $r2 = Get-Statuses $k.Path
  $sw2.Stop()
  $secondMs = $sw2.ElapsedMilliseconds
  $same = (@($r2.openSuggested) -join ',') -eq (@($open) -join ',')
  Check "Goi lai cho ket qua y het" $same "lan 2 khac lan 1 - goi y khong on dinh"
  Check ("Goi lai khong hoi AI nua ({0} ms)" -f $secondMs) ($secondMs -lt 1500) `
        "lan 2 van cham nhu lan dau ($firstMs ms) - cache khong an, moi lan mo trang la ton mot luot AI"
}

Write-Host ""
Write-Host ("=== KET QUA: {0} PASS / {1} FAIL ===" -f $pass, $fail) -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 }
exit 0

# scripts/e2e/features-workflow-config.ps1
# E2E TINH NANG (DONG) - luu cau hinh workflow tu trang So do luong (/flow-preview).
#
# Chay:
#   .\scripts\e2e\features-workflow-config.ps1 -SessionId <sid>
#   .\scripts\e2e\features-workflow-config.ps1 -SessionId <sid> -Type deal-auto-review
#   .\scripts\e2e\features-workflow-config.ps1 -SidFile $env:TEMP\tk_sid.txt
#
# Lay SessionId: dang nhap app roi mo DevTools Console:
#   localStorage.getItem('tourkit_tk_session')
#
# AN TOAN - day la config THAT cua tenant that:
#   - CHI doi 'intervalMinutes' (tan suat chay). KHONG dung toi cac option doi HANH VI
#     (autoReply / replyMode / alertCooling...) vi bat nham co the gui mail that cho khach.
#   - Luu ban goc ra file TRUOC khi sua; khoi phuc o cuoi VA trong khoi finally.
#   - In duong dan file backup de tu khoi phuc tay neu script chet giua chung.

param(
  [string] $BaseUrl   = 'http://localhost:5080',
  [string] $SessionId = '',
  [string] $SidFile   = '',
  [string] $Type      = 'mail-auto-sync'
)

$ErrorActionPreference = 'Stop'

if (-not $SessionId -and $SidFile -and (Test-Path $SidFile)) {
  $SessionId = (Get-Content $SidFile -Raw).Trim()
}
if (-not $SessionId) {
  Write-Host "THIEU SessionId. Dung -SessionId <sid> hoac -SidFile <duong-dan>." -ForegroundColor Red
  exit 2
}

$headers = @{ 'X-Session-Id' = $SessionId; 'Content-Type' = 'application/json' }
$pass = 0; $fail = 0
function Ok  ($m) { $script:pass++; Write-Host ("  PASS  " + $m) -ForegroundColor Green }
function Bad ($m) { $script:fail++; Write-Host ("  FAIL  " + $m) -ForegroundColor Red }
function Info($m) { Write-Host ("  ..    " + $m) -ForegroundColor DarkGray }

function Get-Workflows {
  Invoke-RestMethod -Uri "$BaseUrl/api/v1/workflows" -Headers $headers -Method Get
}
function Put-Config ($t, $body) {
  Invoke-RestMethod -Uri "$BaseUrl/api/v1/workflows/$t" -Headers $headers -Method Put `
    -Body ($body | ConvertTo-Json -Depth 10 -Compress)
}

Write-Host ""
Write-Host "=== E2E DONG: luu cau hinh workflow ===" -ForegroundColor Cyan
Write-Host ("    target: {0}   base: {1}" -f $Type, $BaseUrl)
Write-Host ""

# -- 0. Phien con song khong ----------------------------------------------------
try {
  $null = Invoke-RestMethod -Uri "$BaseUrl/api/v1/session" -Headers $headers -Method Get
  Ok "Phien dang nhap hop le"
} catch {
  Bad "Phien khong hop le (401?) - dang nhap lai roi lay sid moi"
  exit 1
}

# -- 1. Doc cau hinh hien tai ---------------------------------------------------
$all = Get-Workflows
$wf  = $all.items | Where-Object { $_.type -eq $Type }
if (-not $wf) { Bad "Khong tim thay workflow '$Type' trong danh sach"; exit 1 }
Ok ("Doc duoc cau hinh: enabled={0} interval={1}" -f $wf.enabled, $wf.intervalMinutes)

$origEnabled  = [bool] $wf.enabled
$origInterval = [int]  $wf.intervalMinutes
$origOptions  = $wf.options

$backup = Join-Path $env:TEMP ("wf-backup-{0}.json" -f $Type)
@{ type = $Type; enabled = $origEnabled; intervalMinutes = $origInterval; options = $origOptions } |
  ConvertTo-Json -Depth 10 | Set-Content -Path $backup -Encoding utf8
Info ("Ban goc da luu: {0}" -f $backup)

# Chon gia tri interval KHAC ban goc (trong danh sach hop le cua UI)
$allowed = @(5,10,15,30,60,180,360,720,1440)
$newInterval = ($allowed | Where-Object { $_ -ne $origInterval } | Select-Object -First 1)

try {
  # -- 2. Luu interval moi ------------------------------------------------------
  Put-Config $Type @{ enabled = $origEnabled; intervalMinutes = $newInterval; options = $origOptions }
  Ok ("PUT interval {0} -> {1} khong loi" -f $origInterval, $newInterval)

  # -- 3. Doc lai: da doi that chua ---------------------------------------------
  $after = (Get-Workflows).items | Where-Object { $_.type -eq $Type }
  if ([int]$after.intervalMinutes -eq $newInterval) { Ok "Doc lai thay interval moi - LUU THAT SU AN" }
  else { Bad ("Doc lai van la {0} - luu KHONG an" -f $after.intervalMinutes) }

  if ([bool]$after.enabled -eq $origEnabled) { Ok "Trang thai bat/tat khong bi doi ngoai y muon" }
  else { Bad "Trang thai bat/tat bi thay doi ngoai y muon" }

  # -- 4. Options khong bi mat khi chi doi interval -----------------------------
  $o1 = ($origOptions  | ConvertTo-Json -Depth 10 -Compress)
  $o2 = ($after.options | ConvertTo-Json -Depth 10 -Compress)
  if ($o1 -eq $o2) { Ok "Cac tuy chon giu nguyen (khong bi ghi de mat)" }
  else {
    Bad "Cac tuy chon BI DOI khi chi luu interval"
    Info ("truoc: " + $o1)
    Info ("sau  : " + $o2)
  }

  # -- 4b. LUU TUNG PHAN: options=null -> backend GIU NGUYEN options cu (COALESCE).
  #     Day la co che trang So do dung khi user chi doi interval o 1 node,
  #     de khong ghi de len cac tuy chon ma minh khong dung toi.
  Put-Config $Type @{ enabled = $origEnabled; intervalMinutes = $newInterval; options = $null } | Out-Null
  $partial = (Get-Workflows).items | Where-Object { $_.type -eq $Type }
  $o3 = ($partial.options | ConvertTo-Json -Depth 10 -Compress)
  if ($o1 -eq $o3) { Ok "Gui options=null -> options cu GIU NGUYEN (luu tung phan an toan)" }
  else { Bad "Gui options=null lai lam MAT options cu"; Info ("truoc: " + $o1); Info ("sau  : " + $o3) }

  # -- 5. Workflow la nhan 404, khong phai 500 ----------------------------------
  try {
    Put-Config 'khong-ton-tai-abc' @{ enabled = $false; intervalMinutes = 15 }
    Bad "PUT workflow khong ton tai LAI THANH CONG (dang le phai bi tu choi)"
  } catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -eq 404 -or $code -eq 400) { Ok ("Workflow la bi tu choi dung cach (HTTP {0})" -f $code) }
    else { Bad ("Workflow la tra HTTP {0} - dang le 400/404" -f $code) }
  }

  # -- 6. Khong co phien -> 401 -------------------------------------------------
  try {
    Invoke-RestMethod -Uri "$BaseUrl/api/v1/workflows" -Method Get | Out-Null
    Bad "Goi KHONG kem phien van tra du lieu - thieu chan quyen"
  } catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -eq 401) { Ok "Khong co phien -> 401 dung nhu thiet ke" }
    else { Bad ("Khong co phien tra HTTP {0} - dang le 401" -f $code) }
  }
}
finally {
  # -- 7. Khoi phuc ban goc (LUON chay, ke ca khi loi giua chung) ---------------
  try {
    Put-Config $Type @{ enabled = $origEnabled; intervalMinutes = $origInterval; options = $origOptions }
    $back = (Get-Workflows).items | Where-Object { $_.type -eq $Type }
    if ([int]$back.intervalMinutes -eq $origInterval -and [bool]$back.enabled -eq $origEnabled) {
      Ok "Da khoi phuc ban goc"
    } else {
      Bad ("KHOI PHUC KHONG DUNG - khoi phuc tay tu {0}" -f $backup)
    }
  } catch {
    Bad ("Khoi phuc THAT BAI: {0} - khoi phuc tay tu {1}" -f $_.Exception.Message, $backup)
  }
}

Write-Host ""
Write-Host ("{0} pass | {1} fail" -f $pass, $fail) -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
exit $(if ($fail) { 1 } else { 0 })

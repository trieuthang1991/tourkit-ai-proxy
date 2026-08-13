# scripts/e2e/features-digest.ps1
# E2E TINH NANG (DONG) - dang ky nhan ban tin (/api/v1/digest/*) + bang tin trong app (/api/v1/insights/*).
#
# Chay:
#   .\scripts\e2e\features-digest.ps1 -SessionId <sid>
#   .\scripts\e2e\features-digest.ps1 -SidFile $env:TEMP\tk_sid.txt
#
# Lay SessionId: dang nhap app roi mo DevTools Console:
#   localStorage.getItem('tourkit_tk_session')
#
# ASCII-only co y: PowerShell 5.1 doc .ps1 khong BOM se lam meo tieng Viet -> so khop chuoi
# tieng Viet luon that bai dù API tra dung. Vi vay moi assertion o day so theo MA (briefType /
# kind / ma loi), khong so theo chu tieng Viet.
#
# AN TOAN - day la dang ky THAT cua user that:
#   - Luu ban goc dang ky ra file TRUOC khi sua; khoi phuc o cuoi VA trong khoi finally.
#   - CHI gui THU (POST /test) - endpoint do CO Y khong cap nhat moc "da gui hom nay",
#     nen khong lam mat ban tin thuc sang mai.
#   - Kenh email/telegram/zalo: chi bat khi da co san thong tin nhan trong ban goc; script
#     KHONG tu dien dia chi nguoi khac vao.

param(
  [string] $BaseUrl   = 'http://localhost:5080',
  [string] $SessionId = '',
  [string] $SidFile   = ''
)

$ErrorActionPreference = 'Stop'

if (-not $SessionId -and $SidFile -and (Test-Path $SidFile)) {
  $SessionId = (Get-Content $SidFile -Raw).Trim()
}
if (-not $SessionId) {
  Write-Host "THIEU SessionId. Dung -SessionId <sid> hoac -SidFile <duong-dan>." -ForegroundColor Red
  exit 2
}

$H = @{ 'X-Session-Id' = $SessionId }
$pass = 0; $fail = 0
function Ok  ($m) { $script:pass++; Write-Host ("  PASS  " + $m) -ForegroundColor Green }
function Bad ($m) { $script:fail++; Write-Host ("  FAIL  " + $m) -ForegroundColor Red }
function Info($m) { Write-Host ("  ..    " + $m) -ForegroundColor DarkGray }
function Check($label, $cond, $detail) { if ($cond) { Ok $label } else { Bad ("$label -> $detail") } }

function Req($method, $path, $body, $headers) {
  $p = @{ Uri = "$BaseUrl/api/v1$path"; Method = $method; Headers = $headers; TimeoutSec = 60 }
  if ($body) { $p.Body = ($body | ConvertTo-Json -Depth 6); $p.ContentType = 'application/json' }
  try { return @{ Code = 200; Data = (Invoke-RestMethod @p) } }
  catch {
    $code = 0
    if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    $txt = ''
    try { $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream()); $txt = $sr.ReadToEnd() } catch {}
    return @{ Code = $code; Error = $txt }
  }
}

# ── Sao luu dang ky goc ─────────────────────────────────────────────────────────
$backupPath = Join-Path $env:TEMP ("tk_digest_backup_" + (Get-Date -Format 'yyyyMMdd_HHmmss') + ".json")
$orig = Req GET '/digest/subscriptions' $null $H
if ($orig.Code -ne 200) {
  Write-Host "Khong doc duoc dang ky hien tai (code=$($orig.Code)). Dung lai." -ForegroundColor Red
  exit 2
}
$orig.Data.items | ConvertTo-Json -Depth 8 | Set-Content -Path $backupPath -Encoding utf8
Info "Ban goc luu tai: $backupPath"

function RestoreOriginal {
  foreach ($s in @($orig.Data.items)) {
    $body = @{
      enabled         = $s.enabled
      sendHourLocal   = $s.sendHourLocal
      channelInApp    = $s.channelInApp
      channelEmail    = $s.channelEmail
      email           = $s.email
      channelTelegram = $s.channelTelegram
      telegramChatId  = $s.telegramChatId
      channelZalo     = $s.channelZalo
      zaloUserId      = $s.zaloUserId
    }
    $r = Req PUT "/digest/subscriptions/$($s.briefType)" $body $H
    if ($r.Code -ne 200) { Write-Host "  !! Khoi phuc $($s.briefType) that bai (code=$($r.Code)) - dung file $backupPath" -ForegroundColor Red }
  }
}

try {
  Write-Host ""
  Write-Host "1. Khong co phien -> 401" -ForegroundColor Cyan
  Check 'GET subscriptions khong phien = 401' ((Req GET '/digest/subscriptions' $null @{}).Code -eq 401) 'khac 401'
  Check 'GET unread-count khong phien = 401' ((Req GET '/insights/unread-count' $null @{}).Code -eq 401) 'khac 401'
  Check 'Phien rac = 401' ((Req GET '/digest/subscriptions' $null @{ 'X-Session-Id' = 'khong-ton-tai' }).Code -eq 401) 'khac 401'

  Write-Host "2. Doc danh sach dang ky" -ForegroundColor Cyan
  $r = Req GET '/digest/subscriptions' $null $H
  Check 'GET subscriptions = 200' ($r.Code -eq 200) "code=$($r.Code)"
  Check 'Co danh muc 2 loai ban tin' (@($r.Data.briefTypes).Count -eq 2) "$(@($r.Data.briefTypes).Count)"

  Write-Host "3. Kiem tra dau vao khi luu" -ForegroundColor Cyan
  # Loai la phai bi chan: luu duoc se tao ban ghi ma KHONG workflow nao doc -> user cho mai khong thay tin.
  Check 'Loai ban tin la = 400' `
    ((Req PUT '/digest/subscriptions/hacker-brief' @{ enabled=$true; sendHourLocal=7; channelInApp=$true } $H).Code -eq 400) 'khac 400'
  Check 'Bat ban tin ma 0 kenh = 400' `
    ((Req PUT '/digest/subscriptions/sale-brief' @{ enabled=$true; sendHourLocal=7; channelInApp=$false } $H).Code -eq 400) 'khac 400'
  Check 'Bat email ma trong dia chi = 400' `
    ((Req PUT '/digest/subscriptions/sale-brief' @{ enabled=$true; sendHourLocal=7; channelInApp=$true; channelEmail=$true; email='' } $H).Code -eq 400) 'khac 400'

  Write-Host "4. Luu that + doc lai" -ForegroundColor Cyan
  Check 'Luu sale-brief = 200' ((Req PUT '/digest/subscriptions/sale-brief' @{ enabled=$true; sendHourLocal=6; channelInApp=$true } $H).Code -eq 200) 'khac 200'
  $sale = (Req GET '/digest/subscriptions' $null $H).Data.items | Where-Object { $_.briefType -eq 'sale-brief' }
  Check 'Doc lai thay dang ky' ($null -ne $sale) 'khong thay'
  Check 'Gio gui luu dung = 6' ($sale.sendHourLocal -eq 6) "$($sale.sendHourLocal)"

  Write-Host "5. Gio rac bi kep ve 7 (khong phai bo qua im lang)" -ForegroundColor Cyan
  $null = Req PUT '/digest/subscriptions/sale-brief' @{ enabled=$true; sendHourLocal=99; channelInApp=$true } $H
  $sale = (Req GET '/digest/subscriptions' $null $H).Data.items | Where-Object { $_.briefType -eq 'sale-brief' }
  Check 'Gio 99 -> kep ve 7' ($sale.sendHourLocal -eq 7) "$($sale.sendHourLocal)"

  Write-Host "6. Ban tin dieu hanh KHONG bi proxy chan quyen" -ForegroundColor Cyan
  # TourKit.Api tu co pham vi so theo tai khoan (DashboardService.ResolveSpUserIdAsync).
  # Proxy tu gac them se chan oan nguoi co quyen bao cao ma khong co CH_XEM_ALL.
  Check 'Luu ceo-brief = 200' ((Req PUT '/digest/subscriptions/ceo-brief' @{ enabled=$true; sendHourLocal=7; channelInApp=$true } $H).Code -eq 200) 'khac 200'

  Write-Host "6b. C5: moi nguoi chi 1 loai theo vai tro (bat loai nay -> tu tat loai kia)" -ForegroundColor Cyan
  # Vua bat ceo-brief o tren, ma sale-brief da bat o muc 4/5 -> luat server phai TU TAT sale-brief.
  # Server enforce (khong tin client): du goi API tay cung khong the bat ca 2 loai.
  $c5subs = (Req GET '/digest/subscriptions' $null $H).Data.items
  $c5sale = $c5subs | Where-Object { $_.briefType -eq 'sale-brief' }
  $c5ceo  = $c5subs | Where-Object { $_.briefType -eq 'ceo-brief' }
  Check 'ceo-brief dang bat' ($c5ceo.enabled -eq $true) "ceo.enabled=$($c5ceo.enabled)"
  Check 'sale-brief TU TAT khi bat ceo (luat 1 loai)' ($c5sale.enabled -eq $false) "sale.enabled=$($c5sale.enabled)"

  Write-Host "7. Gui thu (khong dung toi moc 'da gui hom nay')" -ForegroundColor Cyan
  $before = (Req GET '/insights/unread-count' $null $H).Data.count
  $t0 = (Get-Date).ToUniversalTime().AddMinutes(-2)
  $r = Req POST '/digest/subscriptions/sale-brief/test' $null $H
  Check 'Gui thu = 200' ($r.Code -eq 200) "code=$($r.Code)"
  Check 'Gui thu bao ok' ($r.Data.ok -eq $true) "$($r.Data | ConvertTo-Json -Compress)"
  Info "summary: $($r.Data.summary)"
  Check 'Gui thu loai la = 400' ((Req POST '/digest/subscriptions/hacker-brief/test' $null $H).Code -eq 400) 'khac 400'

  Write-Host "8. Bang tin trong app nhan duoc" -ForegroundColor Cyan
  $after = (Req GET '/insights/unread-count' $null $H).Data.count
  Check "Badge tang ($before -> $after)" ($after -gt $before) "$before -> $after"
  $list = Req GET '/insights?limit=10' $null $H
  # Doc duoc danh sach la phan de vo nhat: cot Severity la TINYINT, record khai int -> Dapper
  # nem loi materialization. Loi nam im tu luc tao bang vi workflow chi INSERT.
  Check 'GET insights = 200' ($list.Code -eq 200) "code=$($list.Code) $($list.Error)"
  $fresh = @($list.Data.items) | Where-Object { $_.kind -eq 'sale-brief' -and ([datetime]$_.createdUtc) -gt $t0 } | Select-Object -First 1
  Check 'Thay ban tin thu vua gui' ($null -ne $fresh) 'khong thay dong sale-brief moi'
  if ($fresh) {
    Check 'createdUtc co hau to Z (UTC)' ("$($list.Data.items[0].createdUtc)" -match 'Z$|\+00:00$') "$($list.Data.items[0].createdUtc)"
    # C5: item ban tin (sale/ceo) phai kem speakText - loi doc TTS da bo markdown/emoji.
    # Ban tin THU chua chuoi in dam '**THU**' -> speakText phai bo cap dau '**'.
    Check 'C5: ban tin co speakText' (-not [string]::IsNullOrWhiteSpace($fresh.speakText)) "speakText='$($fresh.speakText)'"
    Check 'C5: speakText da bo dau ** (markdown)' (-not ("$($fresh.speakText)" -match '\*\*')) "$($fresh.speakText)"
  }

  Write-Host "9. Danh dau da doc" -ForegroundColor Cyan
  if ($fresh) {
    Check 'Danh dau da doc = 200' ((Req POST "/insights/$($fresh.id)/read" $null $H).Code -eq 200) 'khac 200'
    $c2 = (Req GET '/insights/unread-count' $null $H).Data.count
    Check "Badge giam ($after -> $c2)" ($c2 -lt $after) "$after -> $c2"
  }

  Write-Host "10. Zalo config" -ForegroundColor Cyan
  $z = Req GET '/digest/zalo-config' $null $H
  Check 'GET zalo-config = 200' ($z.Code -eq 200) "code=$($z.Code)"
  Check 'KHONG tra access token ve client' (-not (@($z.Data.PSObject.Properties.Name) -contains 'accessToken')) 'co tra token!'
  $z = Req PUT '/digest/zalo-config' @{ oaId=''; accessToken='' } $H
  Check 'Zalo thieu thong tin = 400/403' (($z.Code -eq 400) -or ($z.Code -eq 403)) "code=$($z.Code)"

  Write-Host "11. Telegram detect (tien ich phu - khong duoc thanh 500)" -ForegroundColor Cyan
  $tg = Req POST '/digest/telegram/detect' $null $H
  Check 'Telegram detect tra 200/502/503' (@(200,502,503) -contains $tg.Code) "code=$($tg.Code)"
  if ($tg.Code -eq 200) { Info "ma: $($tg.Data.code) | chatId: $($tg.Data.chatId)" }
  else { Info "chua cau hinh bot hoac khong goi duoc Telegram (dung nhu mong doi)" }
}
finally {
  Write-Host ""
  Write-Host "Khoi phuc dang ky goc..." -ForegroundColor Cyan
  RestoreOriginal
  Info "Xong. Ban goc: $backupPath"
}

Write-Host ""
Write-Host ("KET QUA: $pass PASS / $fail FAIL") -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
if ($fail -gt 0) { exit 1 }

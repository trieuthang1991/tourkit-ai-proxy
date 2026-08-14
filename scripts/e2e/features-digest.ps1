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

# Cum ban tin nam sau co Features:Digest. Tat co thi /api/v1/digest|insights/* KHONG duoc map ->
# moi assertion do 404. Do la dung y do, khong phai loi -> bo qua va thoat 0, de lan chay E2E toi
# khong ai tuong tinh nang hong that. Hoi server that thay vi doc appsettings: web va worker co the
# khac file, ma cai quyet dinh la cai web dang chay.
try {
  $feat = Invoke-RestMethod -Uri "$BaseUrl/api/v1/features" -Method GET -TimeoutSec 10
  if (-not $feat.digest) {
    Write-Host "BO QUA - tinh nang ban tin dang TAT (Features:Digest=false)." -ForegroundColor Yellow
    Write-Host "  Bat lai: sua appsettings.json -> Features:Digest = true, roi restart." -ForegroundColor DarkGray
    exit 0
  }
} catch {
  Write-Host "Khong hoi duoc /api/v1/features ($($_.Exception.Message)) - chay tiep nhu cu." -ForegroundColor DarkYellow
}

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
  # Khong con chan "0 kenh": kenh trong app luon bat (kho luu de xem/nghe lai), nen chi nhan
  # trong app la lua chon hop le. Gui channelInApp=false cung khong tat duoc - server ep bat.
  Check 'Bat ban tin khong kenh ngoai = 200 (trong app luon co)' `
    ((Req PUT '/digest/subscriptions/sale-brief' @{ enabled=$true; sendHourLocal=7; channelInApp=$false } $H).Code -eq 200) 'khac 200'
  $inapp = (Req GET '/digest/subscriptions' $null $H).Data.items | Where-Object { $_.briefType -eq 'sale-brief' }
  Check 'Server ep channelInApp = true' ($inapp.channelInApp -eq $true) "channelInApp=$($inapp.channelInApp)"
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

  Write-Host "6b. C5: moi nguoi chi 1 loai theo vai tro (doi loai = doi tren chinh dong cu)" -ForegroundColor Cyan
  # Khoa chinh (TenantId, Username) -> moi nguoi DUNG 1 dong: luat "1 nguoi 1 loai" la bat bien
  # cua cau truc, khong phai luat phai nho enforce. Doi loai = UPDATE cot BriefType.
  $c5subs = @((Req GET '/digest/subscriptions' $null $H).Data.items)
  Check 'Chi co DUNG 1 dong dang ky' ($c5subs.Count -eq 1) "co $($c5subs.Count) dong"
  Check 'Dong do la ceo-brief (vua luu)' ($c5subs[0].briefType -eq 'ceo-brief') "briefType=$($c5subs[0].briefType)"
  Check 'ceo-brief dang bat' ($c5subs[0].enabled -eq $true) "enabled=$($c5subs[0].enabled)"

  Write-Host "6c. Doi nguoc lai ve sale-brief" -ForegroundColor Cyan
  Check 'Luu lai sale-brief = 200' ((Req PUT '/digest/subscriptions/sale-brief' @{ enabled=$true; sendHourLocal=7; channelInApp=$true } $H).Code -eq 200) 'khac 200'
  $c5subs = @((Req GET '/digest/subscriptions' $null $H).Data.items)
  Check 'Van chi 1 dong sau khi doi nguoc' ($c5subs.Count -eq 1) "co $($c5subs.Count) dong"
  Check 'Da ve sale-brief' ($c5subs[0].briefType -eq 'sale-brief') "briefType=$($c5subs[0].briefType)"

  Write-Host "7. Gui thu (khong dung toi moc 'da gui hom nay')" -ForegroundColor Cyan
  $before = (Req GET '/insights/unread-count' $null $H).Data.count
  $t0 = (Get-Date).ToUniversalTime().AddMinutes(-2)
  $r = Req POST '/digest/subscriptions/sale-brief/test' $null $H
  Check 'Gui thu = 200' ($r.Code -eq 200) "code=$($r.Code)"
  Check 'Gui thu bao ok' ($r.Data.ok -eq $true) "$($r.Data | ConvertTo-Json -Compress)"
  Info "summary: $($r.Data.summary)"
  Check 'Gui thu loai la = 400' ((Req POST '/digest/subscriptions/hacker-brief/test' $null $H).Code -eq 400) 'khac 400'

  Write-Host "7b. Hang doi ban tin doc duoc" -ForegroundColor Cyan
  # Gui thu di THANG (khong qua hang doi) nen items co the rong - kiem endpoint doc duoc la du.
  # Dong hang doi that chi sinh ra khi workflow chuan bi ban tin (co kenh ngoai duoc bat).
  $q = Req GET '/workflows/outbound-mails?kind=daily-brief&limit=5' $null $H
  Check 'GET outbound-mails = 200' ($q.Code -eq 200) "code=$($q.Code) $($q.Error)"
  Check 'Tra ve mang items' ($null -ne $q.Data.items) 'khong co items'
  $qrow = @($q.Data.items) | Select-Object -First 1
  if ($qrow) {
    Check 'Dong hang doi co field channel' ($null -ne $qrow.channel) "channel=$($qrow.channel)"
    Info "dong moi nhat: channel=$($qrow.channel) status=$($qrow.status) scheduledUtc=$($qrow.scheduledUtc)"
  } else { Info "hang doi rong (chua co workflow nao chuan bi ban tin) - binh thuong" }

  Write-Host "7c. Tat tung kenh: kenh da tat phai bi BO QUA, khong duoc bao loi" -ForegroundColor Cyan
  # Nguoi dung khong muon nhan qua zalo/telegram/email -> bo tick. Kenh tat phai ra 'skip',
  # KHONG duoc thanh 'FAIL' (FAIL nghia la co gang gui roi hong -> admin doc nham la he thong loi).
  $null = Req PUT '/digest/subscriptions/sale-brief' @{
    enabled=$true; sendHourLocal=7; channelInApp=$true
    channelEmail=$false; channelTelegram=$false; channelZalo=$false } $H
  $t = Req POST '/digest/subscriptions/sale-brief/test' $null $H
  Check 'Tat het kenh ngoai: van 200' ($t.Code -eq 200) "code=$($t.Code)"
  Check 'Tat het kenh ngoai: van bao ok (trong app luon nhan)' ($t.Data.ok -eq $true) "$($t.Data | ConvertTo-Json -Compress)"
  Check 'sentChannels co inapp' ("$($t.Data.sentChannels)" -match 'inapp') "sentChannels=$($t.Data.sentChannels)"
  Check 'zalo tat -> skip chu khong FAIL' ("$($t.Data.summary)" -match 'zalo:skip') "summary=$($t.Data.summary)"
  Check 'telegram tat -> skip chu khong FAIL' ("$($t.Data.summary)" -match 'telegram:skip') "summary=$($t.Data.summary)"
  Check 'email tat -> skip chu khong FAIL' ("$($t.Data.summary)" -match 'email:skip') "summary=$($t.Data.summary)"

  Write-Host "7d. Bat zalo khi cong ty CHUA cau hinh OA (hoac user id sai)" -ForegroundColor Cyan
  # Ca nay phai hong RIENG kenh zalo, KHONG duoc keo do ca luot gui. Dung user id ro rang la gia
  # nen khong the gui nham cho nguoi that.
  $zcfg = (Req GET '/digest/zalo-config' $null $H).Data
  Info "OA zalo cua cong ty: configured=$($zcfg.configured)"
  $null = Req PUT '/digest/subscriptions/sale-brief' @{
    enabled=$true; sendHourLocal=7; channelInApp=$true
    channelZalo=$true; zaloUserId='e2e-khong-ton-tai' } $H
  $t = Req POST '/digest/subscriptions/sale-brief/test' $null $H
  Check 'Zalo hong: endpoint van 200 (khong 500)' ($t.Code -eq 200) "code=$($t.Code)"
  Check 'Zalo hong: van ok nho kenh trong app' ($t.Data.ok -eq $true) "$($t.Data | ConvertTo-Json -Compress)"
  Check 'Zalo bao FAIL trong summary' ("$($t.Data.summary)" -match 'zalo:FAIL') "summary=$($t.Data.summary)"
  Check 'Zalo hong KHONG lot vao sentChannels' (-not ("$($t.Data.sentChannels)" -match 'zalo')) "sentChannels=$($t.Data.sentChannels)"

  Write-Host "7e. Bat telegram voi chat id sai / bot chua cau hinh" -ForegroundColor Cyan
  # Chua khai Telegram:BotToken -> kenh tu tat (skip). Da khai ma chat id sai -> FAIL.
  # Ca hai deu chap nhan duoc; cai KHONG chap nhan duoc la 500 hoac keo do kenh khac.
  $null = Req PUT '/digest/subscriptions/sale-brief' @{
    enabled=$true; sendHourLocal=7; channelInApp=$true
    channelTelegram=$true; telegramChatId='-100000000000000' } $H
  $t = Req POST '/digest/subscriptions/sale-brief/test' $null $H
  Check 'Telegram sai: endpoint van 200' ($t.Code -eq 200) "code=$($t.Code)"
  Check 'Telegram sai: van ok nho kenh trong app' ($t.Data.ok -eq $true) "$($t.Data | ConvertTo-Json -Compress)"
  Check 'Telegram ra skip hoac FAIL (khong am tham ok)' `
    ("$($t.Data.summary)" -match 'telegram:(skip|FAIL)') "summary=$($t.Data.summary)"
  Check 'Telegram hong KHONG lot vao sentChannels' (-not ("$($t.Data.sentChannels)" -match 'telegram')) "sentChannels=$($t.Data.sentChannels)"

  Write-Host "7f. Gui thu khi CHUA co ban tin nao duoc dung san" -ForegroundColor Cyan
  # 'Gui thu' co y KHONG doi bat ky ban tin nao da duoc chuan bi - no tu dung noi dung thu tai cho.
  # Nho vay nguoi dung kiem tra duoc kenh nhan ngay sau khi luu, khong phai doi den sang hom sau.
  $t = Req POST '/digest/subscriptions/ceo-brief/test' $null $H
  Check 'Gui thu loai CHUA dang ky = 400 (bao ro, khong im lang)' ($t.Code -eq 400) "code=$($t.Code)"
  Info 'Doi lai loai da dang ky roi gui thu -> phai chay duoc ngay, khong can doi workflow'
  $t = Req POST '/digest/subscriptions/sale-brief/test' $null $H
  Check 'Gui thu chay duoc du chua co ban tin dung san' ($t.Code -eq 200 -and $t.Data.ok -eq $true) "code=$($t.Code)"

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

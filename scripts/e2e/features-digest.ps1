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
#   - Neu ceo-brief CHUA duoc cau hinh: script khai luat chung bang dung bo MAC DINH de test tiep,
#     roi tra ve "chua khai" o cuoi (options = {}). Lich chay giu nguyen trang thai ban dau.

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
      zaloPhone       = $s.zaloPhone
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

  Write-Host "5b. Chua khai luat chung -> KHONG cho dang ky (va khong duoc luu len)" -ForegroundColor Cyan
  # Ban tin chay bang mac dinh ma chua ai xem qua = nhac theo nguong doan mo. Chan o day, chan
  # TRUOC khi luu. Bug that da gap: kiem dat SAU lenh luu -> tra 409 ma dong dang ky VAN duoc ghi.
  $ceoCfg0 = (Req GET '/workflows' $null $H).Data.items | Where-Object { $_.type -eq 'ceo-brief' }
  $ceoHadOptions = ($null -ne $ceoCfg0.options) -and (@($ceoCfg0.options.PSObject.Properties).Count -gt 0)
  if (-not $ceoHadOptions) {
    $subsBefore = @((Req GET '/digest/subscriptions' $null $H).Data.items)
    $blocked = Req PUT '/digest/subscriptions/ceo-brief' @{ enabled=$true; sendHourLocal=7; channelInApp=$true } $H
    Check 'Chua cau hinh -> tu choi dang ky (409)' ($blocked.Code -eq 409) "code=$($blocked.Code)"
    # Req tra than loi vao .Error (chuoi tho) chu khong parse .Data khi status != 2xx.
    Check 'Tu choi co co needsCompanySetup' ($blocked.Error -match 'needsCompanySetup') 'thieu co - client khong biet chi dan di dau'
    Check 'Tu choi noi ro phai khai o dau' ($blocked.Error -match 'Theo t') 'than loi khong chi duong'
    $subsAfter = @((Req GET '/digest/subscriptions' $null $H).Data.items)
    $sameType = ($subsBefore.Count -eq 0 -and $subsAfter.Count -eq 0) -or
                ($subsAfter.Count -gt 0 -and $subsBefore.Count -gt 0 -and $subsAfter[0].briefType -eq $subsBefore[0].briefType)
    Check 'Tu choi thi KHONG duoc ghi dong dang ky' $sameType 'dong dang ky da bi doi du server tra loi'
  } else {
    Info 'ceo-brief da co cau hinh san - bo qua phan kiem "chua khai luat chung"'
  }

  Write-Host "6. Ban tin dieu hanh KHONG bi proxy chan quyen" -ForegroundColor Cyan
  # TourKit.Api tu co pham vi so theo tai khoan (DashboardService.ResolveSpUserIdAsync).
  # Proxy tu gac them se chan oan nguoi co quyen bao cao ma khong co CH_XEM_ALL.
  # Khai luat chung truoc (khoi phuc o cuoi + trong finally) roi moi dang ky duoc.
  if (-not $ceoHadOptions) {
    $null = Req PUT '/workflows/ceo-brief' @{
      enabled = [bool]$ceoCfg0.enabled; intervalMinutes = [int]$ceoCfg0.intervalMinutes
      options = @{ comparePeriod='prev-month'; secSellers=$true; sellerCount=3; secNewDeals=$true
                   secAppointments=$true; secAlerts=$true; useAi=$true; showNumbers=$true }
    } $H
    $script:ceoCfgTouched = $true
  }
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
  $r = Req POST '/digest/subscriptions/sale-brief/test' $null $H
  Check 'Gui thu = 200' ($r.Code -eq 200) "code=$($r.Code)"
  Check 'Gui thu tra ve co cau tra loi ro rang' ($null -ne $r.Data.summary) "$($r.Data | ConvertTo-Json -Compress)"
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

  Write-Host "7c. Khong bat kenh ngoai nao -> khong co gi de thu" -ForegroundColor Cyan
  # Gui thu la de thu KENH NGOAI. Kenh trong app luon bat va nguoi dung thay ban tin that o do moi
  # ngay -> khong can thu. Khong bat kenh ngoai nao thi phai noi thang, dung bao "da gui".
  $null = Req PUT '/digest/subscriptions/sale-brief' @{
    enabled=$true; sendHourLocal=7; channelInApp=$true
    channelEmail=$false; channelTelegram=$false; channelZalo=$false } $H
  $t = Req POST '/digest/subscriptions/sale-brief/test' $null $H
  Check 'Van tra 200 (khong phai loi he thong)' ($t.Code -eq 200) "code=$($t.Code)"
  Check 'Bao ok=false vi khong co gi de thu' ($t.Data.ok -eq $false) "$($t.Data | ConvertTo-Json -Compress)"
  Check 'queued = 0' ($t.Data.queued -eq 0) "queued=$($t.Data.queued)"
  Info "summary: $($t.Data.summary)"

  Write-Host "7d. Zalo: so dien thoai sai bi chan NGAY luc luu" -ForegroundColor Cyan
  # Zalo gui bang ZNS (nhan theo SO DIEN THOAI). So sai thi ZNS tu choi luc gui, ma loi do chi
  # admin nhin thay -> phai chan tu luc luu, luc nguoi dung con dang nhin man hinh.
  Check 'Bat zalo ma bo trong so = 400' `
    ((Req PUT '/digest/subscriptions/sale-brief' @{ enabled=$true; sendHourLocal=7; channelInApp=$true
      channelZalo=$true; zaloPhone='' } $H).Code -eq 400) 'khac 400'
  Check 'So co dinh (khong dung Zalo) = 400' `
    ((Req PUT '/digest/subscriptions/sale-brief' @{ enabled=$true; sendHourLocal=7; channelInApp=$true
      channelZalo=$true; zaloPhone='02812345678' } $H).Code -eq 400) 'khac 400'
  Check 'Chuoi khong phai so = 400' `
    ((Req PUT '/digest/subscriptions/sale-brief' @{ enabled=$true; sendHourLocal=7; channelInApp=$true
      channelZalo=$true; zaloPhone='khong-phai-so' } $H).Code -eq 400) 'khac 400'
  Check 'So di dong hop le = 200' `
    ((Req PUT '/digest/subscriptions/sale-brief' @{ enabled=$true; sendHourLocal=7; channelInApp=$true
      channelZalo=$true; zaloPhone='+84 912 345 678' } $H).Code -eq 200) 'khac 200'
  $zsub = (Req GET '/digest/subscriptions' $null $H).Data.items | Where-Object { $_.briefType -eq 'sale-brief' }
  Check 'Server chuan hoa ve dang 0xxxxxxxxx' ($zsub.zaloPhone -eq '0912345678') "zaloPhone=$($zsub.zaloPhone)"

  Write-Host "7d-2. Gui thu di ĐÚNG duong cua ban tin that: xep hang doi" -ForegroundColor Cyan
  # Proxy KHONG con lop gui nao -> gui thu dung chinh bo dung dong cua workflow. Nho vay "thu OK"
  # la bang chung ban tin that gui duoc, chu khong phai chung minh cho mot duong code rieng.
  $null = Req PUT '/digest/subscriptions/sale-brief' @{
    enabled=$true; sendHourLocal=7; channelInApp=$true
    channelZalo=$true; zaloPhone='0912345678' } $H
  $t = Req POST '/digest/subscriptions/sale-brief/test' $null $H
  Check 'Gui thu = 200' ($t.Code -eq 200) "code=$($t.Code)"
  Check 'Bao xep 1 kenh vao hang doi' ($t.Data.queued -eq 1) "queued=$($t.Data.queued)"
  Check 'sentChannels = zalo (KHONG co trong app)' ("$($t.Data.sentChannels)" -eq 'zalo') "sentChannels=$($t.Data.sentChannels)"
  Info "summary: $($t.Data.summary)"
  $q = Req GET '/workflows/outbound-mails?kind=daily-brief&channel=2&limit=5' $null $H
  Check 'Thay dong Zalo trong hang doi' (@($q.Data.items).Count -gt 0) "items=$(@($q.Data.items).Count)"

  Write-Host "7e. Telegram: xep hang doi, khong gui thang tu proxy" -ForegroundColor Cyan
  # Truoc day proxy tu goi api.telegram.org cho nut Gui thu -> phai khai bot token o CA HAI noi.
  # Nay xep hang doi nhu moi kenh khac; proxy chi con can token cho tien ich tu tim chat id.
  $null = Req PUT '/digest/subscriptions/sale-brief' @{
    enabled=$true; sendHourLocal=7; channelInApp=$true
    channelTelegram=$true; telegramChatId='-100000000000000' } $H
  $t = Req POST '/digest/subscriptions/sale-brief/test' $null $H
  Check 'Telegram: 200' ($t.Code -eq 200) "code=$($t.Code)"
  Check 'Telegram: xep 1 dong hang doi' ($t.Data.queued -eq 1) "queued=$($t.Data.queued)"
  $q = Req GET '/workflows/outbound-mails?kind=daily-brief&channel=1&limit=5' $null $H
  Check 'Thay dong Telegram trong hang doi' (@($q.Data.items).Count -gt 0) "items=$(@($q.Data.items).Count)"

  Write-Host "7f. Gui thu khi CHUA co ban tin nao duoc dung san" -ForegroundColor Cyan
  # 'Gui thu' co y KHONG doi bat ky ban tin nao da duoc chuan bi - no tu dung noi dung thu tai cho.
  # Nho vay nguoi dung kiem tra duoc kenh nhan ngay sau khi luu, khong phai doi den sang hom sau.
  $t = Req POST '/digest/subscriptions/ceo-brief/test' $null $H
  Check 'Gui thu loai CHUA dang ky = 400 (bao ro, khong im lang)' ($t.Code -eq 400) "code=$($t.Code)"
  Info 'Doi lai loai da dang ky roi gui thu -> phai chay duoc ngay, khong can doi workflow'
  $t = Req POST '/digest/subscriptions/sale-brief/test' $null $H
  Check 'Gui thu chay duoc du chua co ban tin dung san' ($t.Code -eq 200 -and $t.Data.ok -eq $true) "code=$($t.Code)"

  Write-Host "8. Bang tin doc duoc + dinh dang dung" -ForegroundColor Cyan
  # Gui thu KHONG con ghi vao Bang tin (co y - xem DigestEndpoints), nen muc nay khong tao du lieu
  # moi ma kiem tren dong dang co. Doc duoc danh sach la phan de vo nhat: cot Severity la TINYINT
  # con record khai int -> Dapper nem loi materialization; loi nay nam im tu luc tao bang vi
  # workflow chi INSERT, chi no khi co cho DOC dau tien.
  $list = Req GET '/insights?limit=10' $null $H
  Check 'GET insights = 200' ($list.Code -eq 200) "code=$($list.Code) $($list.Error)"
  $items = @($list.Data.items)
  Info "Bang tin dang co $($items.Count) dong"
  if ($items.Count -gt 0) {
    Check 'createdUtc co hau to Z (UTC)' ("$($items[0].createdUtc)" -match 'Z$|\+00:00$') "$($items[0].createdUtc)"
    $brief = $items | Where-Object { $_.kind -in @('sale-brief','ceo-brief') } | Select-Object -First 1
    if ($brief) {
      # C5: item ban tin phai kem speakText - loi doc TTS da bo markdown/emoji.
      Check 'C5: ban tin co speakText' (-not [string]::IsNullOrWhiteSpace($brief.speakText)) "speakText='$($brief.speakText)'"
      Check 'C5: speakText da bo dau ** (markdown)' (-not ("$($brief.speakText)" -match '\*\*')) "$($brief.speakText)"
    } else { Info 'chua co dong ban tin nao de kiem speakText' }
  } else { Info 'Bang tin rong - bo qua phan dinh dang' }

  Write-Host "9. Danh dau da doc" -ForegroundColor Cyan
  $before = (Req GET '/insights/unread-count' $null $H).Data.count
  $unread = $items | Where-Object { -not $_.isRead } | Select-Object -First 1
  if ($unread) {
    Check 'Danh dau da doc = 200' ((Req POST "/insights/$($unread.id)/read" $null $H).Code -eq 200) 'khac 200'
    $after = (Req GET '/insights/unread-count' $null $H).Data.count
    Check "Badge giam ($before -> $after)" ($after -lt $before) "$before -> $after"
  } else { Info 'khong con dong chua doc - bo qua' }

  Write-Host "10. Telegram detect (tien ich phu - khong duoc thanh 500)" -ForegroundColor Cyan
  $tg = Req POST '/digest/telegram/detect' $null $H
  Check 'Telegram detect tra 200/502/503' (@(200,502,503) -contains $tg.Code) "code=$($tg.Code)"
  if ($tg.Code -eq 200) { Info "ma: $($tg.Data.code) | chatId: $($tg.Data.chatId)" }
  else { Info "chua cau hinh bot hoac khong goi duoc Telegram (dung nhu mong doi)" }
}
finally {
  Write-Host ""
  Write-Host "Khoi phuc dang ky goc..." -ForegroundColor Cyan
  RestoreOriginal
  # Lich chay cua ceo-brief: tra ve dung trang thai ban dau. Bat/tat lich la thay doi CAP CONG TY,
  # de nguyen thi lan chay E2E vo tinh bat ban tin cho ca cong ty.
  if ($script:ceoCfgTouched -and $ceoCfg0) {
    # options = {} dua ceo-brief ve dung trang thai "chua khai luat chung" (server coi {} la chua
    # cau hinh), nen lan chay E2E sau van kiem duoc cai chan o buoc 5b.
    $null = Req PUT '/workflows/ceo-brief' @{
      enabled = [bool]$ceoCfg0.enabled; intervalMinutes = [int]$ceoCfg0.intervalMinutes
      options = @{}
    } $H
    Info 'Da tra ceo-brief ve trang thai ban dau (chua khai luat chung + lich nhu cu).'
  }
  Info "Xong. Ban goc: $backupPath"
}

Write-Host ""
Write-Host ("KET QUA: $pass PASS / $fail FAIL") -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
if ($fail -gt 0) { exit 1 }

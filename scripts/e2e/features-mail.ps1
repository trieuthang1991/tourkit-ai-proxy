# scripts/e2e/features-mail.ps1
# E2E TINH NANG (DOC) - Hop thu AI (/api/v1/mail/*).
#
# Chay:
#   .\scripts\e2e\features-mail.ps1 -SessionId <sid>
#   .\scripts\e2e\features-mail.ps1 -SidFile $env:TEMP\tk_sid.txt
#
# Lay SessionId: dang nhap app roi mo DevTools Console:
#   localStorage.getItem('tourkit_tk_session')
#
# AN TOAN - hop thu THAT, dia chi THAT:
#   - CHI DOC. KHONG dong bo (cham Gmail that), KHONG gui, KHONG soan nhap (ton luot AI).
#   - KHONG xoa/sua cau hinh tai khoan mail (hong la mat ket noi hop thu).
#   - Cac buoc GHI o day deu la ghi SAI CO Y de xac nhan bi TU CHOI (400/401/404).
#
# ASCII-only co y (PowerShell 5.1 loi parser voi .ps1 tieng Viet khong BOM).

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

$headers = @{ 'X-Session-Id' = $SessionId.Trim(); 'Content-Type' = 'application/json' }
$anon    = @{ 'Content-Type' = 'application/json' }
$pass = 0; $fail = 0; $skip = 0
function Ok  ($m) { $script:pass++; Write-Host ("  PASS  " + $m) -ForegroundColor Green }
function Bad ($m) { $script:fail++; Write-Host ("  FAIL  " + $m) -ForegroundColor Red }
function Skip($m) { $script:skip++; Write-Host ("  SKIP  " + $m) -ForegroundColor Yellow }
function Info($m) { Write-Host ("  ..    " + $m) -ForegroundColor DarkGray }

function Call ($method, $path, $body, $hdr) {
  if (-not $hdr) { $hdr = $headers }
  $args = @{ Uri = "$BaseUrl$path"; Headers = $hdr; Method = $method; UseBasicParsing = $true }
  if ($null -ne $body) { $args.Body = ($body | ConvertTo-Json -Depth 12 -Compress) }
  try {
    $r = Invoke-WebRequest @args
    return @{ Code = [int]$r.StatusCode; Json = ($r.Content | ConvertFrom-Json) }
  } catch {
    $code = 0
    if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    return @{ Code = $code; Json = $null }
  }
}

Write-Host ""
Write-Host "=== E2E: Hop thu AI (chi doc) ===" -ForegroundColor Cyan
Write-Host ("    base: {0}" -f $BaseUrl)
Write-Host ""

# -- 1. Chan cua --------------------------------------------------------------
Write-Host "1. Khong co phien -> 401"
foreach ($p in @('/api/v1/mail', '/api/v1/mail/account')) {
  $c = (Call GET $p $null $anon).Code
  if ($c -eq 401) { Ok ("GET " + $p + " khong phien = 401") }
  else { Bad ("GET " + $p + " khong phien = " + $c + " (phai 401)") }
}
$rac = @{ 'X-Session-Id' = 'khong-ton-tai-0000'; 'Content-Type' = 'application/json' }
if ((Call GET '/api/v1/mail' $null $rac).Code -eq 401) { Ok "Phien rac = 401" }
else { Bad "Phien rac PHAI 401" }

# -- 2. Cau hinh tai khoan ----------------------------------------------------
Write-Host "2. Cau hinh tai khoan"
$acc = Call GET '/api/v1/mail/account' $null $null
if ($acc.Code -eq 200) { Ok "GET tai khoan = 200" } else { Bad ("GET tai khoan = " + $acc.Code) }

# Mat khau ung dung TUYET DOI khong duoc tra ve. Day la bi mat cua khach: lo ra la mat
# quyen doc toan bo hop thu Gmail cua ho.
$tho = ($acc.Json | ConvertTo-Json -Depth 6 -Compress)
if ($tho -notmatch '(?i)"(appPassword|password|pass)"\s*:\s*"[^"]{3,}"') {
  Ok "KHONG lo mat khau ung dung ra ngoai"
} else {
  Bad "LO MAT KHAU trong phan hoi /mail/account"
}

$coTaiKhoan = [bool]$acc.Json.address
if ($coTaiKhoan) { Info ("dia chi dang cam: " + $acc.Json.address) }
else { Info "chua cam hop thu nao" }

# -- 3. Danh sach thu ---------------------------------------------------------
Write-Host "3. Danh sach thu"
$ds = Call GET '/api/v1/mail' $null $null
if ($ds.Code -eq 200) { Ok "GET danh sach = 200" } else { Bad ("GET danh sach = " + $ds.Code) }
$so = @($ds.Json.items).Count
Info ("dang co {0} thu trong danh sach" -f $so)

# Ngay gio phai kem 'Z' - day la luat xuyen suot cua du an. Thieu Z thi giao dien
# hieu la gio dia phuong va lech 7 tieng.
if ($so -gt 0) {
  $mau = $ds.Json.items[0]
  $moc = $mau.receivedAt; if (-not $moc) { $moc = $mau.date }; if (-not $moc) { $moc = $mau.receivedUtc }
  if ($moc -and ("$moc" -match 'Z$')) { Ok "Moc thoi gian co hau to Z (UTC)" }
  elseif ($moc) { Bad ("Moc thoi gian THIEU Z: " + $moc) }
  else { Info "khong tim thay truong thoi gian de kiem" }

  # Moc nam o TUONG LAI = du lieu cu con luu gio dia phuong (+7), chua doi ve UTC.
  # CANH BAO chu khong FAIL: ma nguon da dung roi, phan con lai la viec doi DU LIEU CU - can
  # nguoi quyet dinh, xem scripts/sql/fix-mails-receivedat-local-to-utc.sql. De thanh FAIL thi
  # bo kiem nay do do mai cho toi khi ai do chay migration, va guard hay keu oan thi bi tat.
  $tuongLai = 0
  foreach ($x in $ds.Json.items) {
    $t = $x.receivedAt; if (-not $t) { continue }
    try { if ([datetime]::Parse($t).ToUniversalTime() -gt (Get-Date).ToUniversalTime().AddMinutes(5)) { $tuongLai++ } } catch { }
  }
  if ($tuongLai -eq 0) { Ok "Khong thu nao co moc nam o tuong lai" }
  else {
    Info ("CANH BAO: {0}/{1} thu co moc nam o TUONG LAI - du lieu cu con la gio dia phuong." -f $tuongLai, $so)
    Info "         Xem scripts/sql/fix-mails-receivedat-local-to-utc.sql (chua chay, can duyet)."
  }
}

# -- 4. Loc theo nhom ---------------------------------------------------------
# Nhom la phai bi tu choi hoac tra rong - KHONG duoc lang le tra ve TAT CA thu,
# vi nguoi dung se tuong bo loc dang chay.
Write-Host "4. Loc theo nhom"
$la = Call GET '/api/v1/mail?category=khong-co-nhom-nay' $null $null
if ($la.Code -eq 400) { Ok "Nhom la = 400" }
elseif ($la.Code -eq 200 -and @($la.Json.items).Count -eq 0) { Ok "Nhom la = danh sach rong" }
elseif ($la.Code -eq 200 -and @($la.Json.items).Count -eq $so) {
  Bad "Nhom la tra ve TOAN BO thu - bo loc bi bo qua trong im lang"
} else { Ok ("Nhom la = " + $la.Code) }

# -- 5. Thu khong ton tai -----------------------------------------------------
Write-Host "5. Thu khong ton tai"
$ma = Call GET '/api/v1/mail/khong-ton-tai-e2e' $null $null
if ($ma.Code -eq 404) { Ok "Id la = 404" }
else { Bad ("Id la = " + $ma.Code + " (phai 404, khong duoc 200 rong hay 500)") }

# -- 6. Soan thu: kiem dau vao (KHONG gui) ------------------------------------
# Cac ca duoi deu la GUI SAI CO Y - phai bi chan o buoc kiem dau vao, khong bao gio
# di toi buoc gui that.
Write-Host "6. Soan thu - kiem dau vao (khong gui that)"
$thieu = Call POST '/api/v1/mail/compose/send' @{ subject = 'E2E' } $null
if ($thieu.Code -eq 400 -or $thieu.Code -eq 422) { Ok "Gui khong co nguoi nhan = " + $thieu.Code }
elseif ($thieu.Code -eq 404) { Skip "Duong gui khong mo (tinh nang tat?)" }
else { Bad ("Gui khong co nguoi nhan = " + $thieu.Code + " - PHAI bi chan") }

$sai = Call POST '/api/v1/mail/compose/send' @{ to = 'khong-phai-email'; subject = 'E2E'; body = 'x' } $null
if ($sai.Code -eq 400 -or $sai.Code -eq 422) { Ok ("Dia chi sai dinh dang = " + $sai.Code) }
elseif ($sai.Code -eq 404) { Skip "Duong gui khong mo" }
else { Bad ("Dia chi sai dinh dang = " + $sai.Code + " - PHAI bi chan truoc khi gui") }

# -- 7. Doi trang thai thu khong ton tai --------------------------------------
Write-Host "7. Doi trang thai thu khong ton tai"
$dt = Call PATCH '/api/v1/mail/khong-ton-tai-e2e/status' @{ status = 'da_xu_ly' } $null
if ($dt.Code -eq 404 -or $dt.Code -eq 400) { Ok ("Doi trang thai thu la = " + $dt.Code) }
else { Bad ("Doi trang thai thu la = " + $dt.Code + " (phai 404/400)") }

Write-Host ""
Write-Host ("{0} pass | {1} fail | {2} skip" -f $pass, $fail, $skip) -ForegroundColor (& { if ($fail) { 'Red' } else { 'Green' } })
Write-Host ""
exit (& { if ($fail) { 1 } else { 0 } })

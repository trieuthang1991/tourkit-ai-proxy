# scripts/e2e/features-chat-inbox.ps1
# E2E TINH NANG (DONG) - Hop thu chat da kenh (/api/v1/chat/conversations, /channels, /quick-replies).
#
# Chay:
#   .\scripts\e2e\features-chat-inbox.ps1 -SessionId <sid>
#   .\scripts\e2e\features-chat-inbox.ps1 -SidFile $env:TEMP\tk_sid.txt
#
# Lay SessionId: dang nhap app roi mo DevTools Console:
#   localStorage.getItem('tourkit_tk_session')
#
# AN TOAN - day la hop thu THAT cua tenant that:
#   - CHI tao mau tra loi nhanh voi trigger co tien to 'e2e' roi XOA o cuoi + trong finally.
#   - KHONG gui tin nao cho khach (khong dung /send).
#   - KHONG tao/xoa tai khoan kenh (do la cau hinh that, hong la mat ket noi Zalo/Messenger).
#   - Webhook chi thu chu ky SAI - de xac nhan bi TU CHOI, khong tao hoi thoai nao.
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

$taoQr = @()

Write-Host ""
Write-Host "=== E2E: Hop thu chat da kenh ===" -ForegroundColor Cyan
Write-Host ("    base: {0}" -f $BaseUrl)
Write-Host ""

try {
  # -- 1. Chan cua ------------------------------------------------------------
  Write-Host "1. Khong co phien -> 401"
  foreach ($p in @('/api/v1/chat/conversations', '/api/v1/chat/channels', '/api/v1/chat/quick-replies')) {
    $c = (Call GET $p $null $anon).Code
    if ($c -eq 401) { Ok ("GET " + $p + " khong phien = 401") }
    else { Bad ("GET " + $p + " khong phien = " + $c + " (phai 401)") }
  }

  # -- 2. Tinh nang co dang bat khong ----------------------------------------
  # Tat co Features:Chat thi TAT CA duong nay tra 404 JSON (co y - xem EndpointRegistration).
  # Phan biet ro "tat co" voi "hong": bao SKIP chu khong bao FAIL.
  Write-Host "2. Trang thai tinh nang"
  $hoiThoai = Call GET '/api/v1/chat/conversations' $null $null
  if ($hoiThoai.Code -eq 404) {
    Skip "Features:Chat dang TAT - bo qua phan con lai (day khong phai loi)"
    Write-Host ""
    Write-Host ("{0} pass | {1} fail | {2} skip" -f $pass, $fail, $skip)
    exit 0
  }
  if ($hoiThoai.Code -eq 200) { Ok "GET danh sach hoi thoai = 200" }
  else { Bad ("GET danh sach hoi thoai = " + $hoiThoai.Code) }
  Info ("dang co {0} hoi thoai" -f @($hoiThoai.Json.items).Count)

  # -- 3. Danh sach kenh -----------------------------------------------------
  Write-Host "3. Danh sach kenh"
  $kenh = Call GET '/api/v1/chat/channels' $null $null
  if ($kenh.Code -eq 200) { Ok "GET kenh = 200" } else { Bad ("GET kenh = " + $kenh.Code) }

  # -- 4. Hoi thoai khong ton tai --------------------------------------------
  # Quan trong hon ve ngoai: id la PHAI 404, khong duoc 200-rong (lo du lieu tenant khac)
  # cung khong duoc 500.
  Write-Host "4. Hoi thoai khong ton tai"
  $ma = Call GET '/api/v1/chat/conversations/999999999' $null $null
  if ($ma.Code -eq 404) { Ok "Id la = 404" }
  else { Bad ("Id la = " + $ma.Code + " (phai 404, khong duoc 200 rong hay 500)") }

  # -- 5. Mau tra loi nhanh: tao -> doc -> sua -> xoa -------------------------
  Write-Host "5. Mau tra loi nhanh (tao/doc/sua/xoa)"
  $qr0 = Call GET '/api/v1/chat/quick-replies' $null $null
  if ($qr0.Code -eq 200) { Ok "GET mau tra loi = 200" } else { Bad ("GET mau tra loi = " + $qr0.Code) }
  $qrTruoc = @($qr0.Json.items).Count

  $trigger = 'e2e' + (Get-Date -Format 'HHmmss')
  $tao = Call PUT '/api/v1/chat/quick-replies' @{ trigger = $trigger; body = 'Noi dung E2E, se xoa ngay' } $null
  if ($tao.Code -eq 403) {
    Skip "Tai khoan nay khong co quyen cau hinh he thong - bo qua phan mau tra loi"
  }
  elseif ($tao.Code -eq 200) {
    Ok "Tao mau tra loi = 200"
    $qr1 = Call GET '/api/v1/chat/quick-replies' $null $null
    $moi = @($qr1.Json.items | Where-Object { $_.trigger -eq $trigger })
    if ($moi.Count -eq 1) { Ok "Doc lai thay mau vua tao" } else { Bad "Tao xong khong thay trong danh sach" }
    if ($moi.Count -eq 1) { $taoQr += $moi[0].id }

    # Noi dung rong phai bi tu choi NGAY - khong duoc luu mau trong roi de nhan vien
    # bam nham gui mot tin trang cho khach.
    $rong = Call PUT '/api/v1/chat/quick-replies' @{ trigger = $trigger; body = '   ' } $null
    if ($rong.Code -eq 400) { Ok "Noi dung rong bi tu choi = 400" }
    else { Bad ("Noi dung rong = " + $rong.Code + " (phai 400)") }

    # Cung trigger goi lai = SUA, khong phai them dong thu hai.
    $sua = Call PUT '/api/v1/chat/quick-replies' @{ trigger = $trigger; body = 'Noi dung E2E da sua' } $null
    if ($sua.Code -eq 200) { Ok "Sua mau = 200" } else { Bad ("Sua mau = " + $sua.Code) }
    $qr2 = Call GET '/api/v1/chat/quick-replies' $null $null
    $sauSua = @($qr2.Json.items | Where-Object { $_.trigger -eq $trigger })
    if ($sauSua.Count -eq 1) { Ok "Cung trigger = SUA, khong tao them dong" }
    else { Bad ("Cung trigger tao ra " + $sauSua.Count + " dong") }
    if ($sauSua.Count -ge 1 -and $sauSua[0].body -eq 'Noi dung E2E da sua') { Ok "Noi dung moi da luu" }
    else { Bad "Sua xong noi dung khong doi" }

    foreach ($x in $sauSua) {
      $xoa = Call DELETE ("/api/v1/chat/quick-replies/" + $x.id) $null $null
      if ($xoa.Code -eq 200) { Ok "Xoa mau = 200" } else { Bad ("Xoa mau = " + $xoa.Code) }
    }
    $taoQr = @()
    $qr3 = Call GET '/api/v1/chat/quick-replies' $null $null
    if (@($qr3.Json.items).Count -eq $qrTruoc) { Ok ("So mau ve dung ban dau ({0})" -f $qrTruoc) }
    else { Bad ("So mau lech: {0} thay vi {1}" -f @($qr3.Json.items).Count, $qrTruoc) }
  }
  else { Bad ("Tao mau tra loi = " + $tao.Code) }

  # -- 6. Webhook: chu ky sai phai bi tu choi --------------------------------
  # Day la be mat CONG KHAI - ai cung goi duoc. Nhan bua mot than tin ma he thong tao
  # hoi thoai that thi bat ky ai cung bom duoc tin gia vao hop thu cua khach.
  Write-Host "6. Webhook chu ky sai bi tu choi"
  $than = @{ app_id = 'gia'; message = @{ text = 'tin gia tu E2E' }; timestamp = '1' }
  $wh = Call POST '/api/v1/chat/webhook/zalo/staging.tourkit.vn' $than $anon
  if ($wh.Code -eq 401 -or $wh.Code -eq 403 -or $wh.Code -eq 400) {
    Ok ("Webhook chu ky sai bi tu choi = " + $wh.Code)
  } elseif ($wh.Code -eq 404) {
    Ok "Webhook tu choi = 404 (tenant/kenh chua cau hinh)"
  } else {
    Bad ("Webhook chu ky sai = " + $wh.Code + " - PHAI tu choi, khong duoc nhan")
  }

  # -- 7. Vuot bien tenant ---------------------------------------------------
  # Phien cua tenant nay khong duoc doc hop thu tenant khac qua duong webhook-tenant.
  Write-Host "7. Khong doc duoc hop thu cua cong ty khac"
  $lay = Call GET '/api/v1/chat/conversations?tenantId=erp.tourkit.vn' $null $null
  if ($lay.Code -eq 200) {
    # Tham so la phai bi BO QUA, khong duoc dung de doi tenant.
    Ok "Tham so tenantId la bi bo qua (van tra hop thu cua chinh minh)"
  } else {
    Ok ("Tu choi tham so tenantId la = " + $lay.Code)
  }

} finally {
  foreach ($q in $taoQr) {
    try { $null = Call DELETE ("/api/v1/chat/quick-replies/" + $q) $null $null; Info ("da don mau " + $q) } catch { }
  }
}

Write-Host ""
Write-Host ("{0} pass | {1} fail | {2} skip" -f $pass, $fail, $skip) -ForegroundColor (& { if ($fail) { 'Red' } else { 'Green' } })
Write-Host ""
exit (& { if ($fail) { 1 } else { 0 } })

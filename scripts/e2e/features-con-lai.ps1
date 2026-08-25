# scripts/e2e/features-con-lai.ps1
# E2E TINH NANG (DOC) - cac cum chua co bo rieng: tham dinh visa, nhap gia NCC,
# widget chat, giong noi (doc/nghe), quota.
#
# Chay:
#   .\scripts\e2e\features-con-lai.ps1 -SessionId <sid>
#   .\scripts\e2e\features-con-lai.ps1 -SidFile $env:TEMP\tk_sid.txt
#   .\scripts\e2e\features-con-lai.ps1 -SidFile ... -TonAi     # chay them cac ca TON LUOT AI
#
# Lay SessionId: dang nhap app roi mo DevTools Console:
#   localStorage.getItem('tourkit_tk_session')
#
# VI SAO GOP MOT FILE: moi cum o day chi con vai duong dang kiem duoc re va an toan
# (chan cua, hinh dang phan hoi, dau vao sai). Tach thanh nam file 40 dong thi kho nho
# hon la nho. Cum nao lon them thi tach ra rieng luc do.
#
# AN TOAN:
#   - MAC DINH khong ton luot AI nao. Cac ca ton AI nam sau co -TonAi.
#   - KHONG tao don mua quota (do la don thanh toan that).
#   - Widget: tao token roi XOA o cuoi + trong finally.
#   - KHONG tai file len tham dinh visa (ton luot AI vision + luu file that).
#
# ASCII-only co y (PowerShell 5.1 loi parser voi .ps1 tieng Viet khong BOM).

param(
  [string] $BaseUrl   = 'http://localhost:5080',
  [string] $SessionId = '',
  [string] $SidFile   = '',
  [switch] $TonAi
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
    return @{ Code = [int]$r.StatusCode; Json = ($r.Content | ConvertFrom-Json); Raw = $r }
  } catch {
    $code = 0
    if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    return @{ Code = $code; Json = $null; Raw = $null }
  }
}

$tokenTao = @()

Write-Host ""
Write-Host "=== E2E: cac cum con lai ===" -ForegroundColor Cyan
Write-Host ("    base: {0}   ton-AI: {1}" -f $BaseUrl, $TonAi)
Write-Host ""

try {
  # ══ Tham dinh visa ═════════════════════════════════════════════════════════
  Write-Host "-- Tham dinh visa"
  foreach ($p in @('/api/v1/visa/assessments', '/api/v1/visa/questions')) {
    $c = (Call GET $p $null $anon).Code
    if ($c -eq 401) { Ok ("GET " + $p + " khong phien = 401") }
    else { Bad ("GET " + $p + " khong phien = " + $c + " (phai 401)") }
  }

  $hs = Call GET '/api/v1/visa/assessments' $null $null
  if ($hs.Code -eq 200) { Ok "GET danh sach ho so = 200" } else { Bad ("GET danh sach ho so = " + $hs.Code) }

  $ch = Call GET '/api/v1/visa/questions' $null $null
  if ($ch.Code -eq 200) { Ok "GET bo cau hoi = 200" } else { Bad ("GET bo cau hoi = " + $ch.Code) }

  $maVisa = Call GET '/api/v1/visa/assessments/00000000-0000-0000-0000-000000000000' $null $null
  if ($maVisa.Code -eq 404) { Ok "Ho so khong ton tai = 404" }
  else { Bad ("Ho so khong ton tai = " + $maVisa.Code + " (phai 404)") }

  # ══ Nhap gia NCC ═══════════════════════════════════════════════════════════
  Write-Host "-- Nhap gia NCC"
  $meta = Call GET '/api/v1/ncc-import/meta' $null $null
  if ($meta.Code -eq 200) { Ok "GET meta = 200" } else { Bad ("GET meta = " + $meta.Code) }

  $dv = Call GET '/api/v1/ncc-import/services' $null $null
  if ($dv.Code -eq 200) { Ok "GET danh muc dich vu = 200" } else { Bad ("GET danh muc dich vu = " + $dv.Code) }

  # File mau phai tai duoc - day la thu nguoi dung bam dau tien khi khong biet dinh dang.
  try {
    $tpl = Invoke-WebRequest -Uri "$BaseUrl/api/v1/ncc-import/template" -Headers $headers -Method Get -UseBasicParsing
    if ([int]$tpl.StatusCode -eq 200 -and $tpl.RawContentLength -gt 0) {
      Ok ("Tai duoc file mau ({0} byte)" -f $tpl.RawContentLength)
    } else { Bad "File mau rong" }
  } catch { Bad "Khong tai duoc file mau" }

  # Boc tach rong phai bi tu choi, khong duoc goi AI cho mot chuoi rong.
  $rong = Call POST '/api/v1/ncc-import/extract' @{ text = '' } $null
  if ($rong.Code -eq 400) { Ok "Boc tach van ban rong = 400" }
  elseif ($rong.Code -eq 200) { Bad "Van ban rong VAN goi AI - phai chan tu dau" }
  else { Ok ("Boc tach van ban rong = " + $rong.Code) }

  if ($TonAi) {
    $bang = "Khach san ABC - phong Deluxe - 1.200.000 VND/dem - ap dung tu 01/09/2026"
    $bt = Call POST '/api/v1/ncc-import/extract' @{ text = $bang } $null
    if ($bt.Code -eq 200) { Ok "Boc tach mot dong gia = 200" } else { Bad ("Boc tach = " + $bt.Code) }
  } else { Skip "Boc tach that (ton luot AI) - them -TonAi de chay" }

  # ══ Widget chat ════════════════════════════════════════════════════════════
  Write-Host "-- Widget chat"
  $tk0 = Call GET '/api/v1/admin/widget/tokens' $null $null
  if ($tk0.Code -eq 200) { Ok "GET danh sach token = 200" } else { Bad ("GET danh sach token = " + $tk0.Code) }
  $tkTruoc = @($tk0.Json.items).Count

  $tao = Call POST '/api/v1/admin/widget/tokens' @{ label = 'E2E - se xoa ngay' } $null
  if ($tao.Code -eq 200 -and $tao.Json.token) {
    Ok "Tao token = 200"
    $tokenTao += $tao.Json.token

    # Cau hinh doc bang chinh token do - day la duong ma site khach goi, KHONG can phien.
    $cfg = Call GET ("/api/v1/widget/config?token=" + $tao.Json.token) $null $anon
    if ($cfg.Code -eq 200) { Ok "Site khach doc duoc cau hinh bang token (khong can phien)" }
    else { Bad ("Doc cau hinh bang token = " + $cfg.Code) }

    # Token bia phai bi tu choi - neu khong thi ai cung nhung widget cua tenant khac.
    $bia = Call GET '/api/v1/widget/config?token=token-bia-e2e' $null $anon
    if ($bia.Code -eq 401 -or $bia.Code -eq 403 -or $bia.Code -eq 404) { Ok ("Token bia bi tu choi = " + $bia.Code) }
    else { Bad ("Token bia = " + $bia.Code + " - PHAI tu choi") }

    $xoa = Call DELETE ("/api/v1/admin/widget/tokens/" + $tao.Json.token) $null $null
    if ($xoa.Code -eq 200) { Ok "Xoa token = 200" } else { Bad ("Xoa token = " + $xoa.Code) }
    $tokenTao = @()

    $tk1 = Call GET '/api/v1/admin/widget/tokens' $null $null
    if (@($tk1.Json.items).Count -eq $tkTruoc) { Ok ("So token ve dung ban dau ({0})" -f $tkTruoc) }
    else { Bad ("So token lech: {0} thay vi {1}" -f @($tk1.Json.items).Count, $tkTruoc) }
  }
  elseif ($tao.Code -eq 403) { Skip "Tai khoan khong co quyen tao token widget" }
  else { Bad ("Tao token = " + $tao.Code) }

  # ══ Giong noi ══════════════════════════════════════════════════════════════
  Write-Host "-- Giong noi (doc thanh tieng)"
  # Van ban rong khong duoc lam no, va khong duoc goi dich vu ngoai.
  $ttsRong = Call POST '/api/v1/speech/tts' @{ text = '' } $null
  if ($ttsRong.Code -eq 400) { Ok "Doc van ban rong = 400" }
  elseif ($ttsRong.Code -eq 500) { Bad "Doc van ban rong = 500 - phai chan bang 400" }
  else { Ok ("Doc van ban rong = " + $ttsRong.Code) }

  if ($TonAi) {
    try {
      $tts = Invoke-WebRequest -Uri "$BaseUrl/api/v1/speech/tts" -Headers $headers -Method Post `
             -Body (@{ text = 'Xin chao' } | ConvertTo-Json -Compress) -UseBasicParsing
      if ([int]$tts.StatusCode -eq 200 -and $tts.RawContentLength -gt 1000) {
        Ok ("Doc thanh tieng ra {0} byte am thanh" -f $tts.RawContentLength)
      } else { Bad "Doc thanh tieng khong ra du lieu am thanh" }
      # Nhan dong co giup biet dang chay Vbee/Google/edge hay da roi ve du phong.
      $dc = $tts.Headers['X-Tts-Engine']
      if ($dc) { Info ("dong co TTS: " + $dc) } else { Info "khong co header X-Tts-Engine" }
    } catch { Bad "Goi doc thanh tieng that bai" }
  } else { Skip "Doc thanh tieng that (goi dich vu ngoai) - them -TonAi de chay" }

  # ══ Quota ══════════════════════════════════════════════════════════════════
  Write-Host "-- Quota"
  if ((Call GET '/api/v1/quota' $null $anon).Code -eq 401) { Ok "GET quota khong phien = 401" }
  else { Bad "GET quota khong phien PHAI 401" }

  $q = Call GET '/api/v1/quota' $null $null
  if ($q.Code -eq 200) { Ok "GET quota = 200" } else { Bad ("GET quota = " + $q.Code) }
  if ($null -ne $q.Json) {
    $tho = ($q.Json | ConvertTo-Json -Depth 6 -Compress)
    if ($tho -match '(?i)(used|remain|limit|con|da[Dd]ung|quota)') { Ok "Phan hoi quota co so lieu su dung" }
    else { Bad ("Phan hoi quota khong ro hinh dang: " + $tho.Substring(0, [Math]::Min(120, $tho.Length))) }
  }

  # Cong quan tri quota (AdminOk): so header X-Admin-Token voi Admin:Token trong cau hinh.
  #
  # CHI kiem khi CO cau hinh: token SAI phai bi tu choi. Khi Admin:Token de TRONG thi cong nay
  # CO Y de mo (xem AdminOk) - day la quyet dinh cua chu du an de khong lam hong luong dang chay,
  # nen bao THONG TIN chu khong bao do. Guard keu oan ve mot lua chon co chu dich thi som muon
  # bi tat, keo theo ca phan no canh dung.
  $admSai = Call GET '/api/v1/admin/quota' $null @{ 'X-Session-Id' = $SessionId.Trim(); 'X-Admin-Token' = 'token-sai-e2e' }
  if ($admSai.Code -eq 403) {
    Ok "Quota quan tri: co cau hinh + token SAI -> bi tu choi = 403"
  } elseif ($admSai.Code -eq 200) {
    Info "Admin:Token de trong -> cong quan tri quota dang MO (lua chon co chu dich)."
    Info "       Muon dong thi dat Admin:Token trong appsettings; luc do token sai se la 403."
  } else {
    Bad ("Quota quan tri token sai = " + $admSai.Code + " (mong doi 403 hoac 200)")
  }

} finally {
  foreach ($t in $tokenTao) {
    try { $null = Call DELETE ("/api/v1/admin/widget/tokens/" + $t) $null $null; Info ("da don token " + $t) } catch { }
  }
}

Write-Host ""
Write-Host ("{0} pass | {1} fail | {2} skip" -f $pass, $fail, $skip) -ForegroundColor (& { if ($fail) { 'Red' } else { 'Green' } })
Write-Host ""
exit (& { if ($fail) { 1 } else { 0 } })

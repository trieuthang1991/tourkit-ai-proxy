# scripts/e2e/features-tour-quote.ps1
# E2E TINH NANG (DONG) - Bao gia tour luu tren may chu (/api/v1/tour-quotes).
#
# Chay:
#   .\scripts\e2e\features-tour-quote.ps1 -SessionId <sid>
#   .\scripts\e2e\features-tour-quote.ps1 -SidFile $env:TEMP\tk_sid.txt
#
# Lay SessionId: dang nhap app roi mo DevTools Console:
#   localStorage.getItem('tourkit_tk_session')
#
# AN TOAN:
#   - CHI tao ban ghi MOI (tieu de co tien to E2E-) roi XOA o cuoi + trong finally.
#   - KHONG dung toi ban ghi co san cua tenant.
#   - KHONG ton luot AI nao: day la CRUD thuan.
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
$pass = 0; $fail = 0
function Ok  ($m) { $script:pass++; Write-Host ("  PASS  " + $m) -ForegroundColor Green }
function Bad ($m) { $script:fail++; Write-Host ("  FAIL  " + $m) -ForegroundColor Red }
function Info($m) { Write-Host ("  ..    " + $m) -ForegroundColor DarkGray }

# Goi API va tra ve ca ma trang thai - de kiem duoc ca duong LOI, khong chi duong sang.
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

$created = @()

Write-Host ""
Write-Host "=== E2E: Bao gia tour ===" -ForegroundColor Cyan
Write-Host ("    base: {0}" -f $BaseUrl)
Write-Host ""

try {
  # -- 1. Chan cua ------------------------------------------------------------
  Write-Host "1. Khong co phien -> 401"
  $anon = @{ 'Content-Type' = 'application/json' }
  if ((Call GET '/api/v1/tour-quotes' $null $anon).Code -eq 401) { Ok "GET danh sach khong phien = 401" }
  else { Bad "GET danh sach khong phien PHAI 401" }

  $rac = @{ 'X-Session-Id' = 'khong-ton-tai-0000'; 'Content-Type' = 'application/json' }
  if ((Call GET '/api/v1/tour-quotes' $null $rac).Code -eq 401) { Ok "Phien rac = 401" }
  else { Bad "Phien rac PHAI 401" }

  # -- 2. Doc danh sach -------------------------------------------------------
  Write-Host "2. Doc danh sach"
  $ds = Call GET '/api/v1/tour-quotes' $null $null
  if ($ds.Code -eq 200) { Ok "GET danh sach = 200" } else { Bad ("GET danh sach = " + $ds.Code) }
  $truoc = @($ds.Json.items).Count
  Info ("dang co {0} bao gia" -f $truoc)

  # -- 3. Luu nhap ------------------------------------------------------------
  Write-Host "3. Luu nhap (draft)"
  $tieuDe = "E2E-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
  $req = @{
    title         = $tieuDe
    customerName  = 'Khach E2E'
    customerPhone = '0900000000'
    tourType      = 'GIT'
    adultCount    = 2
    childCount    = 1
    totalNet      = 12000000
  }
  $tao = Call POST '/api/v1/tour-quotes/draft' $req $null
  if ($tao.Code -eq 200 -and $tao.Json.id) {
    Ok "Luu nhap = 200 va tra ve id"
    $created += $tao.Json.id
  } else {
    # Dung han o day. Chay tiep voi id rong chi de ra mot loat FAIL vo nghia: duong
    # /api/v1/tour-quotes/ (dau gach cheo cuoi, id rong) roi vao route DANH SACH nen tra 200,
    # doc ra thi thay tieu de rong -> bao "tieu de lech" thay vi bao dung cai da hong.
    Bad ("Luu nhap that bai (code " + $tao.Code + ") - dung, cac buoc sau deu phu thuoc buoc nay")
    Write-Host ""
    Write-Host ("{0} pass | {1} fail" -f $pass, $fail) -ForegroundColor Red
    exit 1
  }

  if ($tao.Json.isDraft -eq $true) { Ok "Danh dau la ban nhap" } else { Bad "Phai danh dau isDraft=true" }

  # -- 4. Doc lai dung ban vua luu -------------------------------------------
  # Luu y hinh dang: noi dung nam trong `item`, khong phai o goc.
  Write-Host "4. Doc lai ban nhap"
  $id = $tao.Json.id
  $doc = Call GET "/api/v1/tour-quotes/$id" $null $null
  if ($doc.Code -eq 200) { Ok "GET theo id = 200" } else { Bad ("GET theo id = " + $doc.Code) }
  if ($doc.Json.item.title -eq $tieuDe) { Ok "Tieu de dung ban vua luu" }
  else { Bad ("Tieu de lech: '" + $doc.Json.item.title + "' thay vi '" + $tieuDe + "'") }
  if ([int]$doc.Json.item.adultCount -eq 2 -and [int]$doc.Json.item.childCount -eq 1) { Ok "So khach luu dung" }
  else { Bad "So khach khong dung nhu luc luu" }
  if ($doc.Json.draftSavedAt) { Ok "Co moc thoi gian luu nhap" } else { Bad "Thieu draftSavedAt" }

  # Khong gui field `data` van phai luu duoc. KHONG phai chi tiet vun vat: truoc 25/08/2026
  # duong nhap nem 500 ngay cho nay (JsonElement rong dem di serialize), nen client nao khong
  # gui `data` la "khong luu duoc bao gia" ma khong hieu vi sao. Duong SQL da vá tu truoc,
  # duong nhap (Redis, them sau) thi chua - E2E nay bat lai duoc.
  if ($null -ne $doc.Json.item.data) { Ok "Khong gui 'data' van luu duoc (khong con 500)" }
  else { Bad "Thieu 'data' trong ban doc lai" }

  # -- 4b. Nhap CHUA phai bao gia da chot ------------------------------------
  # Co y: nhap nam o Redis, chi vao danh sach sau khi bam chot. Kiem tuong minh de khoi co
  # ai "sua" cho nay thanh luu thang.
  Write-Host "4b. Ban nhap chua duoc tinh la bao gia da chot"
  $dsNhap = Call GET '/api/v1/tour-quotes' $null $null
  if (@($dsNhap.Json.items | Where-Object { $_.id -eq $id }).Count -eq 0) {
    Ok "Ban nhap KHONG nam trong danh sach (dung thiet ke)"
  } else { Bad "Ban nhap da lot vao danh sach khi chua chot" }

  # -- 4c. Chot ---------------------------------------------------------------
  Write-Host "4c. Chot ban nhap"
  $chot = Call POST "/api/v1/tour-quotes/$id/commit" $null $null
  if ($chot.Code -eq 200) { Ok "Chot = 200" } else { Bad ("Chot = " + $chot.Code) }

  $ds2 = Call GET '/api/v1/tour-quotes' $null $null
  if (@($ds2.Json.items | Where-Object { $_.id -eq $id }).Count -eq 1) { Ok "Chot xong moi xuat hien trong danh sach" }
  else { Bad "Chot xong van KHONG thay trong danh sach" }

  # -- 5. Ban cong khai ------------------------------------------------------
  # Duong /public la duong khach hang mo - phai xem duoc, va KHONG duoc doi hoi phien.
  Write-Host "5. Ban cong khai (khach mo bang duong dan)"
  $pub = Call GET "/api/v1/tour-quotes/$id/public" $null $anon
  if ($pub.Code -eq 200) { Ok "Khach xem duoc ban cong khai (khong can dang nhap)" }
  else { Bad ("Ban cong khai = " + $pub.Code + " - khach se khong mo duoc") }

  # -- 6. Id khong ton tai ---------------------------------------------------
  Write-Host "6. Id khong ton tai"
  $ma = Call GET '/api/v1/tour-quotes/khong-ton-tai-e2e' $null $null
  if ($ma.Code -eq 404) { Ok "Id la = 404" }
  else { Bad ("Id la = " + $ma.Code + " (phai 404, khong duoc 200 rong hay 500)") }

  # -- 7. Xoa ----------------------------------------------------------------
  Write-Host "7. Xoa"
  $xoa = Call DELETE "/api/v1/tour-quotes/$id" $null $null
  if ($xoa.Code -eq 200) { Ok "Xoa = 200" } else { Bad ("Xoa = " + $xoa.Code) }
  if ((Call GET "/api/v1/tour-quotes/$id" $null $null).Code -eq 404) {
    Ok "Xoa xong doc lai = 404"
    $created = @($created | Where-Object { $_ -ne $id })
  } else {
    Bad "Xoa xong van doc duoc - chua xoa that"
  }

  $ds3 = Call GET '/api/v1/tour-quotes' $null $null
  if (@($ds3.Json.items).Count -eq $truoc) { Ok ("Danh sach ve dung so ban dau ({0})" -f $truoc) }
  else { Bad ("Danh sach lech: {0} thay vi {1}" -f @($ds3.Json.items).Count, $truoc) }

} finally {
  # Don rac ke ca khi script chet giua chung - khong de lai ban ghi E2E tren tenant that.
  foreach ($c in $created) {
    try { $null = Call DELETE "/api/v1/tour-quotes/$c" $null $null; Info ("da don ban ghi " + $c) } catch { }
  }
}

Write-Host ""
Write-Host ("{0} pass | {1} fail" -f $pass, $fail) -ForegroundColor (& { if ($fail) { 'Red' } else { 'Green' } })
Write-Host ""
exit (& { if ($fail) { 1 } else { 0 } })

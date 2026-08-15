# check-model-drift.ps1 — Model nào ĐANG chạy thật, so với appsettings đang khai.
#
# VÌ SAO CẦN: appsettings.json của web và của worker là 2 file trên 2 máy, đều gitignore. Bản trên
# server có thể là bản cũ mà KHÔNG chỗ nào lộ ra — AiModelRegistry thiếu khoá thì rơi ngầm về
# Models:Primary, không log, không cảnh báo. Nên cách duy nhất biết chắc là đọc NGƯỢC từ log dùng
# thật (dbo.AiUsageHistory) rồi đối chiếu với cấu hình.
#
# READ-ONLY: chỉ SELECT, không ghi gì.
#
# ⚠️ GIỚI HẠN: cột "CauHinh" đọc từ appsettings.json TRÊN MÁY NÀY. Log thì do mọi máy cùng ghi vào
#    một DB. Nên một dòng LỆCH nghĩa là "khác cấu hình ở đây", chưa nói được là máy nào lệch — với
#    tính năng nền thì gần như chắc là worker, với tính năng bấm tay thì có thể là web prod đang
#    cầm appsettings khác bản local. Đối chiếu file trên đúng máy chạy tính năng đó rồi mới kết luận.
#
#   .\scripts\check-model-drift.ps1              # 7 ngày gần nhất
#   .\scripts\check-model-drift.ps1 -Days 30
#
# ĐỌC KẾT QUẢ:
#   OK      — model chạy thật khớp khoá cấu hình.
#   LỆCH    — chạy thật KHÁC cấu hình. Feature nền (mail-auto-sync / *-auto-review / digest) lệch
#             gần như chắc chắn là worker cầm appsettings cũ → sửa trên máy worker rồi restart.
#   ?       — model chạy thật TRÙNG Models:Primary, nên không phân biệt được "khai đúng" với "thiếu
#             khoá nên rơi ngầm về Primary". KHÔNG phải bằng chứng là đúng.
#   (bỏ qua) — tag log không map sang khoá cấu hình nào (completions/other/unknown: client tự chọn model).
param([int]$Days = 7)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# ── Tag log (AiFeatures) → khoá cấu hình (enum AiFeature). QUAN HỆ NHIỀU-1, phải viết tay: một tag
#    có thể do nhiều khoá phục vụ (mail = classify + draft + compose). So sánh máy móc 1-1 sẽ báo
#    động giả. Khớp BẤT KỲ khoá nào trong danh sách = OK.
$Map = @{
  'chat'                 = @('ChatAnalytics')
  'deals'                = @('DealScoring')
  'reviews'              = @('CustomerReview')
  'mail'                 = @('MailClassify', 'MailDraft', 'MailCompose')
  'visa'                 = @('VisaScoring', 'VisaExtraction')
  'tour-builder'         = @('TourBuilder')
  'ncc-import'           = @('NccImport')
  'widget'               = @('Widget')
  'widget-crm'           = @('Widget')
  'widget-crm-plan'      = @('Widget')
  'mail-auto-sync'       = @('MailClassify', 'MailDraft')
  'deal-auto-review'     = @('DealScoring')
  'customer-auto-review' = @('CustomerReview')
  'digest'               = @('Digest')
  'assistant-action'     = @('CustomerReview', 'DealScoring')
  'status-semantics'     = @('StatusSemantics')
}

# ── Cấu hình web ────────────────────────────────────────────────────────────────
$cfgPath = Join-Path $root 'appsettings.json'
if (-not (Test-Path $cfgPath)) { throw "Không thấy $cfgPath — copy từ appsettings.example.json trước." }
$cfg = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json

function Get-ModelKey($section) {
  $s = $cfg.Models.$section
  if ($null -eq $s) { return $null }
  $p = $s.Provider; $m = $s.Model
  if ([string]::IsNullOrWhiteSpace($p)) { $p = $cfg.Models.Primary.Provider }
  if ([string]::IsNullOrWhiteSpace($m)) { $m = $cfg.Models.Primary.Model }
  return "$p/$m"
}
$primary = "$($cfg.Models.Primary.Provider)/$($cfg.Models.Primary.Model)"

# ── Kết nối (conn string có thể ENC: Crypton — giải trong bộ nhớ, KHÔNG in ra) ──
$cs = $cfg.ConnectionStrings.PushDb
if ($cs.StartsWith('ENC:')) {
  $salt = [Text.Encoding]::ASCII.GetBytes('s@1tValue')
  $iv   = [Text.Encoding]::ASCII.GetBytes('@1B2c3D4e5F6g7H8')
  $pdb  = New-Object System.Security.Cryptography.PasswordDeriveBytes('Pas5pr@se', $salt, 'SHA1', 2)
  $aes  = [System.Security.Cryptography.Aes]::Create()
  $aes.Mode = 'CBC'; $aes.Key = $pdb.GetBytes(32); $aes.IV = $iv
  $b = [Convert]::FromBase64String($cs.Substring(4))
  $cs = [Text.Encoding]::UTF8.GetString($aes.CreateDecryptor().TransformFinalBlock($b, 0, $b.Length))
}
# "Command Timeout" là từ khoá của EF/Dapper, SqlConnection cũ không hiểu → bỏ.
$cs = ($cs -split ';' | Where-Object { $_ -notmatch '^\s*Command Timeout\s*=' -and $_ -ne '' }) -join ';'
if ($cs -notmatch 'TrustServerCertificate') { $cs += ';TrustServerCertificate=True' }

$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()
try {
  $cmd = $conn.CreateCommand()
  $cmd.CommandText = @"
SELECT Feature, Provider, Model, COUNT(*) AS Calls, MAX(Ts) AS LastTs
FROM dbo.AiUsageHistory
WHERE Ts >= DATEADD(day, -@d, SYSUTCDATETIME()) AND Status = 'ok'
GROUP BY Feature, Provider, Model
ORDER BY Feature, Calls DESC
"@
  [void]$cmd.Parameters.AddWithValue('@d', $Days)
  $da = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
  $dt = New-Object System.Data.DataTable
  [void]$da.Fill($dt)
} finally { $conn.Close() }

Write-Host ""
Write-Host "Model dung THAT trong $Days ngay qua  (Models:Primary = $primary)" -ForegroundColor Cyan
Write-Host ("-" * 96)

# CHỈ chấm LẦN CHẠY GẦN NHẤT của mỗi tính năng, không chấm mọi tổ hợp trong cửa sổ. Bản đầu chấm
# hết thì ra 15 dòng "LỆCH" mà phần lớn là rác: lần chạy TRƯỚC khi đổi cấu hình, và các lần A/B
# truyền thẳng provider/model theo request. Báo động giả nhiều tới mức không tin được dòng nào —
# tệ hơn là không có script. Câu hỏi thật luôn là "BÂY GIỜ nó đang chạy bằng gì".
$rows = @()
foreach ($g in ($dt.Rows | Group-Object Feature)) {
  $tag     = [string]$g.Name
  $latest  = $g.Group | Sort-Object LastTs -Descending | Select-Object -First 1
  $actual  = "$($latest.Provider)/$($latest.Model)"
  $others  = @($g.Group).Count - 1
  $ghiChu  = ''
  if ($others -gt 0) { $ghiChu = "+$others model khac truoc do" }

  if (-not $Map.ContainsKey($tag)) {
    $rows += [pscustomobject]@{ TinhNang = $tag; ChayThat = $actual; CauHinh = '-'; KetLuan = '(bo qua)'; LanCuoi = $latest.LastTs; GhiChu = $ghiChu }
    continue
  }
  $keys = $Map[$tag]
  $thieu = @($keys | Where-Object { $null -eq $cfg.Models.$_ })
  $expected = @($keys | ForEach-Object { Get-ModelKey $_ } | Where-Object { $_ } | Select-Object -Unique)

  if ($thieu.Count -eq $keys.Count) {
    # Không khai khoá nào → chắc chắn đang rơi ngầm về Primary.
    $rows += [pscustomobject]@{
      TinhNang = $tag; ChayThat = $actual; CauHinh = "(thieu Models:$($keys -join '/'))"
      KetLuan = 'THIEU KHOA'; LanCuoi = $latest.LastTs; GhiChu = $ghiChu
    }
    continue
  }
  $verdict = 'LECH'
  if ($expected -contains $actual) { $verdict = 'OK' }
  # Trùng Primary → không phân biệt được khai đúng với rơi ngầm. Nói thẳng là chưa kết luận được.
  if ($actual -eq $primary -and $verdict -eq 'OK') { $verdict = '?' }
  $rows += [pscustomobject]@{
    TinhNang = $tag; ChayThat = $actual; CauHinh = ($expected -join ' | ')
    KetLuan = $verdict; LanCuoi = $latest.LastTs; GhiChu = $ghiChu
  }
}
$rows = $rows | Sort-Object @{ E = { $_.KetLuan -eq 'OK' } }, TinhNang

$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

Write-Host "Chi cham LAN CHAY GAN NHAT cua moi tinh nang. Cot GhiChu = so model khac tung dung" -ForegroundColor DarkGray
Write-Host "truoc do trong cua so $Days ngay (doi cau hinh / chay A-B) — khong tinh la lech." -ForegroundColor DarkGray
Write-Host ""

$lech = @($rows | Where-Object { $_.KetLuan -eq 'LECH' -or $_.KetLuan -eq 'THIEU KHOA' })
if ($lech.Count -eq 0) {
  Write-Host "Khong thay lech." -ForegroundColor Green
} else {
  Write-Host "$($lech.Count) dong LECH — kiem appsettings cua may dang chay tinh nang do:" -ForegroundColor Yellow
  foreach ($x in $lech) {
    Write-Host ("  {0}: chay {1} nhung cau hinh la {2}" -f $x.TinhNang, $x.ChayThat, $x.CauHinh)
  }
  Write-Host ""
  Write-Host "mail-auto-sync / deal-auto-review / customer-auto-review / digest chay o WORKER" -ForegroundColor Yellow
  Write-Host "-> sua C:\Services\TourkitAiWorker\appsettings.json roi: sc.exe stop/start TourkitAiProxyWorker"
}
Write-Host ""

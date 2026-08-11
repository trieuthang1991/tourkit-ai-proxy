# check-provider.ps1 - Xem AI call GAN NHAT that su chay provider/model nao (READ-ONLY tren dbo.AiUsageHistory).
#
# VI SAO CAN: he thong co FALLBACK AM THAM (chot chan co chu y) - provider cau hinh chet thi tu chuyen
# sang provider mac dinh va van tra loi binh thuong. Nen E2E co the PASS 100% ma KHONG he chay provider
# ban dinh test. Doi provider/model xong -> chay script nay de xac nhan.
#
# ASCII-only co y (PowerShell 5.1 loi parser voi .ps1 tieng Viet khong BOM).
# Connection string giai ma TRONG BO NHO, khong bao gio in ra.
#
# Vi du:
#   .\scripts\e2e\check-provider.ps1
#   .\scripts\e2e\check-provider.ps1 -Top 30 -Feature chat
#   .\scripts\e2e\check-provider.ps1 -Summary          # gop theo provider/model 3 gio qua

[CmdletBinding()]
param(
    [int]    $Top     = 15,
    [string] $Feature = 'chat',
    [int]    $Hours   = 3,
    [switch] $Summary
)

$ErrorActionPreference = 'Stop'

$appsettings = Join-Path $PSScriptRoot '..\..\appsettings.json'
if (-not (Test-Path $appsettings)) { Write-Host "Khong tim thay appsettings.json" -ForegroundColor Red; exit 1 }

$cfg = Get-Content $appsettings -Raw | ConvertFrom-Json
$cs  = $cfg.ConnectionStrings.PushDb
if ($cs.StartsWith('ENC:')) {
    $pass='Pas5pr@se'
    $salt=[Text.Encoding]::ASCII.GetBytes('s@1tValue')
    $iv=[Text.Encoding]::ASCII.GetBytes('@1B2c3D4e5F6g7H8')
    $pdb = New-Object System.Security.Cryptography.PasswordDeriveBytes($pass, $salt, 'SHA1', 2)
    $key = $pdb.GetBytes(32)
    $aes = [System.Security.Cryptography.Aes]::Create(); $aes.Mode='CBC'; $aes.Key=$key; $aes.IV=$iv
    $bytes = [Convert]::FromBase64String($cs.Substring(4))
    $dec = $aes.CreateDecryptor().TransformFinalBlock($bytes, 0, $bytes.Length)
    $cs = [Text.Encoding]::UTF8.GetString($dec)
}
# System.Data.SqlClient (PS 5.1) khong ho tro 'Command Timeout'; server dung self-signed cert.
$cs = ($cs -split ';' | Where-Object { $_ -notmatch '^\s*Command Timeout\s*=' -and $_ -ne '' }) -join ';'
if ($cs -notmatch 'TrustServerCertificate') { $cs += ';TrustServerCertificate=True' }

$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandTimeout = 30

if ($Summary) {
    $cmd.CommandText = @"
SELECT Provider, Model,
       COUNT(*) AS Calls,
       SUM(CASE WHEN Status='ok' THEN 1 ELSE 0 END) AS OkCalls,
       AVG(LatencyMs) AS AvgMs, MAX(LatencyMs) AS MaxMs,
       AVG(CAST(InTok AS FLOAT)) AS AvgIn, AVG(CAST(OutTok AS FLOAT)) AS AvgOut
FROM dbo.AiUsageHistory
WHERE Ts >= DATEADD(HOUR,-$Hours,SYSUTCDATETIME())
  AND (@f = '' OR Feature = @f)
GROUP BY Provider, Model
ORDER BY Calls DESC
"@
} else {
    $cmd.CommandText = @"
SELECT TOP $Top Ts, Feature, Provider, Model, Status, LatencyMs, InTok, OutTok
FROM dbo.AiUsageHistory
WHERE (@f = '' OR Feature = @f)
ORDER BY Id DESC
"@
}
[void]$cmd.Parameters.AddWithValue('@f', $Feature)

$da = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
$dt = New-Object System.Data.DataTable
[void]$da.Fill($dt)
$dt | Format-Table -AutoSize | Out-String -Width 200
$conn.Close()

Write-Host 'Luu y: he thong co fallback am tham (chot chan). Neu provider o day KHAC voi Models:ChatAnalytics' -ForegroundColor DarkYellow
Write-Host 'trong appsettings.json thi provider cau hinh dang LOI va he thong da tu chuyen sang provider khac.' -ForegroundColor DarkYellow

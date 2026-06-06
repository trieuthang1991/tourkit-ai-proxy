# scripts/publish.ps1
# Build + publish TourkitAiProxy ra folder ./publish (framework-dependent, Release).
# Sau khi chạy xong, copy nội dung folder lên server, tạo appsettings.json từ
# appsettings.example.json và chạy `dotnet TourkitAiProxy.dll` (hoặc IIS host).
#
# Usage:
#   scripts\publish.ps1                       # Linux x64 (cho Docker / Linux host)
#   scripts\publish.ps1 -Runtime win-x64      # Windows x64 (cho IIS)
#   scripts\publish.ps1 -SelfContained        # Bundle .NET runtime (nặng hơn nhưng không cần cài .NET trên server)
#   scripts\publish.ps1 -OutDir D:\deploy\proxy
#
# Sau khi build:
#   • Folder out chứa TourkitAiProxy.dll/.exe + wwwroot + appsettings.example.json + data/customers.seed.json
#   • KHÔNG copy data nhạy cảm (mails.json, tk-sessions.json, deal-cache, visa-files…) — đã gitignore
#   • KHÔNG copy appsettings.json (chứa key thật)

[CmdletBinding()]
param(
    [string]$Runtime = "linux-x64",
    [string]$Configuration = "Release",
    [string]$OutDir = "$PSScriptRoot\..\publish",
    [switch]$SelfContained,
    [switch]$NoBuild,
    [switch]$NoTests,
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path "$PSScriptRoot\..").Path
$Csproj = Join-Path $Root "TourkitAiProxy.csproj"
$TestsProj = Join-Path $Root "TourkitAiProxy.Tests\TourkitAiProxy.Tests.csproj"

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

Step "TourkitAiProxy publish"
Write-Host "  Runtime       : $Runtime"
Write-Host "  Configuration : $Configuration"
Write-Host "  OutDir        : $OutDir"
Write-Host "  SelfContained : $SelfContained"

# Stop running instance để khỏi lock TourkitAiProxy.dll/.exe khi rebuild
Get-Process -Name TourkitAiProxy -ErrorAction SilentlyContinue | ForEach-Object {
    Step "Stop process TourkitAiProxy PID $($_.Id)"
    $_.Kill()
    Start-Sleep -Seconds 1
}

# Pre-flight: appsettings.example.json phải có (deploy sẽ copy mẫu)
$Example = Join-Path $Root "appsettings.example.json"
if (-not (Test-Path $Example)) { throw "Thiếu appsettings.example.json — cần file mẫu để deploy" }

if (-not $NoTests -and (Test-Path $TestsProj)) {
    Step "Run unit tests"
    dotnet test $TestsProj --nologo --configuration $Configuration -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests fail — abort publish" }
}

if (Test-Path $OutDir) {
    Step "Clean $OutDir"
    Remove-Item $OutDir -Recurse -Force
}

Step "dotnet publish"
$args = @(
    "publish", $Csproj,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $OutDir,
    "--nologo"
)
if ($SelfContained) {
    $args += @("--self-contained", "true", "-p:PublishSingleFile=false")
} else {
    $args += @("--self-contained", "false")
}
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish lỗi" }

# Copy file mẫu — server admin đổi tên thành appsettings.json + điền key thật
Step "Copy appsettings.example.json"
Copy-Item $Example (Join-Path $OutDir "appsettings.example.json") -Force

# Đảm bảo data/customers.seed.json đi cùng (seed read-only, không phải runtime state)
$SeedSrc = Join-Path $Root "data\customers.seed.json"
if (Test-Path $SeedSrc) {
    $DataOut = Join-Path $OutDir "data"
    New-Item -ItemType Directory -Force -Path $DataOut | Out-Null
    Copy-Item $SeedSrc (Join-Path $DataOut "customers.seed.json") -Force
}

# Đếm file để xác minh thành công
$count = (Get-ChildItem $OutDir -Recurse -File | Measure-Object).Count
Step "Done — $count file trong $OutDir"

if ($Zip) {
    $ZipPath = "$OutDir.zip"
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Step "Compress → $ZipPath"
    Compress-Archive -Path "$OutDir\*" -DestinationPath $ZipPath -CompressionLevel Optimal
    $size = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
    Write-Host "  Archive: $size MB" -ForegroundColor Green
}

Step "Next steps trên server"
Write-Host "  1. Copy $OutDir → server (scp/rsync/IIS)"
Write-Host "  2. cp appsettings.example.json appsettings.json    # rồi điền API keys thật"
Write-Host "  3. dotnet TourkitAiProxy.dll                        # chạy"
Write-Host "     hoặc: cấu hình IIS hosting bundle + recycle app pool"

# scripts/docker-publish.ps1
# Build Docker image + tag (latest + sha ngắn của HEAD) + (tuỳ chọn) push lên registry.
# Project đã có Dockerfile sẵn ở gốc — script này chỉ orchestrate.
#
# Usage:
#   scripts\docker-publish.ps1                                   # build local thôi
#   scripts\docker-publish.ps1 -Registry "registry.tourkit.vn"   # build + tag + push
#   scripts\docker-publish.ps1 -Tag "v1.2.0"                     # tag riêng (mặc định = latest + sha)
#   scripts\docker-publish.ps1 -NoCache                          # build từ đầu, không dùng cache

[CmdletBinding()]
param(
    [string]$Image    = "tourkit-ai-proxy",
    [string]$Registry = "",     # vd "ghcr.io/trieuthang1991" hoặc "registry.tourkit.vn" — trống = không push
    [string]$Tag      = "",     # trống = dùng latest + sha
    [switch]$NoCache,
    [switch]$Push                # mặc định không push, có Registry vẫn cần -Push
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path "$PSScriptRoot\..").Path

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# Verify docker available
try { docker version --format '{{.Server.Version}}' | Out-Null } catch { throw "Docker chưa cài/chưa chạy — xem https://docker.com" }

# Tag suy ra từ git HEAD nếu không truyền
if (-not $Tag) {
    Push-Location $Root
    $Tag = (git rev-parse --short HEAD).Trim()
    Pop-Location
}

$LocalTag  = "${Image}:$Tag"
$LatestTag = "${Image}:latest"
$RemoteTag  = if ($Registry) { "$Registry/$LocalTag" }  else { $null }
$RemoteLatest = if ($Registry) { "$Registry/$LatestTag" } else { $null }

Step "Build image"
Write-Host "  Local : $LocalTag, $LatestTag"
if ($RemoteTag) { Write-Host "  Remote: $RemoteTag, $RemoteLatest" }

$buildArgs = @("build", "-t", $LocalTag, "-t", $LatestTag)
if ($NoCache) { $buildArgs += "--no-cache" }
$buildArgs += $Root
& docker @buildArgs
if ($LASTEXITCODE -ne 0) { throw "docker build lỗi" }

if ($RemoteTag) {
    Step "Tag remote"
    docker tag $LocalTag  $RemoteTag
    docker tag $LatestTag $RemoteLatest

    if ($Push) {
        Step "Push"
        docker push $RemoteTag
        docker push $RemoteLatest
        Write-Host "  ✓ Pushed $RemoteTag + :latest" -ForegroundColor Green
    } else {
        Write-Host "`n  (Chưa push — thêm -Push để đẩy lên $Registry)" -ForegroundColor Yellow
    }
}

Step "Run local test"
Write-Host "  docker run --rm -p 5080:8080 \" -ForegroundColor Gray
Write-Host "    -e Providers__OpenCode__ApiKey=`"sk-...`" \" -ForegroundColor Gray
Write-Host "    $LocalTag" -ForegroundColor Gray

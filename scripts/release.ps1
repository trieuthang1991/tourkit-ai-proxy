# scripts/release.ps1
# Tag commit hiện tại + push lên origin → trigger CI/CD bên ngoài hoặc dùng cho versioning.
#
# Usage:
#   scripts\release.ps1 -Version "1.4.0"
#   scripts\release.ps1 -Version "1.4.0" -Message "Soạn Tour GIT bằng AI + cost guardrails"

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,
    [string]$Message = "",
    [switch]$NoPush
)

$ErrorActionPreference = "Stop"
function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

Push-Location "$PSScriptRoot\.."
try {
    # Verify clean working tree
    $status = (git status --porcelain).Trim()
    if ($status) {
        Write-Host "  Working tree không sạch:" -ForegroundColor Yellow
        Write-Host $status
        throw "Commit/stash mọi thay đổi trước khi release"
    }

    # Verify version format vX.Y.Z (X.Y.Z hay v-prefix đều OK)
    if ($Version -notmatch '^v?\d+\.\d+\.\d+(-\w+)?$') {
        throw "Version phải dạng X.Y.Z hoặc vX.Y.Z (vd 1.4.0, v1.4.0-rc.1)"
    }
    $Tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }

    # Verify tag chưa tồn tại
    if (git tag --list $Tag) { throw "Tag $Tag đã tồn tại" }

    $Branch = (git rev-parse --abbrev-ref HEAD).Trim()
    $Sha = (git rev-parse --short HEAD).Trim()

    Step "Tag $Tag @ $Sha (branch $Branch)"
    $msg = if ($Message) { $Message } else { "Release $Tag" }
    git tag -a $Tag -m $msg
    Write-Host "  ✓ Tag tạo local" -ForegroundColor Green

    if (-not $NoPush) {
        Step "Push tag → origin"
        git push origin $Tag
        Write-Host "  ✓ Tag pushed" -ForegroundColor Green
    } else {
        Write-Host "  (Chưa push — chạy 'git push origin $Tag' khi sẵn sàng)" -ForegroundColor Yellow
    }

    Step "Next steps"
    Write-Host "  • GitHub: https://github.com/trieuthang1991/tourkit-ai-proxy/releases/new?tag=$Tag"
    Write-Host "  • Hoặc tạo binary: scripts\publish.ps1 -Zip"
    Write-Host "  • Hoặc docker:    scripts\docker-publish.ps1 -Tag $Tag"
} finally {
    Pop-Location
}

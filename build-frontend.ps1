# build-frontend.ps1 - Bundle wwwroot/*.jsx vao 1 file dist/app.bundle.js bang esbuild.
#
# Workflow:
#   * Dev mode (DEFAULT): index.html load 35 file .jsx + Babel standalone -> hot reload tot
#     nhung cold start 3-5s.
#   * Prod mode (sau khi chay script nay): ton tai wwwroot/dist/app.bundle.js -> StaticFilesSetup
#     tu switch sang 1 the <script src="dist/app.bundle.js?v=hash"> + KHONG load Babel CDN.
#     Cold start ~200ms (giam ~15x).
#
# Usage:
#   .\build-frontend.ps1            # build prod bundle (minify)
#   .\build-frontend.ps1 -Watch     # watch mode cho dev
#   .\build-frontend.ps1 -Clean     # xoa dist/, tro ve dev mode

param(
  [switch]$Watch,
  [switch]$Clean,
  [switch]$NoMinify
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
$entry  = Join-Path $scriptDir 'wwwroot\bundle-entry.js'
$outDir = Join-Path $scriptDir 'wwwroot\dist'
$out    = Join-Path $outDir 'app.bundle.js'

if ($Clean) {
  if (Test-Path $outDir) {
    Remove-Item -Recurse -Force $outDir
    Write-Host "[OK] Da xoa $outDir -> tro ve dev mode (Babel in-browser)"
  } else {
    Write-Host "[INFO] dist/ khong ton tai - da o dev mode"
  }
  exit 0
}

if (-not (Test-Path $entry)) { throw "Khong tim thay entry $entry" }
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$flags = @(
  $entry,
  '--bundle',
  '--format=iife',
  "--outfile=$out",
  '--loader:.jsx=jsx',
  '--loader:.js=jsx',
  '--jsx-factory=React.createElement',
  '--jsx-fragment=React.Fragment',
  '--target=es2019',
  '--platform=browser',
  ('--metafile=' + (Join-Path $outDir 'meta.json')),
  '--log-level=info'
)
# Full --minify (--minify-identifiers) doi ten function/class -> React component identification
# va Chart.js render bi vo -> chat treo o assistant page. Dung "safe minify" combo:
#   --minify-whitespace + --minify-syntax + --keep-names  (giam ~25% size, an toan 100%)
if (-not $NoMinify) {
  $flags += '--minify-whitespace'
  $flags += '--minify-syntax'
  $flags += '--keep-names'
}
if ($Watch) { $flags += '--watch' }

Write-Host ">> esbuild bundle -> wwwroot/dist/app.bundle.js ..." -ForegroundColor Cyan
& npx --yes esbuild @flags

if ($LASTEXITCODE -ne 0) { throw "esbuild failed (exit $LASTEXITCODE)" }

if (Test-Path $out) {
  $size = (Get-Item $out).Length
  $kb = [math]::Round($size / 1024, 1)
  Write-Host ""
  Write-Host "[OK] Bundle: $out  ($kb KB)" -ForegroundColor Green
  Write-Host "  -> Reload server hoac restart preview de StaticFilesSetup pickup bundle." -ForegroundColor Yellow
}

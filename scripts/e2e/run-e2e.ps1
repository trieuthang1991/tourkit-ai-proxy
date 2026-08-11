# run-e2e.ps1 - Chay E2E qua API that (/api/v1/chat).
#
# HAI LOAI FILE CASE (phan biet bang CA thu muc LAN tien to ten file):
#   scripts/e2e/features/features-*.cases.json  -> E2E TINH NANG. Chay THUONG XUYEN.
#   scripts/e2e/specs/spec-*.cases.json         -> E2E SPEC/BUG. Chay khi dung vao vung code do.
#
# ASCII-only co y (PowerShell 5.1 parser loi voi .ps1 chua tieng Viet neu khong co BOM).
# Cau hoi tieng Viet nam trong file .cases.json (doc bang -Encoding UTF8).
#
# CANH BAO CHI PHI: moi cau hoi ton ~2 luot AI (planner + phan tich) cua tenant dang dang nhap.
#
# Vi du:
#   .\scripts\e2e\run-e2e.ps1 -ListOnly                                  # xem co gi, khong ton quota
#   .\scripts\e2e\run-e2e.ps1 -SessionId <sid> -Kind features -Suite smoke
#   .\scripts\e2e\run-e2e.ps1 -SessionId <sid> -Kind features            # chay dinh ky
#   .\scripts\e2e\run-e2e.ps1 -SessionId <sid> -Kind specs               # sau khi sua JsonPlannerAgent
#   .\scripts\e2e\run-e2e.ps1 -SessionId <sid> -CaseId spec-6ddcf65-follow-up-thuong-giu-panel
#
# Lay SessionId: dang nhap /assistant roi mo DevTools Console:
#   localStorage.getItem('tourkit_tk_session')
#
# Exit code: 0 = pass het, 1 = co FAIL.

[CmdletBinding()]
param(
    [string] $BaseUrl   = 'http://localhost:5080',
    [string] $SessionId = '',
    [ValidateSet('all', 'features', 'specs')]
    [string] $Kind      = 'all',
    [string] $Suite     = '',      # smoke | core | routing | regression (tuy file)
    [string] $Feature   = '',      # loc theo ten tinh nang, vd chat-analytics
    [string] $CaseId    = '',
    [int]    $DelayMs   = 800,
    [switch] $ListOnly
)

$ErrorActionPreference = 'Stop'

# ---------- nap case tu ca 2 thu muc ----------

$dirs = @()
if ($Kind -eq 'all' -or $Kind -eq 'features') { $dirs += (Join-Path $PSScriptRoot 'features') }
if ($Kind -eq 'all' -or $Kind -eq 'specs')    { $dirs += (Join-Path $PSScriptRoot 'specs') }

$all = New-Object System.Collections.ArrayList
foreach ($d in $dirs) {
    if (-not (Test-Path $d)) { continue }
    foreach ($f in (Get-ChildItem -Path $d -Filter '*.cases.json' -File | Sort-Object Name)) {
        $bank = Get-Content -Raw -Encoding UTF8 $f.FullName | ConvertFrom-Json
        $bankKind  = [string]$bank.kind
        $bankName  = ''
        if ($bank.PSObject.Properties.Name -contains 'feature') { $bankName = [string]$bank.feature }
        elseif ($bank.PSObject.Properties.Name -contains 'spec') { $bankName = [string]$bank.spec }
        foreach ($c in @($bank.cases)) {
            [void]$all.Add([pscustomobject]@{
                Kind  = $bankKind
                Name  = $bankName
                Title = [string]$bank.title
                File  = $f.Name
                Case  = $c
            })
        }
    }
}

$sel = $all
if (-not [string]::IsNullOrWhiteSpace($Suite))   { $sel = @($sel | Where-Object { $_.Case.suite -eq $Suite }) }
if (-not [string]::IsNullOrWhiteSpace($Feature)) { $sel = @($sel | Where-Object { $_.Name  -eq $Feature }) }
if (-not [string]::IsNullOrWhiteSpace($CaseId))  { $sel = @($sel | Where-Object { $_.Case.id -eq $CaseId }) }

if ($ListOnly) {
    Write-Host ''
    foreach ($grp in ($sel | Group-Object Kind, Name)) {
        $first = $grp.Group[0]
        $label = 'TINH NANG'
        if ($first.Kind -eq 'spec') { $label = 'SPEC/BUG ' }
        Write-Host ("{0} | {1} | {2}" -f $label, $first.Name, $first.Title) -ForegroundColor Cyan
        Write-Host ("            file: {0}" -f $first.File) -ForegroundColor DarkGray
        foreach ($x in $grp.Group) {
            Write-Host ("   [{0,-10}] {1}" -f $x.Case.suite, $x.Case.id)
            Write-Host ("                {0}" -f $x.Case.why) -ForegroundColor DarkGray
        }
        Write-Host ''
    }
    $nAsk = (($sel | ForEach-Object { $_.Case.steps.Count }) | Measure-Object -Sum).Sum
    Write-Host ("Tong: {0} case, {1} cau hoi (~{2} luot AI)." -f $sel.Count, $nAsk, ($nAsk * 2))
    exit 0
}

if ([string]::IsNullOrWhiteSpace($SessionId)) {
    Write-Host 'Thieu -SessionId. Lay o DevTools Console: localStorage.getItem("tourkit_tk_session")' -ForegroundColor Red
    exit 1
}
if ($sel.Count -eq 0) {
    Write-Host 'Khong co case nao khop bo loc.' -ForegroundColor Yellow
    exit 1
}

$totalAsks = (($sel | ForEach-Object { $_.Case.steps.Count }) | Measure-Object -Sum).Sum
Write-Host ''
Write-Host ("E2E | {0} | kind={1} | {2} case / {3} cau hoi" -f $BaseUrl, $Kind, $sel.Count, $totalAsks) -ForegroundColor Cyan
Write-Host ("Uoc tinh ton ~{0} luot quota AI cua tenant dang dang nhap." -f ($totalAsks * 2)) -ForegroundColor DarkYellow
Write-Host ''

# ---------- helpers ----------

function Invoke-ChatAsk {
    param([string] $Question, [System.Collections.ArrayList] $History)
    $msgs = New-Object System.Collections.ArrayList
    foreach ($h in $History) { [void]$msgs.Add($h) }
    [void]$msgs.Add(@{ role = 'user'; content = $Question })
    $json  = @{ messages = $msgs } | ConvertTo-Json -Depth 8 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    return Invoke-RestMethod -Uri "$BaseUrl/api/v1/chat" -Method Post `
        -Headers @{ 'X-Session-Id' = $SessionId } `
        -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec 180
}

function Clear-ChatMemory {
    try {
        Invoke-RestMethod -Uri "$BaseUrl/api/v1/chat/memory" -Method Delete `
            -Headers @{ 'X-Session-Id' = $SessionId } -TimeoutSec 30 | Out-Null
    } catch {
        Write-Host '  (khong xoa duoc bo nho hoi thoai - bo qua)' -ForegroundColor DarkGray
    }
}

function Test-HasData {
    param($Data)
    if ($null -eq $Data) { return $false }
    if ($Data.PSObject.Properties.Name -contains 'stats' -and $Data.stats -and @($Data.stats).Count -gt 0) { return $true }
    if ($Data.PSObject.Properties.Name -contains 'raw' -and $Data.raw) {
        if ($Data.raw -is [array] -and @($Data.raw).Count -gt 0) { return $true }
    }
    return $false
}

function Test-Expectations {
    param($Expect, $Resp, [string] $PrevReply)
    $errs  = New-Object System.Collections.ArrayList
    $reply = [string]$Resp.reply
    $tool  = [string]$Resp.toolName
    if ($null -eq $Expect) { return $errs }
    $names = $Expect.PSObject.Properties.Name

    if ($names -contains 'hasData') {
        $has = Test-HasData -Data $Resp.data
        if ($Expect.hasData -and (-not $has)) { [void]$errs.Add('cho doi CO so lieu (data) nhung data rong/null') }
        if ((-not $Expect.hasData) -and $has) { [void]$errs.Add('cho doi KHONG co so lieu nhung data lai co') }
    }
    if ($names -contains 'toolNameIn') {
        if (-not (@($Expect.toolNameIn) -contains $tool)) {
            [void]$errs.Add(("toolName='{0}' khong nam trong [{1}]" -f $tool, (@($Expect.toolNameIn) -join ', ')))
        }
    }
    if ($names -contains 'replyContains') {
        foreach ($s in @($Expect.replyContains)) {
            if ($reply -notmatch [regex]::Escape($s)) { [void]$errs.Add(("reply THIEU chuoi '{0}'" -f $s)) }
        }
    }
    if ($names -contains 'replyNotContains') {
        foreach ($s in @($Expect.replyNotContains)) {
            if ($reply -match [regex]::Escape($s)) { [void]$errs.Add(("reply LO chuoi cam '{0}'" -f $s)) }
        }
    }
    if ($names -contains 'replyDiffersFromPrevious' -and $Expect.replyDiffersFromPrevious) {
        if (-not [string]::IsNullOrWhiteSpace($PrevReply)) {
            if ($reply.Trim() -eq $PrevReply.Trim()) { [void]$errs.Add('reply GIONG HET buoc truoc (lap cau tra loi)') }
        }
    }
    return $errs
}

# ---------- chay ----------

$results = New-Object System.Collections.ArrayList
$nPass = 0; $nFail = 0; $nWarn = 0

foreach ($item in $sel) {
    $case = $item.Case
    $tag  = 'TINH NANG'
    if ($item.Kind -eq 'spec') { $tag = 'SPEC/BUG' }

    Write-Host ("[{0}][{1}] {2}" -f $tag, $case.suite, $case.id) -ForegroundColor White
    Write-Host ("   {0}" -f $case.why) -ForegroundColor DarkGray

    $fresh = $true
    if ($case.PSObject.Properties.Name -contains 'freshConversation') { $fresh = [bool]$case.freshConversation }
    if ($fresh) { Clear-ChatMemory }

    $history   = New-Object System.Collections.ArrayList
    $prevReply = ''

    foreach ($step in $case.steps) {
        $isSoft = $false
        if ($step.PSObject.Properties.Name -contains 'soft') { $isSoft = [bool]$step.soft }

        Write-Host ("   > {0}" -f $step.ask) -ForegroundColor Gray

        $resp = $null; $callErr = $null
        try { $resp = Invoke-ChatAsk -Question $step.ask -History $history }
        catch { $callErr = $_.Exception.Message }

        if ($callErr) {
            Write-Host ("     FAIL - goi API loi: {0}" -f $callErr) -ForegroundColor Red
            $nFail++
            [void]$results.Add([pscustomobject]@{ Tag=$tag; Case=$case.id; Ask=$step.ask; Status='FAIL'; Detail=$callErr })
            break
        }

        $errs    = Test-Expectations -Expect $step.expect -Resp $resp -PrevReply $prevReply
        $tool    = [string]$resp.toolName
        $hasD    = Test-HasData -Data $resp.data
        $snippet = [string]$resp.reply
        if ($snippet.Length -gt 90) { $snippet = $snippet.Substring(0, 90) + '...' }

        if ($errs.Count -eq 0) {
            Write-Host ("     PASS  tool={0} data={1} | {2}" -f $tool, $hasD, $snippet) -ForegroundColor Green
            $nPass++
            [void]$results.Add([pscustomobject]@{ Tag=$tag; Case=$case.id; Ask=$step.ask; Status='PASS'; Detail='' })
        }
        elseif ($isSoft) {
            Write-Host ("     WARN  tool={0} data={1}" -f $tool, $hasD) -ForegroundColor Yellow
            foreach ($e in $errs) { Write-Host ("           - {0}" -f $e) -ForegroundColor Yellow }
            $nWarn++
            [void]$results.Add([pscustomobject]@{ Tag=$tag; Case=$case.id; Ask=$step.ask; Status='WARN'; Detail=($errs -join ' | ') })
        }
        else {
            Write-Host ("     FAIL  tool={0} data={1}" -f $tool, $hasD) -ForegroundColor Red
            foreach ($e in $errs) { Write-Host ("           - {0}" -f $e) -ForegroundColor Red }
            Write-Host ("           reply: {0}" -f $snippet) -ForegroundColor DarkRed
            $nFail++
            [void]$results.Add([pscustomobject]@{ Tag=$tag; Case=$case.id; Ask=$step.ask; Status='FAIL'; Detail=($errs -join ' | ') })
        }

        [void]$history.Add(@{ role = 'user';      content = $step.ask })
        [void]$history.Add(@{ role = 'assistant'; content = [string]$resp.reply })
        $prevReply = [string]$resp.reply

        if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }
    }
    Write-Host ''
}

# ---------- tong ket ----------

Write-Host '================ TONG KET ================' -ForegroundColor Cyan
Write-Host ("PASS: {0}   WARN: {1}   FAIL: {2}" -f $nPass, $nWarn, $nFail)

if ($nFail -gt 0) {
    Write-Host ''
    Write-Host 'CAC BUOC FAIL:' -ForegroundColor Red
    $results | Where-Object { $_.Status -eq 'FAIL' } | ForEach-Object {
        Write-Host ("  [{0}][{1}] {2}" -f $_.Tag, $_.Case, $_.Ask) -ForegroundColor Red
        Write-Host ("      {0}" -f $_.Detail) -ForegroundColor DarkRed
    }
}
if ($nWarn -gt 0) {
    Write-Host ''
    Write-Host 'CAC BUOC WARN (mem - phu thuoc AI routing, khong tinh fail):' -ForegroundColor Yellow
    $results | Where-Object { $_.Status -eq 'WARN' } | ForEach-Object {
        Write-Host ("  [{0}][{1}] {2} -> {3}" -f $_.Tag, $_.Case, $_.Ask, $_.Detail) -ForegroundColor Yellow
    }
}

Write-Host ''
if ($nFail -gt 0) { exit 1 } else { exit 0 }

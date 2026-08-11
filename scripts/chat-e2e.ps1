# chat-e2e.ps1 - Chay bo cau hoi E2E cho Tro ly so lieu (/api/v1/chat).
#
# ASCII-only co y (PowerShell 5.1 parser loi voi file .ps1 chua tieng Viet neu khong co BOM).
# Cau hoi tieng Viet nam trong scripts/chat-e2e-cases.json (doc bang -Encoding UTF8).
#
# CANH BAO CHI PHI: moi cau hoi ton ~2 luot AI (planner + phan tich) cua tenant dang dang nhap.
# Bo day du ~20 cau => ~40 luot quota. Dung -Suite smoke de chay nhanh 2 cau.
#
# Vi du:
#   .\scripts\chat-e2e.ps1 -SessionId abc123 -Suite smoke
#   .\scripts\chat-e2e.ps1 -SessionId abc123 -Suite regression
#   .\scripts\chat-e2e.ps1 -SessionId abc123 -CaseId reg-02-hoi-nguon-goc-khong-lap
#   .\scripts\chat-e2e.ps1 -ListOnly
#
# Lay SessionId: dang nhap /assistant roi mo DevTools Console:
#   localStorage.getItem('tourkit_tk_session')
#
# Exit code: 0 = tat ca pass, 1 = co case fail (dung duoc cho CI sau nay).

[CmdletBinding()]
param(
    [string] $BaseUrl   = 'http://localhost:5080',
    [string] $SessionId = '',
    [ValidateSet('all', 'smoke', 'regression', 'routing')]
    [string] $Suite     = 'all',
    [string] $CaseId    = '',
    [int]    $DelayMs   = 800,
    [string] $CasesFile = '',
    [switch] $ListOnly
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($CasesFile)) {
    $CasesFile = Join-Path $PSScriptRoot 'chat-e2e-cases.json'
}
if (-not (Test-Path $CasesFile)) {
    Write-Host "Khong tim thay file cases: $CasesFile" -ForegroundColor Red
    exit 1
}

$bank  = Get-Content -Raw -Encoding UTF8 $CasesFile | ConvertFrom-Json
$cases = @($bank.cases)

if ($Suite -ne 'all')                          { $cases = @($cases | Where-Object { $_.suite -eq $Suite }) }
if (-not [string]::IsNullOrWhiteSpace($CaseId)) { $cases = @($cases | Where-Object { $_.id -eq $CaseId }) }

if ($ListOnly) {
    Write-Host ''
    Write-Host 'DANH SACH CASE' -ForegroundColor Cyan
    foreach ($c in $cases) {
        Write-Host ("  [{0,-10}] {1}" -f $c.suite, $c.id)
        Write-Host ("               {0}" -f $c.why) -ForegroundColor DarkGray
    }
    Write-Host ''
    Write-Host ("Tong: {0} case, {1} cau hoi." -f $cases.Count, (($cases | ForEach-Object { $_.steps.Count }) | Measure-Object -Sum).Sum)
    exit 0
}

if ([string]::IsNullOrWhiteSpace($SessionId)) {
    Write-Host 'Thieu -SessionId. Lay o DevTools Console: localStorage.getItem("tourkit_tk_session")' -ForegroundColor Red
    exit 1
}
if ($cases.Count -eq 0) {
    Write-Host 'Khong co case nao khop bo loc.' -ForegroundColor Yellow
    exit 1
}

$totalAsks = (($cases | ForEach-Object { $_.steps.Count }) | Measure-Object -Sum).Sum
Write-Host ''
Write-Host ("E2E Tro ly so lieu | {0} | suite={1} | {2} case / {3} cau hoi" -f $BaseUrl, $Suite, $cases.Count, $totalAsks) -ForegroundColor Cyan
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

# data co noi dung that? (stats co phan tu HOAC raw la mang khong rong)
function Test-HasData {
    param($Data)
    if ($null -eq $Data) { return $false }
    if ($Data.PSObject.Properties.Name -contains 'stats' -and $Data.stats -and @($Data.stats).Count -gt 0) { return $true }
    if ($Data.PSObject.Properties.Name -contains 'raw'   -and $Data.raw) {
        if ($Data.raw -is [array] -and @($Data.raw).Count -gt 0) { return $true }
    }
    return $false
}

# Kiem 1 buoc; tra ve mang cac loi (rong = pass)
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
        $ok = @($Expect.toolNameIn) -contains $tool
        if (-not $ok) { [void]$errs.Add(("toolName='{0}' khong nam trong [{1}]" -f $tool, (@($Expect.toolNameIn) -join ', '))) }
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

$results  = New-Object System.Collections.ArrayList
$nPass = 0; $nFail = 0; $nWarn = 0

foreach ($case in $cases) {
    Write-Host ("[{0}] {1}" -f $case.suite, $case.id) -ForegroundColor White
    Write-Host ("   {0}" -f $case.why) -ForegroundColor DarkGray

    $fresh = $true
    if ($case.PSObject.Properties.Name -contains 'freshConversation') { $fresh = [bool]$case.freshConversation }
    if ($fresh) { Clear-ChatMemory }

    $history   = New-Object System.Collections.ArrayList
    $prevReply = ''
    $caseFailed = $false
    $caseWarned = $false

    foreach ($step in $case.steps) {
        $isSoft = $false
        if ($step.PSObject.Properties.Name -contains 'soft') { $isSoft = [bool]$step.soft }

        Write-Host ("   > {0}" -f $step.ask) -ForegroundColor Gray

        $resp = $null
        $callErr = $null
        try {
            $resp = Invoke-ChatAsk -Question $step.ask -History $history
        } catch {
            $callErr = $_.Exception.Message
        }

        if ($callErr) {
            Write-Host ("     FAIL - goi API loi: {0}" -f $callErr) -ForegroundColor Red
            $caseFailed = $true
            [void]$results.Add([pscustomobject]@{ Case=$case.id; Ask=$step.ask; Status='FAIL'; Detail=$callErr; Tool=''; })
            break
        }

        $errs = Test-Expectations -Expect $step.expect -Resp $resp -PrevReply $prevReply
        $tool = [string]$resp.toolName
        $hasD = Test-HasData -Data $resp.data
        $snippet = [string]$resp.reply
        if ($snippet.Length -gt 90) { $snippet = $snippet.Substring(0, 90) + '...' }

        if ($errs.Count -eq 0) {
            Write-Host ("     PASS  tool={0} data={1} | {2}" -f $tool, $hasD, $snippet) -ForegroundColor Green
            $nPass++
            [void]$results.Add([pscustomobject]@{ Case=$case.id; Ask=$step.ask; Status='PASS'; Detail=''; Tool=$tool })
        }
        elseif ($isSoft) {
            Write-Host ("     WARN  tool={0} data={1}" -f $tool, $hasD) -ForegroundColor Yellow
            foreach ($e in $errs) { Write-Host ("           - {0}" -f $e) -ForegroundColor Yellow }
            $nWarn++; $caseWarned = $true
            [void]$results.Add([pscustomobject]@{ Case=$case.id; Ask=$step.ask; Status='WARN'; Detail=($errs -join ' | '); Tool=$tool })
        }
        else {
            Write-Host ("     FAIL  tool={0} data={1}" -f $tool, $hasD) -ForegroundColor Red
            foreach ($e in $errs) { Write-Host ("           - {0}" -f $e) -ForegroundColor Red }
            Write-Host ("           reply: {0}" -f $snippet) -ForegroundColor DarkRed
            $nFail++; $caseFailed = $true
            [void]$results.Add([pscustomobject]@{ Case=$case.id; Ask=$step.ask; Status='FAIL'; Detail=($errs -join ' | '); Tool=$tool })
        }

        # noi tiep hoi thoai cho buoc sau
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
        Write-Host ("  [{0}] {1}" -f $_.Case, $_.Ask) -ForegroundColor Red
        Write-Host ("      {0}" -f $_.Detail) -ForegroundColor DarkRed
    }
}
if ($nWarn -gt 0) {
    Write-Host ''
    Write-Host 'CAC BUOC WARN (mem - phu thuoc AI routing, khong tinh fail):' -ForegroundColor Yellow
    $results | Where-Object { $_.Status -eq 'WARN' } | ForEach-Object {
        Write-Host ("  [{0}] {1} -> {2}" -f $_.Case, $_.Ask, $_.Detail) -ForegroundColor Yellow
    }
}

Write-Host ''
if ($nFail -gt 0) { exit 1 } else { exit 0 }

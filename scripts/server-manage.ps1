# scripts/server-manage.ps1
# Manage Linux production server via SSH (Python + paramiko).
# Requirements: Python 3 (py -3) + paramiko (pip install paramiko)
# Config: scripts/.server-config.json (gitignored)
#
# Actions:
#   status         Server status (uptime, disk, RAM, service)
#   health         Full health check (system + PostgreSQL + app)
#   backup         Backup PostgreSQL tourkit-chat -> local backups/
#   restore        Restore from local backup file -> server
#   list-backups   List available backup files
#   logs           View app logs (default 100 lines)
#   restart        Restart tourkit-ai-proxy service
#   deploy         Deploy new build to server via SFTP
#   shell          Run arbitrary bash command on server
#
# Usage:
#   .\scripts\server-manage.ps1 -Action backup
#   .\scripts\server-manage.ps1 -Action backup -Tag "pre-migration"
#   .\scripts\server-manage.ps1 -Action restore -BackupFile "chat-2026-10-19-0439.dump"
#   .\scripts\server-manage.ps1 -Action logs -Lines 200
#   .\scripts\server-manage.ps1 -Action shell -Command "df -h /"
#   .\scripts\server-manage.ps1 -Action deploy -LocalPublishPath "out/"

param(
    [ValidateSet("status","health","backup","restore","list-backups","logs","restart","deploy","shell")]
    [string]$Action = "status",

    [string]$Command          = "",
    [int]$Lines               = 100,
    [string]$Tag              = "",
    [string]$BackupFile       = "",
    [string]$LocalPublishPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- Paths ---

$RepoRoot  = Split-Path $PSScriptRoot -Parent
$CfgPath   = Join-Path $PSScriptRoot ".server-config.json"
$BackupDir = Join-Path $RepoRoot "backups"
$OpsLog    = Join-Path $BackupDir "ops-log.txt"

# --- Helpers ---

function Ensure-Config {
    if (-not (Test-Path $CfgPath)) {
        $ex = Join-Path $PSScriptRoot ".server-config.example.json"
        Write-Error "Missing $CfgPath - copy from $ex and fill in real credentials."
        exit 1
    }
    $cfg = Get-Content $CfgPath -Raw | ConvertFrom-Json
    if (-not $cfg.backupDir) {
        $cfg | Add-Member -NotePropertyName backupDir -NotePropertyValue $BackupDir -Force
    }
    return $cfg
}

function Write-SSH-Script {
    param([string]$Body)
    $tmp = [System.IO.Path]::GetTempFileName() + ".py"
    [System.IO.File]::WriteAllText($tmp, $Body, [System.Text.Encoding]::UTF8)
    return $tmp
}

# Run SSH command, print output. Returns nothing.
function Invoke-SSH {
    param([string]$Cmd)
    $body = "import paramiko, sys`n" +
            "import json`n" +
            "cfg = json.loads(open(sys.argv[1], encoding='utf-8').read())`n" +
            "c = paramiko.SSHClient()`n" +
            "c.set_missing_host_key_policy(paramiko.AutoAddPolicy())`n" +
            "c.connect(cfg['host'], username=cfg.get('user','root'), password=cfg['password'], timeout=30)`n" +
            "_, stdout, stderr = c.exec_command(sys.argv[2], get_pty=False)`n" +
            "out = stdout.read().decode('utf-8', errors='replace')`n" +
            "err = stderr.read().decode('utf-8', errors='replace')`n" +
            "code = stdout.channel.recv_exit_status()`n" +
            "c.close()`n" +
            "print(out, end='')`n" +
            "if err.strip(): print('[ERR]', err.strip(), file=__import__('sys').stderr)`n" +
            "sys.exit(code)`n"
    $tmp = Write-SSH-Script $body
    try {
        py -3 $tmp $CfgPath $Cmd 2>&1 | Write-Host
    } finally { Remove-Item $tmp -Force -EA SilentlyContinue }
}

# Run SSH command, capture and return output string.
function Get-SSHOutput {
    param([string]$Cmd)
    $body = "import paramiko, sys`n" +
            "import json`n" +
            "cfg = json.loads(open(sys.argv[1], encoding='utf-8').read())`n" +
            "c = paramiko.SSHClient()`n" +
            "c.set_missing_host_key_policy(paramiko.AutoAddPolicy())`n" +
            "c.connect(cfg['host'], username=cfg.get('user','root'), password=cfg['password'], timeout=30)`n" +
            "_, stdout, _ = c.exec_command(sys.argv[2])`n" +
            "out = stdout.read().decode('utf-8', errors='replace')`n" +
            "c.close()`n" +
            "print(out, end='')`n"
    $tmp = Write-SSH-Script $body
    try { return (py -3 $tmp $CfgPath $Cmd 2>&1) }
    finally { Remove-Item $tmp -Force -EA SilentlyContinue }
}

# Download a file from server via SFTP.
function Get-RemoteFile {
    param([string]$RemotePath, [string]$LocalPath)
    $body = "import paramiko, sys`n" +
            "import json`n" +
            "cfg = json.loads(open(sys.argv[1], encoding='utf-8').read())`n" +
            "c = paramiko.SSHClient()`n" +
            "c.set_missing_host_key_policy(paramiko.AutoAddPolicy())`n" +
            "c.connect(cfg['host'], username=cfg.get('user','root'), password=cfg['password'], timeout=120)`n" +
            "sftp = c.open_sftp()`n" +
            "sftp.get(sys.argv[2], sys.argv[3])`n" +
            "sftp.close(); c.close()`n" +
            "print('Downloaded:', sys.argv[3])`n"
    $tmp = Write-SSH-Script $body
    try { py -3 $tmp $CfgPath $RemotePath $LocalPath }
    finally { Remove-Item $tmp -Force -EA SilentlyContinue }
}

# Upload a file to server via SFTP.
function Send-RemoteFile {
    param([string]$LocalPath, [string]$RemotePath)
    $body = "import paramiko, sys`n" +
            "import json`n" +
            "cfg = json.loads(open(sys.argv[1], encoding='utf-8').read())`n" +
            "c = paramiko.SSHClient()`n" +
            "c.set_missing_host_key_policy(paramiko.AutoAddPolicy())`n" +
            "c.connect(cfg['host'], username=cfg.get('user','root'), password=cfg['password'], timeout=60)`n" +
            "sftp = c.open_sftp()`n" +
            "sftp.put(sys.argv[2], sys.argv[3])`n" +
            "sftp.close(); c.close()`n" +
            "print('Uploaded:', sys.argv[3])`n"
    $tmp = Write-SSH-Script $body
    try { py -3 $tmp $CfgPath $LocalPath $RemotePath }
    finally { Remove-Item $tmp -Force -EA SilentlyContinue }
}

# Upload a directory tree to server via SFTP.
function Send-RemoteDir {
    param([string]$LocalDir, [string]$RemoteDir)
    $pyLines = @(
        "import paramiko, sys, os",
        "import json",
        "cfg = json.loads(open(sys.argv[1], encoding='utf-8').read())",
        "local_dir = sys.argv[2]",
        "remote_dir = sys.argv[3]",
        "c = paramiko.SSHClient()",
        "c.set_missing_host_key_policy(paramiko.AutoAddPolicy())",
        "c.connect(cfg['host'], username=cfg.get('user','root'), password=cfg['password'], timeout=30)",
        "sftp = c.open_sftp()",
        "def sftp_mkdir_p(sftp, path):",
        "    parts = [p for p in path.replace(chr(92),'/').split('/') if p]",
        "    cur = ''",
        "    for p in parts:",
        "        cur += '/' + p",
        "        try: sftp.stat(cur)",
        "        except: sftp.mkdir(cur)",
        "count = 0",
        "for root, dirs, files in os.walk(local_dir):",
        "    rel = os.path.relpath(root, local_dir)",
        "    rpath = remote_dir if rel == '.' else remote_dir + '/' + rel.replace(os.sep, '/')",
        "    sftp_mkdir_p(sftp, rpath)",
        "    for f in files:",
        "        sftp.put(os.path.join(root, f), rpath + '/' + f)",
        "        count += 1",
        "        if count % 20 == 0: print('  uploaded', count, 'files...')",
        "sftp.close(); c.close()",
        "print('Done:', count, 'files ->', remote_dir)"
    )
    $body = $pyLines -join "`n"
    $tmp = Write-SSH-Script $body
    try { py -3 $tmp $CfgPath $LocalDir $RemoteDir }
    finally { Remove-Item $tmp -Force -EA SilentlyContinue }
}

function Write-OpsLog {
    param([string]$Message)
    if (-not (Test-Path $BackupDir)) {
        New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
    }
    $entry = "[$(Get-Date -Format 'u')] $Message"
    $entry | Add-Content $OpsLog -Encoding UTF8
}

# --- Actions ---

switch ($Action) {

    "status" {
        Write-Host "`n=== Server Status ===" -ForegroundColor Cyan
        $cmd = "uptime && echo '---' && df -h / && echo '---' && free -h && echo '---' && " +
               "systemctl is-active tourkit-ai-proxy.service 2>/dev/null || echo 'service: unknown'"
        Invoke-SSH $cmd
    }

    "health" {
        Write-Host "`n=== Full Health Check ===" -ForegroundColor Cyan
        $cmd = "echo '=== SYSTEM ===' && uptime && free -h && df -h / && echo '' && " +
               "echo '=== POSTGRESQL ===' && systemctl is-active postgresql 2>/dev/null || echo 'NOT RUNNING' && " +
               "sudo -u postgres psql -d 'tourkit-chat' -c '\dt' 2>/dev/null | wc -l && " +
               "echo '' && echo '=== APP ===' && " +
               "systemctl status tourkit-ai-proxy.service --no-pager 2>/dev/null | head -10 || echo 'not found' && " +
               "echo '' && echo '=== ERRORS ===' && " +
               "journalctl -u tourkit-ai-proxy.service -n 10 --no-pager 2>/dev/null | grep -iE 'error|fail|exception' | tail -5 || echo 'none'"
        Invoke-SSH $cmd
    }

    "backup" {
        $ts = Get-Date -Format "yyyy-MM-dd-HHmm"
        if ($Tag) {
            $fileName = "chat-$ts-$Tag.dump"
        } else {
            $fileName = "chat-$ts.dump"
        }
        $remoteFile = "/tmp/$fileName"
        $localFile  = Join-Path $BackupDir $fileName

        if (-not (Test-Path $BackupDir)) {
            New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
        }

        Write-Host "`n=== Backup tourkit-chat ===" -ForegroundColor Cyan
        Write-Host "  Remote : $remoteFile"
        Write-Host "  Local  : $localFile"

        Write-Host "`n[1/4] pg_dump on server..." -ForegroundColor Yellow
        $dumpCmd = "sudo -u postgres pg_dump -d 'tourkit-chat' --format=custom --compress=9" +
                   " -f '$remoteFile' && echo DUMP_OK && stat -c '%s' '$remoteFile'"
        $out = Get-SSHOutput $dumpCmd
        Write-Host $out

        if ($out -notmatch "DUMP_OK") {
            Write-Error "pg_dump failed!"; exit 1
        }

        $sizeMatch = [regex]::Matches($out, '(?m)^\d+$')
        $sizeBytes = 0
        if ($sizeMatch.Count -gt 0) {
            $sizeBytes = [int]$sizeMatch[$sizeMatch.Count - 1].Value
        }
        if ($sizeBytes -lt 1024) {
            Write-Error "Dump file too small ($sizeBytes bytes) - possible error"; exit 1
        }

        Write-Host "`n[2/4] Downloading..." -ForegroundColor Yellow
        Get-RemoteFile $remoteFile $localFile

        Write-Host "`n[3/4] Cleanup remote temp..." -ForegroundColor Yellow
        $null = Get-SSHOutput "rm -f '$remoteFile' && echo CLEANED"

        Write-Host "`n[4/4] Verify..." -ForegroundColor Yellow
        $localSize = [math]::Round((Get-Item $localFile).Length / 1KB)
        Write-Host "  OK: $localFile ($localSize KB)" -ForegroundColor Green

        Write-OpsLog "backup OK: $fileName ($localSize KB)"
        Write-Host "`nBackup complete: $localFile" -ForegroundColor Green
    }

    "restore" {
        if (-not $BackupFile) {
            Write-Error "Use -BackupFile 'filename.dump'. Run -Action list-backups to see options."; exit 1
        }

        if ([System.IO.Path]::IsPathRooted($BackupFile)) {
            $localFile = $BackupFile
        } else {
            $localFile = Join-Path $BackupDir $BackupFile
        }

        if (-not (Test-Path $localFile)) {
            Write-Error "File not found: $localFile"; exit 1
        }

        $sizeKB = [math]::Round((Get-Item $localFile).Length / 1KB)
        Write-Host "`n=== Restore from $BackupFile ($sizeKB KB) ===" -ForegroundColor Yellow
        Write-Host "WARNING: This will ERASE all current data in tourkit-chat!" -ForegroundColor Red

        $confirm = Read-Host "Type 'yes' to confirm"
        if ($confirm -ne "yes") { Write-Host "Cancelled."; exit 0 }

        # Safety backup before restore
        Write-Host "`n[0/5] Creating safety backup before restore..." -ForegroundColor Yellow
        & $PSCommandPath -Action backup -Tag "pre-restore-safety"

        $remoteFile = "/tmp/restore-$(Split-Path $localFile -Leaf)"

        Write-Host "`n[1/5] Uploading backup file to server..." -ForegroundColor Yellow
        Send-RemoteFile $localFile $remoteFile

        Write-Host "`n[2/5] Stopping app..." -ForegroundColor Yellow
        Invoke-SSH "systemctl stop tourkit-ai-proxy.service 2>/dev/null; echo stopped"

        Write-Host "`n[3/5] Drop + recreate database..." -ForegroundColor Yellow
        $dropCmd = "sudo -u postgres psql -c " +
                   "`"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='tourkit-chat';`" " +
                   "&& sudo -u postgres dropdb 'tourkit-chat' " +
                   "&& sudo -u postgres createdb 'tourkit-chat' -O tourkit_chat -E UTF8 " +
                   "&& sudo -u postgres psql -d 'tourkit-chat' -c 'GRANT ALL ON SCHEMA public TO tourkit_chat;' " +
                   "&& echo DB_READY"
        $dbOut = Get-SSHOutput $dropCmd
        Write-Host $dbOut
        if ($dbOut -notmatch "DB_READY") {
            Write-Error "Could not recreate database!"; exit 1
        }

        Write-Host "`n[4/5] Restoring data..." -ForegroundColor Yellow
        $restoreCmd = "sudo -u postgres pg_restore --clean --no-acl --no-owner" +
                      " -d 'tourkit-chat' '$remoteFile' && echo RESTORE_OK && rm -f '$remoteFile'"
        Invoke-SSH $restoreCmd

        Write-Host "`n[5/5] Starting app..." -ForegroundColor Yellow
        Invoke-SSH "systemctl start tourkit-ai-proxy.service && sleep 3 && systemctl is-active tourkit-ai-proxy.service"

        Write-OpsLog "restore OK: from $BackupFile"
        Write-Host "`nRestore complete!" -ForegroundColor Green
    }

    "list-backups" {
        Write-Host "`n=== Available Backups ===" -ForegroundColor Cyan
        Write-Host "Directory: $BackupDir`n"

        if (-not (Test-Path $BackupDir)) {
            Write-Host "No backups found."; break
        }

        $dumps = @(Get-ChildItem $BackupDir -Filter "*.dump" | Sort-Object LastWriteTime -Descending)
        if ($dumps.Count -eq 0) {
            Write-Host "No .dump files found."
        } else {
            $dumps | ForEach-Object {
                $kb = [math]::Round($_.Length / 1KB)
                "{0,-55} {1,8} KB   {2}" -f $_.Name, $kb, $_.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
            }
        }

        Write-Host "`n--- Ops Log (last 10 lines) ---"
        if (Test-Path $OpsLog) {
            Get-Content $OpsLog -Tail 10
        } else {
            Write-Host "(no log yet)"
        }
    }

    "logs" {
        Write-Host "`n=== App Logs (last $Lines lines) ===" -ForegroundColor Cyan
        $cmd = "journalctl -u tourkit-ai-proxy.service -n $Lines --no-pager 2>/dev/null" +
               " || tail -$Lines /var/log/tourkit-ai-proxy.log 2>/dev/null || echo 'Log not found'"
        Invoke-SSH $cmd
    }

    "restart" {
        Write-Host "`n=== Restart Service ===" -ForegroundColor Cyan
        Invoke-SSH "systemctl restart tourkit-ai-proxy.service && sleep 3 && systemctl status tourkit-ai-proxy.service --no-pager | head -15"
        Write-OpsLog "restart service"
    }

    "deploy" {
        if (-not $LocalPublishPath -or -not (Test-Path $LocalPublishPath)) {
            Write-Error "Use -LocalPublishPath <publish-dir>. Run 'dotnet publish -c Release -o out/' first."
            exit 1
        }

        $cfg = Ensure-Config
        if ($cfg.deployPath) {
            $deployPath = $cfg.deployPath
        } else {
            $deployPath = "/var/www/tourkit-ai-proxy"
        }

        Write-Host "`n=== Deploy -> $deployPath ===" -ForegroundColor Cyan

        # Backup before deploy
        Write-Host "`n[0/4] Backup DB before deploy..." -ForegroundColor Yellow
        $hashRaw = git -C $RepoRoot rev-parse --short HEAD 2>/dev/null
        if ($hashRaw) { $hash = $hashRaw } else { $hash = "unknown" }
        & $PSCommandPath -Action backup -Tag "pre-deploy-$hash"

        Write-Host "`n[1/4] Stop service..." -ForegroundColor Yellow
        Invoke-SSH "systemctl stop tourkit-ai-proxy.service && echo STOPPED"

        Write-Host "`n[2/4] Upload files..." -ForegroundColor Yellow
        Send-RemoteDir $LocalPublishPath $deployPath

        Write-Host "`n[3/4] Set permissions + start..." -ForegroundColor Yellow
        Invoke-SSH "chmod +x '$deployPath/TourkitAiProxy' 2>/dev/null; systemctl start tourkit-ai-proxy.service && sleep 3 && systemctl status tourkit-ai-proxy.service --no-pager | head -10"

        Write-Host "`n[4/4] Check startup log..." -ForegroundColor Yellow
        Invoke-SSH "journalctl -u tourkit-ai-proxy.service -n 20 --no-pager"

        Write-OpsLog "deploy OK: $LocalPublishPath (commit $hash)"
        Write-Host "`nDeploy complete!" -ForegroundColor Green
    }

    "shell" {
        if (-not $Command) {
            Write-Error "Use -Command 'bash command'"; exit 1
        }
        Write-Host "`n=== Shell: $Command ===" -ForegroundColor Cyan
        Invoke-SSH $Command
    }
}

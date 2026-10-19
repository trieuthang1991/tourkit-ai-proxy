# scripts/server-manage.ps1
# Quản lý server Linux sản xuất qua SSH (Python + paramiko).
# Yêu cầu: Python 3 (py -3) + paramiko (pip install paramiko)
# Config: scripts/.server-config.json (gitignored)
#
# Dùng:
#   .\scripts\server-manage.ps1 -Action status
#   .\scripts\server-manage.ps1 -Action backup
#   .\scripts\server-manage.ps1 -Action logs -Lines 200
#   .\scripts\server-manage.ps1 -Action restart
#   .\scripts\server-manage.ps1 -Action deploy -LocalPublishPath "out/"

param(
    [ValidateSet("status","backup","logs","restart","shell","deploy")]
    [string]$Action = "status",

    [string]$Command = "",       # dùng với -Action shell
    [int]$Lines = 100,           # dùng với -Action logs
    [string]$LocalPublishPath = "" # dùng với -Action deploy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- Helpers ---

function Ensure-Config {
    $cfgPath = Join-Path $PSScriptRoot ".server-config.json"
    if (-not (Test-Path $cfgPath)) {
        $exPath = Join-Path $PSScriptRoot ".server-config.example.json"
        Write-Error "Thiếu $cfgPath — copy từ $exPath rồi điền thông tin thật."
        exit 1
    }
    return $cfgPath
}

function Run-SSH {
    param([string]$Cmd, [switch]$Print)

    $cfgPath = Ensure-Config
    $py = @"
import paramiko, json, pathlib, sys

cfg = json.loads(pathlib.Path(r'$cfgPath').read_text(encoding='utf-8'))
c = paramiko.SSHClient()
c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
c.connect(cfg['host'], username=cfg.get('user','root'), password=cfg['password'], timeout=30)

cmd = sys.argv[1]
_, stdout, stderr = c.exec_command(cmd, get_pty=False)
out = stdout.read().decode('utf-8', errors='replace')
err = stderr.read().decode('utf-8', errors='replace')
code = stdout.channel.recv_exit_status()
c.close()

print(out, end='')
if err.strip(): print('[STDERR]', err, file=sys.stderr)
sys.exit(code)
"@
    $tmpPy = [System.IO.Path]::GetTempFileName() + ".py"
    [System.IO.File]::WriteAllText($tmpPy, $py, [System.Text.Encoding]::UTF8)
    try {
        $out = py -3 $tmpPy $Cmd 2>&1
        if ($Print) { $out | Write-Host }
        return $out
    } finally {
        Remove-Item $tmpPy -Force -ErrorAction SilentlyContinue
    }
}

function Download-SFTP {
    param([string]$RemotePath, [string]$LocalPath)

    $cfgPath = Ensure-Config
    $py = @"
import paramiko, json, pathlib, sys

cfg = json.loads(pathlib.Path(r'$cfgPath').read_text(encoding='utf-8'))
c = paramiko.SSHClient()
c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
c.connect(cfg['host'], username=cfg.get('user','root'), password=cfg['password'], timeout=60)
sftp = c.open_sftp()
sftp.get(sys.argv[1], sys.argv[2])
sftp.close()
c.close()
print('Downloaded:', sys.argv[2])
"@
    $tmpPy = [System.IO.Path]::GetTempFileName() + ".py"
    [System.IO.File]::WriteAllText($tmpPy, $py, [System.Text.Encoding]::UTF8)
    try {
        py -3 $tmpPy $RemotePath $LocalPath
    } finally {
        Remove-Item $tmpPy -Force -ErrorAction SilentlyContinue
    }
}

# --- Actions ---

switch ($Action) {

    "status" {
        Write-Host "`n=== Server Status ===" -ForegroundColor Cyan
        Run-SSH "uptime && echo '---' && df -h / && echo '---' && free -h && echo '---' && systemctl is-active tourkit-ai-proxy.service 2>/dev/null || echo 'service: unknown'" -Print
    }

    "backup" {
        $ts = Get-Date -Format "yyyy-MM-dd-HHmm"
        $remoteFile = "/tmp/chat-$ts.dump"
        $cfgPath = Ensure-Config
        $cfg = Get-Content $cfgPath | ConvertFrom-Json
        $backupDir = if ($cfg.backupDir) { $cfg.backupDir } else { Join-Path $PSScriptRoot "..\backups" }

        if (-not (Test-Path $backupDir)) {
            New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        }

        $localFile = Join-Path $backupDir "chat-$ts.dump"

        Write-Host "`n=== Backup tourkit-chat ===" -ForegroundColor Cyan
        Write-Host "Remote: $remoteFile"
        Write-Host "Local : $localFile"

        Write-Host "`n[1/3] Chạy pg_dump trên server..." -ForegroundColor Yellow
        Run-SSH "sudo -u postgres pg_dump -d 'tourkit-chat' --format=custom --compress=9 -f '$remoteFile' && echo 'pg_dump OK' && ls -lh '$remoteFile'" -Print

        Write-Host "`n[2/3] Download file về máy local..." -ForegroundColor Yellow
        Download-SFTP -RemotePath $remoteFile -LocalPath $localFile

        Write-Host "`n[3/3] Xoá file tạm trên server..." -ForegroundColor Yellow
        Run-SSH "rm -f '$remoteFile' && echo 'Cleaned up'" -Print

        $size = (Get-Item $localFile).Length / 1KB
        Write-Host "`nBackup xong: $localFile ($([math]::Round($size))KB)" -ForegroundColor Green

        # Ghi log
        $logPath = Join-Path $backupDir "ops-log.txt"
        "[$(Get-Date -Format 'u')] backup OK: $localFile ($([math]::Round($size))KB)" | Add-Content $logPath
    }

    "logs" {
        Write-Host "`n=== App Logs (last $Lines lines) ===" -ForegroundColor Cyan
        Run-SSH "journalctl -u tourkit-ai-proxy.service -n $Lines --no-pager 2>/dev/null || tail -$Lines /var/log/tourkit-ai-proxy.log 2>/dev/null || echo 'Không tìm thấy log'" -Print
    }

    "restart" {
        Write-Host "`n=== Restart Service ===" -ForegroundColor Cyan
        Run-SSH "systemctl restart tourkit-ai-proxy.service && sleep 2 && systemctl status tourkit-ai-proxy.service --no-pager" -Print
    }

    "shell" {
        if (-not $Command) {
            Write-Error "Dùng với -Command 'lệnh bash cần chạy'"
            exit 1
        }
        Write-Host "`n=== Shell: $Command ===" -ForegroundColor Cyan
        Run-SSH $Command -Print
    }

    "deploy" {
        if (-not $LocalPublishPath -or -not (Test-Path $LocalPublishPath)) {
            Write-Error "Dùng với -LocalPublishPath 'đường dẫn thư mục publish' (chạy dotnet publish trước)"
            exit 1
        }
        Write-Error "Deploy chưa được cấu hình — cần biết đường dẫn deploy trên server. Sửa phần này trực tiếp trong script."
    }
}

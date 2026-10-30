param([ValidateRange(1024,65535)][int]$Port = 5081)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console.ps1')
trap { Write-StartupError $_; exit 1 }
try {
    # Only the classroom subnet can reach the teacher service.
    $ruleName = "RoomScope-Teacher-$Port"
    Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name $ruleName -DisplayName "声域教师端（端口 $Port）" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -RemoteAddress LocalSubnet -Profile Any | Out-Null
} catch { Write-StartupError $_; exit 1 }

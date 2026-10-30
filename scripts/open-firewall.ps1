param([ValidateRange(1024,65535)][int]$Port = 5081)
$ErrorActionPreference = 'Stop'
try {
    # Only the classroom subnet can reach the teacher service.
    $ruleName = "RoomScope-Teacher-$Port"
    Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name $ruleName -DisplayName "RoomScope Teacher ($Port)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -RemoteAddress LocalSubnet -Profile Any | Out-Null
} catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }

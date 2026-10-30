param(
    [Parameter(Mandatory=$true)][string]$TeacherServer,
    [string]$IdentityDirectory,
    [switch]$EnableLoginStartup
)
$ErrorActionPreference = 'Stop'
$agentPath = Join-Path $PSScriptRoot 'Noise.Agent.exe'
if (-not (Test-Path $agentPath)) { throw 'Run this script from the extracted student package.' }
if (Get-Process -Name Noise.Agent -ErrorAction SilentlyContinue) { throw 'Close all RoomScope student clients before preparing the image.' }
$serverUri = $null
if (-not [Uri]::TryCreate($TeacherServer, [UriKind]::Absolute, [ref]$serverUri) -or
    $serverUri.Scheme -ne 'https' -or $serverUri.AbsolutePath -ne '/' -or
    $serverUri.UserInfo -or $serverUri.Query -or $serverUri.Fragment) {
    throw 'TeacherServer must be an HTTPS root address, for example https://192.168.1.10:5081.'
}
if ($IdentityDirectory -and [Environment]::ExpandEnvironmentVariables($IdentityDirectory) -notmatch '^[A-Za-z]:[\\/]') {
    throw 'IdentityDirectory must be a fully qualified local path on a non-restored partition.'
}
function Clear-CurrentIdentity {
    $process = Start-Process -FilePath $agentPath -ArgumentList '--prepare-image' -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'Identity cleanup failed. Check file permissions and the existing deployment profile.' }
}
# Clean both the old configured location and the new target, for this Windows account.
Clear-CurrentIdentity
$deploymentProfile = @{ Server = $serverUri.GetLeftPart([UriPartial]::Authority); AutoEnroll = $true; IdentityDirectory = $IdentityDirectory; ManagedMode = $true; EnableLoginStartup = [bool]$EnableLoginStartup }
$deploymentProfile | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $PSScriptRoot 'agent-deployment.json')
Clear-CurrentIdentity
if ($EnableLoginStartup) {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name RoomScopeAgent -Value ('"' + $agentPath + '" --minimized') -PropertyType String -Force | Out-Null
}
Write-Host 'Image profile prepared. No device ID or credential is included.'
Write-Host ('Teacher service: ' + $deploymentProfile.Server)
Write-Host 'Do not start or enroll the master client again before capturing the image.'
Write-Host 'Ensure cloned computer names match real seats (PC01, PC02, ...), or review mappings on the teacher computer.'
Write-Host 'After deployment, open the teacher bulk enrollment window and approve the pending computers.'
Write-Host 'Login startup applies to this Windows account. Microphone capture remains off until locally allowed and the teacher starts an experiment.'

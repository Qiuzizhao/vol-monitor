# Exercise real ZIP generation with simulated Windows networking/certificate UI.
# This test never installs a certificate, changes firewall rules, or starts a service.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-test-' + [guid]::NewGuid().ToString('N'))
$previous = Get-Location
try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'data'),(Join-Path $fixture 'template') -Force | Out-Null
    Copy-Item (Join-Path $repo 'scripts/start-teacher.ps1') $fixture
    Set-Content (Join-Path $fixture 'Noise.Server.exe') 'placeholder'
    Set-Content (Join-Path $fixture 'roomscope-teacher.cer') 'public certificate fixture'
    Set-Content (Join-Path $fixture 'data/teacher.pfx') 'PRIVATE KEY - must never enter student ZIP'
    Set-Content (Join-Path $fixture 'data/https-password.dpapi') 'PRIVATE PASSWORD'
    Set-Content (Join-Path $fixture 'data/https.json') '{"ServerIp":"192.168.10.20","Port":5081}'
    Set-Content (Join-Path $fixture 'start-lan.ps1') '$global:teacherStarted = $true; $global:LASTEXITCODE = 0'
    Set-Content (Join-Path $fixture 'template/Noise.Agent.exe') 'student binary fixture'
    Copy-Item (Join-Path $repo 'scripts/start-student.cmd'),(Join-Path $repo 'scripts/start-student.ps1') (Join-Path $fixture 'template')
    Compress-Archive -Path (Join-Path $fixture 'template/*') -DestinationPath (Join-Path $fixture 'student-template.zip')
    function Get-NetIPConfiguration {
        [pscustomobject]@{ NetAdapter = @{ Status = 'Up' }; IPv4Address = @(@{ IPAddress = '192.168.10.20' }); InterfaceAlias = 'Classroom'; IPv4DefaultGateway = '192.168.10.1' }
        [pscustomobject]@{ NetAdapter = @{ Status = 'Up' }; IPv4Address = @(@{ IPAddress = '10.0.0.2' }); InterfaceAlias = 'Other'; IPv4DefaultGateway = $null }
    }
    function Get-NetFirewallRule { param($Name, $ErrorAction) [pscustomobject]@{ Enabled = 'True' } }
    function Test-Path {
        param([string]$Path, [string]$LiteralPath, [string]$PathType)
        if ($Path -like 'Cert:*') { return $true }
        Microsoft.PowerShell.Management\Test-Path @PSBoundParameters
    }
    function New-Object {
        param([string]$TypeName, [object[]]$ArgumentList)
        if ($TypeName -like '*X509Certificate2*') { [pscustomobject]@{ Thumbprint = 'TEST'; NotAfter = (Get-Date).AddMonths(6) } }
        else { Microsoft.PowerShell.Utility\New-Object -TypeName $TypeName -ArgumentList $ArgumentList }
    }
    function Start-Job { param($ArgumentList, $ScriptBlock) return $null }
    & (Join-Path $fixture 'start-teacher.ps1')
    if (-not $global:teacherStarted) { throw 'Teacher service was not started.' }
    $output = Join-Path $fixture 'output'
    Expand-Archive -LiteralPath (Join-Path $fixture 'RoomScope-classroom-student.zip') -DestinationPath $output
    $profile = Get-Content (Join-Path $output 'agent-deployment.json') -Raw | ConvertFrom-Json
    if ($profile.Server -ne 'https://192.168.10.20:5081' -or -not $profile.AutoEnroll) { throw 'Wrong student classroom profile.' }
    $files = @(Get-ChildItem $output -Recurse -File | ForEach-Object { $_.Name })
    if ($files.Count -ne 5 -or 'roomscope-teacher.cer' -notin $files -or 'start-student.cmd' -notin $files -or 'start-student.ps1' -notin $files) { throw 'Unexpected student package contents.' }
    Write-Host 'PASS: existing network reused, student ZIP generated, public certificate included, teacher secrets excluded, teacher started.'
} finally {
    Set-Location $previous
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'RoomScope-test-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

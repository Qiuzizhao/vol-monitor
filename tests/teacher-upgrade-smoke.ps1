# Run the real takeover flow with simulated processes and registry access.
# No actual teacher/student process, startup entry, certificate or firewall is changed.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-upgrade-test-' + [guid]::NewGuid().ToString('N'))
$old = Join-Path $fixture '旧版 教师/程序'
$current = Join-Path $fixture '新版 教师/程序'
$global:upgradeFixtureState = @{ Stopped=@(); RemovedStartup=$false; SimulateCopyFailure=$false; Processes=@(); Startup=$null }
$global:upgradeFixtureState.RemovedStartup = $false
$global:upgradeFixtureState.SimulateCopyFailure = $false
try {
    New-Item -ItemType Directory -Path (Join-Path $old 'data'),$current -Force | Out-Null
    foreach ($directory in @($old, $current)) { Set-Content (Join-Path $directory 'start-teacher.ps1') 'RoomScopeTeacher' }
    foreach ($leaf in @('roomscope.db','roomscope.db-wal','teacher-auth.json','teacher.pfx','https-password.dpapi','https.json')) {
        Set-Content (Join-Path $old ('data/' + $leaf)) ('fixture-' + $leaf)
    }
    Set-Content (Join-Path $old 'roomscope-teacher.cer') 'public fixture'
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $global:upgradeFixtureState.Processes = @(
        [pscustomobject]@{ ProcessId=$PID; ParentProcessId=900001; SessionId=$session; Name='powershell.exe'; CommandLine=('powershell.exe -File "' + (Join-Path $current 'startup-worker.ps1') + '" -Target teacher') },
        [pscustomobject]@{ ProcessId=900001; ParentProcessId=0; SessionId=$session; Name='powershell.exe'; CommandLine=('powershell.exe -File "' + (Join-Path $current 'launch-ui.ps1') + '" -Target teacher') },
        [pscustomobject]@{ ProcessId=900002; ParentProcessId=0; SessionId=$session; Name='powershell.exe'; CommandLine=('powershell.exe -File "' + (Join-Path $old 'launch-ui.ps1') + '" -Target teacher') },
        [pscustomobject]@{ ProcessId=900003; ParentProcessId=900002; SessionId=$session; Name='powershell.exe'; CommandLine=('powershell.exe -File "' + (Join-Path $old 'start-teacher.ps1') + '"') },
        [pscustomobject]@{ ProcessId=900004; ParentProcessId=900003; SessionId=$session; Name='Noise.Server.exe'; ExecutablePath=(Join-Path $old 'Noise.Server.exe') },
        [pscustomobject]@{ ProcessId=900005; ParentProcessId=0; SessionId=$session; Name='powershell.exe'; CommandLine=('powershell.exe -File "' + (Join-Path $old 'launch-ui.ps1') + '" -Target student') },
        [pscustomobject]@{ ProcessId=900006; ParentProcessId=0; SessionId=$session; Name='Noise.Agent.exe'; ExecutablePath=(Join-Path $old 'Noise.Agent.exe') },
        [pscustomobject]@{ ProcessId=900007; ParentProcessId=0; SessionId=$session; Name='powershell.exe'; CommandLine='powershell.exe -Command Get-Date' }
    )
    $global:upgradeFixtureState.Startup = [pscustomobject]@{ RoomScopeTeacher=('powershell.exe -File "' + (Join-Path $old 'launch-ui.ps1') + '" -Target teacher -AtLogin') }
    function Get-CimInstance { param($ClassName) $global:upgradeFixtureState.Processes }
    function Get-ItemProperty { param($LiteralPath, $Name, $ErrorAction) $global:upgradeFixtureState.Startup }
    function Remove-ItemProperty { param($LiteralPath, $Name) $global:upgradeFixtureState.RemovedStartup = $true; $global:upgradeFixtureState.Startup = $null }
    function Get-Process {
        param($Id, $ErrorAction)
        if ($Id -notin @($global:upgradeFixtureState.Processes.ProcessId)) { return $null }
        $process = [pscustomobject]@{ Handle=0 }
        $process | Add-Member ScriptMethod WaitForExit { param($Timeout) return $true }
        return $process
    }
    function Stop-Process {
        param($Id, $ErrorAction)
        $global:upgradeFixtureState.Stopped += $Id
        $global:upgradeFixtureState.Processes = @($global:upgradeFixtureState.Processes | Where-Object ProcessId -ne $Id)
    }
    function Invoke-RestMethod { param($Uri, $TimeoutSec) [pscustomobject]@{ status='ok' } }
    function Copy-Item {
        param($LiteralPath, $Destination, [switch]$Recurse, [switch]$Force)
        if ($global:upgradeFixtureState.SimulateCopyFailure) { throw '模拟复制失败' }
        Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    }
    $takeover = Join-Path $repo 'scripts/stop-teacher.ps1'
    # A failed migration must leave a usable source for automatic retry.
    $global:upgradeFixtureState.SimulateCopyFailure = $true
    $failed = $false
    try { & $takeover -ForStartup -CurrentDirectory $current } catch { $failed = $true }
    if (-not $failed -or -not (Test-Path (Join-Path $current 'previous-teacher.txt'))) { throw 'Migration failure did not retain the source.' }
    if (Test-Path (Join-Path $current 'data')) { throw 'Partial migration became visible as complete data.' }
    $global:upgradeFixtureState.SimulateCopyFailure = $false
    & $takeover -ForStartup -CurrentDirectory $current
    if (($global:upgradeFixtureState.Stopped -join ',') -ne '900002,900003,900004') { throw 'Wrong processes or shutdown order.' }
    if (-not $global:upgradeFixtureState.RemovedStartup) { throw 'Old startup entry was not replaced.' }
    foreach ($leaf in @('roomscope.db','roomscope.db-wal','teacher-auth.json','teacher.pfx','https-password.dpapi','https.json')) {
        if ((Get-FileHash (Join-Path $old ('data/' + $leaf))).Hash -ne (Get-FileHash (Join-Path $current ('data/' + $leaf))).Hash) { throw 'Migrated data changed.' }
    }
    if (-not (Test-Path (Join-Path $current 'roomscope-teacher.cer'))) { throw 'Public certificate was not retained.' }
    if (Test-Path (Join-Path $current 'previous-teacher.txt')) { throw 'Completed migration left a pending source.' }
    # Existing destination settings must win over older settings.
    Set-Content (Join-Path $current 'data/teacher-auth.json') 'current fixture'
    $global:upgradeFixtureState.Startup = [pscustomobject]@{ RoomScopeTeacher=('powershell.exe -File "' + (Join-Path $old 'launch-ui.ps1') + '" -Target teacher') }
    & $takeover -ForStartup -CurrentDirectory $current
    if ((Get-Content (Join-Path $current 'data/teacher-auth.json')) -ne 'current fixture') { throw 'Existing settings were overwritten.' }
    # Reopening the same healthy installation must never stop it.
    $global:upgradeFixtureState.Stopped = @()
    $global:upgradeFixtureState.Processes += [pscustomobject]@{ ProcessId=900008; ParentProcessId=0; SessionId=$session; Name='Noise.Server.exe'; ExecutablePath=(Join-Path $current 'Noise.Server.exe') }
    & $takeover -ForStartup -CurrentDirectory $current
    if ($global:upgradeFixtureState.Stopped.Count) { throw 'Reopening the running version stopped a process.' }
    Write-Host '通过：自动关闭旧教师进程，保留新版启动器和学生端；迁移数据、密码和证书；复制失败可重试，已有配置不覆盖，重复启动不重启。'
} finally {
    Remove-Variable -Name upgradeFixtureState -Scope Global -ErrorAction SilentlyContinue
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'RoomScope-upgrade-test-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

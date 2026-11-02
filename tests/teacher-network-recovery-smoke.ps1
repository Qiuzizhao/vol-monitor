$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-network-test-'+[guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    Copy-Item (Join-Path $repo 'scripts/start-teacher.ps1'),(Join-Path $repo 'scripts/stop-teacher.ps1'),(Join-Path $repo 'scripts/console.ps1') $fixture
    Set-Content (Join-Path $fixture 'Noise.Server.exe') 'fixture'
    $run=@'
$ErrorActionPreference='Stop'
function Get-CimInstance { param($ClassName) @() }
function Get-ItemProperty { param($LiteralPath,$Name,$ErrorAction) [pscustomobject]@{RoomScopeTeacher=('powershell.exe -File "'+(Join-Path $PSScriptRoot 'launch-ui.ps1')+'" -Target teacher -AtLogin')} }
function Remove-ItemProperty { param($LiteralPath,$Name) Set-Content (Join-Path $PSScriptRoot 'removed.txt') 'removed' }
function Get-NetIPConfiguration { @() }
& (Join-Path $PSScriptRoot 'start-teacher.ps1') -AtLogin -FromLauncher
exit $LASTEXITCODE
'@
    [IO.File]::WriteAllText((Join-Path $fixture 'run.ps1'),$run,[Text.UTF8Encoding]::new($true))
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'run.ps1')
    if ($LASTEXITCODE -ne 1 -or (Test-Path (Join-Path $fixture 'removed.txt'))) { throw 'Network failure must retain the original startup entry.' }
    Write-Host '通过：网络未就绪时启动失败但保留自启；未修改真实注册表、网络或进程。'
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    if ($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'RoomScope-network-test-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

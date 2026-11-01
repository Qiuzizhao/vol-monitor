# Exercise teacher startup with simulated Windows networking/certificate UI.
# This test never installs a certificate, changes firewall rules, or starts a service.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-test-' + [guid]::NewGuid().ToString('N'))
$teacherDirectory = Join-Path $fixture '教师/程序'
$previous = Get-Location
try {
    New-Item -ItemType Directory -Path (Join-Path $teacherDirectory 'data'),(Join-Path $fixture 'template/程序') -Force | Out-Null
    Copy-Item (Join-Path $repo 'scripts/start-teacher.ps1') $teacherDirectory
    Copy-Item (Join-Path $repo 'scripts/console.ps1') $teacherDirectory
    Set-Content (Join-Path $teacherDirectory 'Noise.Server.exe') 'placeholder'
    Set-Content (Join-Path $teacherDirectory 'roomscope-teacher.cer') 'public certificate fixture'
    Set-Content (Join-Path $teacherDirectory 'data/teacher.pfx') 'PRIVATE KEY - must never enter student ZIP'
    Set-Content (Join-Path $teacherDirectory 'data/https-password.dpapi') 'PRIVATE PASSWORD'
    Set-Content (Join-Path $teacherDirectory 'data/https.json') '{"ServerIp":"192.168.10.20","Port":5081}'
    Set-Content (Join-Path $teacherDirectory 'start-lan.ps1') '$global:teacherStarted = $true; $global:LASTEXITCODE = 0'
    Set-Content (Join-Path $fixture 'template/程序/Noise.Agent.exe') 'student binary fixture'
    Copy-Item (Join-Path $repo 'scripts/start-student.ps1') (Join-Path $fixture 'template/程序')
    Copy-Item (Join-Path $repo 'scripts/console.ps1') (Join-Path $fixture 'template/程序')
    Copy-Item (Join-Path $repo 'scripts/launch-ui.ps1') (Join-Path $fixture 'template/程序')
    Copy-Item (Join-Path $repo 'scripts/start-student.cmd') (Join-Path $fixture 'template/启动学生端.cmd')
    Compress-Archive -Path (Join-Path $fixture 'template/*') -DestinationPath (Join-Path $teacherDirectory 'student-template.zip')
    function Get-NetIPConfiguration {
        [pscustomobject]@{ NetAdapter = @{ Status = 'Up' }; IPv4Address = @(@{ IPAddress = '192.168.10.20' }); InterfaceAlias = 'Classroom'; IPv4DefaultGateway = '192.168.10.1' }
        [pscustomobject]@{ NetAdapter = @{ Status = 'Up' }; IPv4Address = @(@{ IPAddress = '10.0.0.2' }); InterfaceAlias = 'Other'; IPv4DefaultGateway = $null }
    }
    function Get-NetFirewallRule { param($Name, $ErrorAction) [pscustomobject]@{ Enabled = 'True' } }
    function Test-Path {
        param([string]$Path, [string]$LiteralPath, [Alias('Type')][string]$PathType)
        if ($Path -like 'Cert:*') { return $true }
        Microsoft.PowerShell.Management\Test-Path @PSBoundParameters
    }
    function New-Object {
        param([string]$TypeName, [object[]]$ArgumentList)
        if ($TypeName -like '*X509Certificate2*') { [pscustomobject]@{ Thumbprint = 'TEST'; NotAfter = (Get-Date).AddMonths(6) } }
        else { Microsoft.PowerShell.Utility\New-Object -TypeName $TypeName -ArgumentList $ArgumentList }
    }
    function Start-Job {
        param($ArgumentList, $ScriptBlock)
        if ($ArgumentList[0] -ne 'http://127.0.0.1:5080') { throw 'Teacher browser must open the local passwordless dashboard.' }
        return $null
    }
    function New-Item {
        param($Path, [Alias('Type')]$ItemType, [switch]$Force, $ErrorAction)
        if ($Path -like 'HKCU:*') { return }
        Microsoft.PowerShell.Management\New-Item @PSBoundParameters
    }
    function New-ItemProperty {
        param($Path, $Name, $Value, $PropertyType, [switch]$Force)
        if ($Path -notlike 'HKCU:*' -or $Name -ne 'RoomScopeTeacher' -or $Value -notlike '*-WindowStyle Hidden*-AtLogin') { throw 'Invalid teacher login startup registration.' }
        $global:teacherStartupRegistered = $true
    }
    & (Join-Path $teacherDirectory 'start-teacher.ps1')
    if (-not $global:teacherStarted) { throw 'Teacher service was not started.' }
    if (-not $global:teacherStartupRegistered) { throw 'Teacher login startup was not registered.' }
    if (Test-Path (Join-Path (Split-Path $teacherDirectory -Parent) '学生端安装包.zip')) { throw '启动时不应绕过页面设置密码自动生成学生端。' }
    if (-not (Test-Path (Join-Path $teacherDirectory 'student-template.zip'))) { throw '学生端模板丢失。' }
    Write-Host '通过：沿用机房网络，登记登录自启，启动教师服务；学生端由教师页面设置密码后生成。'
} finally {
    Set-Location $previous
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'RoomScope-test-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

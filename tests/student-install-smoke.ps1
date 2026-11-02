# Runs the real installer against a temporary directory with all system mutations mocked.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-install-test-'+[guid]::NewGuid().ToString('N'))
$source=Join-Path $fixture '安装包 程序'
$base=Join-Path $fixture 'Program Files/RoomScope'
$state=Join-Path $fixture 'ProgramData/Guard'
$global:installFixture=@{ Registered=$false; Running=$false; Sc=@(); Stopped=@(); FailNext=$false; Legacy=$true; State=$state }
try {
    New-Item -ItemType Directory -Force -Path $source,(Join-Path $base 'Student'),$state | Out-Null
    Set-Content (Join-Path $source 'Noise.Agent.exe') 'new binary'
    Set-Content (Join-Path $source 'roomscope-teacher.cer') 'public fixture'
    Set-Content (Join-Path $source 'roomscope-root.cer') 'root fixture'
    Set-Content (Join-Path $source 'agent-deployment.json') '{"Server":"https://192.168.1.20:5081","ManagedMode":true}'
    Set-Content (Join-Path $base 'Student/Noise.Agent.exe') 'old binary'
    Set-Content (Join-Path $base 'Student/agent-deployment.json') '{"Server":"https://192.168.1.10:5081"}'
    function Set-Acl { param($LiteralPath,$AclObject) if(-not $AclObject.AreAccessRulesProtected){throw 'Installation ACL must not inherit writable user permissions.'} }
    function Import-Certificate { param($FilePath,$CertStoreLocation) if($CertStoreLocation -ne 'Cert:\LocalMachine\Root'){throw 'Service trust must be machine-wide.'} }
    function Get-Service {
        param($Name,$ErrorAction)
        if(-not $global:installFixture.Registered){return $null}
        $service=[pscustomobject]@{ Status=if($global:installFixture.Running){'Running'}else{'Stopped'} }
        $service | Add-Member ScriptMethod WaitForStatus { param($Status,$Timeout) }
        return $service
    }
    function Stop-Service { param($Name,[switch]$Force,$ErrorAction) $global:installFixture.Running=$false }
    function Start-Service {
        param($Name,$ErrorAction)
        if($global:installFixture.FailNext){$global:installFixture.FailNext=$false;throw '模拟服务启动失败'}
        $global:installFixture.Running=$true
        Set-Content (Join-Path $global:installFixture.State 'guard-ready.json') '{"ProcessId":777003}'
    }
    function New-Service { param($Name,$BinaryPathName,$DisplayName,$StartupType) if($Name -ne 'RoomScopeStudentGuard' -or $StartupType -ne 'Automatic' -or $BinaryPathName -notlike '*" --guard-service'){throw 'Invalid automatic service registration'}; $global:installFixture.Registered=$true }
    function Invoke-CimMethod {param($InputObject,$MethodName,$Arguments) if($MethodName -ne 'Change' -or $Arguments.PathName -notlike '*" --guard-service' -or $Arguments.StartMode -ne 'Automatic'){throw 'Invalid service path update'}; [pscustomobject]@{ReturnValue=0} }
    function sc.exe { param([Parameter(ValueFromRemainingArguments=$true)]$Arguments) $global:installFixture.Sc+=($Arguments -join ' '); $global:LASTEXITCODE=0 }
    function Get-CimInstance {
        param($ClassName,$Filter)
        if($ClassName -eq 'Win32_Service'){return [pscustomobject]@{ProcessId=777003}}
        if($global:installFixture.Legacy){return [pscustomobject]@{ProcessId=777002; ExecutablePath=(Join-Path $source 'Noise.Agent.exe')} }
        return @()
    }
    function Get-Process { param($Id,$ErrorAction) $p=[pscustomobject]@{Handle=0}; $p | Add-Member ScriptMethod WaitForExit {param($Timeout) return $true}; return $p }
    function Stop-Process {param($Id) $global:installFixture.Stopped+=$Id; $global:installFixture.Legacy=$false}
    function Get-ItemProperty {param($LiteralPath,$Name,$ErrorAction) return $null}
    function Remove-ItemProperty {throw 'Unexpected real startup mutation'}
    $installer=Join-Path $repo 'scripts/install-student.ps1'
    $result=Join-Path $fixture 'result.txt'
    & $installer -SourceDirectory $source -BaseDirectory $base -StateDirectory $state -ResultPath $result
    if((Get-Content $result) -ne 'OK'){throw 'Upgrade failed'}
    if((Get-Content (Join-Path $base 'Student/Noise.Agent.exe')) -ne 'new binary'){throw 'Old version was not replaced'}
    $profile=Get-Content (Join-Path $base 'Student/agent-deployment.json') -Raw | ConvertFrom-Json
    if(-not $profile.SystemGuard -or $profile.EnableLoginStartup -or $profile.Server -ne 'https://192.168.1.20:5081'){throw 'Invalid protected installation profile'}
    if(($global:installFixture.Stopped -join ',') -ne '777002'){throw 'Wrong legacy process selected'}
    if(-not ($global:installFixture.Sc -match '^failure ') -or -not ($global:installFixture.Sc -match '^sdset ')){throw 'Missing service recovery or access protection'}
    # A failed replacement restores the last working installation and restarts its service.
    Set-Content (Join-Path $source 'Noise.Agent.exe') 'broken replacement'
    $global:installFixture.FailNext=$true
    & $installer -SourceDirectory $source -BaseDirectory $base -StateDirectory $state -ResultPath $result
    if((Get-Content $result) -eq 'OK' -or (Get-Content (Join-Path $base 'Student/Noise.Agent.exe')) -ne 'new binary' -or -not $global:installFixture.Running){throw 'Failed upgrade did not roll back'}
    $global:LASTEXITCODE=0
    Write-Host '通过：升级接管旧学生端，安装受保护的系统守护与恢复策略，更新连接地址；失败恢复旧程序。未改动真实服务、进程、证书或自启。'
} finally {
    Remove-Variable -Name installFixture -Scope Global -ErrorAction SilentlyContinue
    $resolved=[IO.Path]::GetFullPath($fixture)
    if($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'RoomScope-install-test-*'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}

param([Parameter(Mandatory=$true)][string]$SourceDirectory, [Parameter(Mandatory=$true)][string]$ResultPath, [string]$BaseDirectory = (Join-Path $env:ProgramFiles 'RoomScope'), [string]$StateDirectory = (Join-Path $env:ProgramData 'RoomScope/Guard'))
$ErrorActionPreference = 'Stop'
try {
    $source = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
    $profile = Get-Content -LiteralPath (Join-Path $source 'agent-deployment.json') -Raw | ConvertFrom-Json
    $server = [Uri]$profile.Server
    if ($server.Scheme -ne 'https' -or $server.AbsolutePath -ne '/' -or $server.UserInfo -or $server.Query -or $server.Fragment) { throw '教师连接地址无效。' }
    foreach ($leaf in @('Noise.Agent.exe','roomscope-teacher.cer')) { if (-not (Test-Path -LiteralPath (Join-Path $source $leaf))) { throw ('缺少安装文件：' + $leaf) } }
    $base = [IO.Path]::GetFullPath($BaseDirectory).TrimEnd('\')
    $target = Join-Path $base 'Student'
    if ($source -ieq $target) { throw '请从新解压的学生端安装包执行安装。' }
    New-Item -ItemType Directory -Force -Path $base | Out-Null
    $staging = Join-Path $base ('Student-install-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -notin @('agent.json','enrollment.json','initial-password.txt') } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $staging }
    @{ Server=$server.GetLeftPart([UriPartial]::Authority); AutoEnroll=$true; ManagedMode=$true; EnableLoginStartup=$false; SystemGuard=$true; IdentityDirectory=$profile.IdentityDirectory } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'agent-deployment.json') -Encoding UTF8
    # Executables may be read/run by students; only SYSTEM and administrators can replace them.
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl','ContainerInherit,ObjectInherit','None','Allow'))) }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')), 'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
    Set-Acl -LiteralPath $base -AclObject $acl
    Set-Acl -LiteralPath $staging -AclObject $acl
    $state = $StateDirectory
    New-Item -ItemType Directory -Force -Path $state | Out-Null
    $stateAcl = New-Object Security.AccessControl.DirectorySecurity
    $stateAcl.SetAccessRuleProtection($true,$false)
    $stateAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) { $stateAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl','ContainerInherit,ObjectInherit','None','Allow'))) }
    Set-Acl -LiteralPath (Split-Path $state -Parent) -AclObject $stateAcl
    Set-Acl -LiteralPath $state -AclObject $stateAcl
    foreach ($leaf in @('roomscope-teacher.cer','roomscope-root.cer')) {
        $certificate = Join-Path $staging $leaf
        if (Test-Path -LiteralPath $certificate) { Import-Certificate -FilePath $certificate -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null }
    }
    $service = Get-Service -Name RoomScopeStudentGuard -ErrorAction SilentlyContinue
    if ($service) { Stop-Service -Name RoomScopeStudentGuard -Force; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15)) }
    # Only this product's legacy clients are selected, never arbitrary PowerShell processes.
    foreach ($process in Get-CimInstance Win32_Process -Filter "Name='Noise.Agent.exe'") {
        if ($process.ExecutablePath -and (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($process.ExecutablePath)) 'agent-deployment.json'))) {
            $running = Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
            if ($running) { $null=$running.Handle; Stop-Process -Id $process.ProcessId; if (-not $running.WaitForExit(10000)) { throw '旧学生端尚未退出，正在等待升级。' } }
        }
    }
    $backup = $null
    if (Test-Path -LiteralPath $target) {
        $backup = Join-Path $base ('Student-previous-' + [guid]::NewGuid().ToString('N'))
        # Both resolved paths must remain under the fixed installation root.
        if (-not ([IO.Path]::GetFullPath($target).StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFullPath($backup).StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase))) { throw '安装目录校验失败。' }
        Move-Item -LiteralPath $target -Destination $backup
    }
    try {
        Move-Item -LiteralPath $staging -Destination $target
        $command = '"' + (Join-Path $target 'Noise.Agent.exe') + '" --guard-service'
        if (-not $service) { New-Service -Name RoomScopeStudentGuard -BinaryPathName $command -DisplayName '机房音量管理学生端守护' -StartupType Automatic | Out-Null }
        else {
            $registeredService=Get-CimInstance Win32_Service -Filter "Name='RoomScopeStudentGuard'"
            $changed=Invoke-CimMethod -InputObject $registeredService -MethodName Change -Arguments @{PathName=$command;StartMode='Automatic'}
            if ($changed.ReturnValue -ne 0) { throw '更新守护服务失败。' }
        }
        & sc.exe failure RoomScopeStudentGuard reset= 86400 actions= restart/2000/restart/5000/restart/10000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw '设置守护恢复失败。' }
        & sc.exe failureflag RoomScopeStudentGuard 1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw '设置守护恢复失败。' }
        & sc.exe sdset RoomScopeStudentGuard 'D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;AU)' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw '设置守护权限失败。' }
        $serviceStartedAt=[DateTime]::UtcNow.AddSeconds(-2)
        Start-Service -Name RoomScopeStudentGuard
        (Get-Service RoomScopeStudentGuard).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
        $ready = $false
        for ($attempt=0; $attempt -lt 30; $attempt++) {
            $hostProcess = Get-CimInstance Win32_Service -Filter "Name='RoomScopeStudentGuard'"
            $marker = Join-Path $state 'guard-ready.json'
            if ($hostProcess -and (Test-Path -LiteralPath $marker)) {
                $startup = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
                if ($hostProcess.ProcessId -ne 0 -and $startup.ProcessId -eq $hostProcess.ProcessId -and (Get-Item -LiteralPath $marker).LastWriteTimeUtc -ge $serviceStartedAt) { $ready=$true; break }
            }
            Start-Sleep -Seconds 1
        }
        if (-not $ready) { throw ('系统守护未就绪，请查看 ' + (Join-Path $state '运行错误.log')) }
    } catch {
        if ($backup -and (Test-Path -LiteralPath $backup)) {
            Stop-Service RoomScopeStudentGuard -Force -ErrorAction SilentlyContinue
            $failed = Join-Path $base ('Student-failed-' + [guid]::NewGuid().ToString('N'))
            if (-not [IO.Path]::GetFullPath($failed).StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)) { throw }
            if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $failed }
            Move-Item -LiteralPath $backup -Destination $target
            Start-Service RoomScopeStudentGuard -ErrorAction SilentlyContinue
        }
        throw
    }
    # Remove loaded accounts' obsolete per-user launch entries. The service now handles login.
    foreach ($user in Get-ChildItem Registry::HKEY_USERS) {
        $key = 'Registry::' + $user.Name + '\Software\Microsoft\Windows\CurrentVersion\Run'
        $entry = Get-ItemProperty -LiteralPath $key -Name RoomScopeAgent -ErrorAction SilentlyContinue
        if ($entry -and $entry.RoomScopeAgent -match '(?i)Noise\.Agent\.exe') { Remove-ItemProperty -LiteralPath $key -Name RoomScopeAgent -ErrorAction SilentlyContinue }
    }
    [IO.File]::WriteAllText($ResultPath, 'OK', [Text.UTF8Encoding]::new($true))
} catch {
    if ($service -and $target -and (Test-Path -LiteralPath (Join-Path $target 'Noise.Agent.exe'))) { Start-Service RoomScopeStudentGuard -ErrorAction SilentlyContinue }
    [IO.File]::WriteAllText($ResultPath, ('学生端安装未完成：' + $_.Exception.Message), [Text.UTF8Encoding]::new($true))
    exit 1
}

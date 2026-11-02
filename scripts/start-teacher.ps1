param([string]$ServerIp, [ValidateRange(1024,65535)][int]$Port = 5081, [switch]$AtLogin, [switch]$FromLauncher)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console.ps1')
trap { Write-StartupError $_; exit 1 }
Set-Location $PSScriptRoot
$browserJob = $null
$instance = $null
$ownsInstance = $false
$startupExitCode = 1
try {
    if (-not (Test-Path './Noise.Server.exe')) { throw '请先完整解压教师端运行包，再双击启动入口。' }
    $settingsPath = Join-Path $PSScriptRoot 'data/https.json'
    $created = $false
    $instance = [Threading.Mutex]::new($true, ('Local\RoomScope.Teacher.' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value), [ref]$created)
    $ownsInstance = $created
    if (-not $created) {
        try { $ownsInstance = $instance.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsInstance = $true }
    }
    if (-not $ownsInstance) {
        $ready = $false
        try {
            $health = Invoke-RestMethod 'http://127.0.0.1:5080/api/health' -TimeoutSec 2
            $ready = $health.status -eq 'ok'
        } catch { }
        if (-not $ready) { throw '旧版教师启动进程仍在运行，但服务没有就绪。请先运行「关闭旧版教师端.cmd」，再启动新版。' }
        Write-Host '教师服务已在运行，正在打开页面。'
        if (-not $AtLogin -and -not $FromLauncher -and (Test-Path $settingsPath)) {
            $current = Get-Content $settingsPath -Raw | ConvertFrom-Json
            Start-Process 'http://127.0.0.1:5080'
        }
        return
    }
    $addresses = @(Get-NetIPConfiguration | Where-Object { $_.NetAdapter.Status -eq 'Up' } |
        ForEach-Object { foreach ($ip in $_.IPv4Address) {
            if ($ip.IPAddress -and $ip.IPAddress -notlike '169.254.*' -and $ip.IPAddress -ne '127.0.0.1') {
                [pscustomobject]@{ Address = $ip.IPAddress; Adapter = $_.InterfaceAlias; Gateway = [bool]$_.IPv4DefaultGateway }
            }
        } } | Sort-Object -Property @{Expression='Gateway';Descending=$true},Address -Unique)
    $settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { $null }
    $activeIps = @($addresses | ForEach-Object { $_.Address })
    if (-not $ServerIp -and $settings -and $settings.ServerIp -in $activeIps) { $ServerIp = $settings.ServerIp; $Port = [int]$settings.Port }
    if (-not $ServerIp) {
        if ($addresses.Count -eq 0) { throw '请先将教师机连接到机房网络，再重新启动。' }
        if ($addresses.Count -eq 1) { $ServerIp = $addresses[0].Address }
        else {
            if ($FromLauncher) { $ServerIp = Select-ClassroomNetwork $addresses }
            else {
            Write-Host '请选择机房使用的网络：'
            for ($i = 0; $i -lt $addresses.Count; $i++) { Write-Host "[$($i + 1)] $($addresses[$i].Adapter) - $($addresses[$i].Address)" }
            $choice = 0
            if (-not [int]::TryParse((Read-Host '请输入网络编号'), [ref]$choice) -or $choice -lt 1 -or $choice -gt $addresses.Count) { throw '网络编号无效，请重新启动并选择列表中的编号。' }
            $ServerIp = $addresses[$choice - 1].Address
            }
        }
    }
    if ($ServerIp -notin $activeIps) { throw '所选地址不是本机当前可用的网络地址，请检查网卡连接。' }
    $certPath = Join-Path $PSScriptRoot 'roomscope-teacher.cer'
    $renew = -not $settings -or $settings.ServerIp -ne $ServerIp -or $settings.Port -ne $Port -or -not (Test-Path $certPath) -or
        -not (Test-Path (Join-Path $PSScriptRoot 'data/teacher.pfx')) -or -not (Test-Path (Join-Path $PSScriptRoot 'data/https-password.dpapi'))
    if (-not $renew) {
        $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certPath)
        $renew = $certificate.NotAfter -lt (Get-Date).AddDays(7)
    }
    if ($renew) {
        if (Get-Process -Name Noise.Server -ErrorAction SilentlyContinue) { throw '请先关闭正在运行的教师服务，再更新网络配置。' }
        # Keep earlier private configuration for recovery; never include it in the student ZIP.
        $data = Join-Path $PSScriptRoot 'data'
        $backup = Join-Path $data ('https-backup-' + [guid]::NewGuid().ToString('N'))
        foreach ($file in @('teacher.pfx', 'https-password.dpapi', 'https.json')) {
            $path = Join-Path $data $file
            if (Test-Path $path) { New-Item -ItemType Directory -Force $backup | Out-Null; Move-Item -LiteralPath $path -Destination $backup }
        }
        & (Join-Path $PSScriptRoot 'configure-https.ps1') -ServerIp $ServerIp -Port $Port
    }
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certPath)
    if (-not (Test-Path "Cert:\CurrentUser\Root\$($certificate.Thumbprint)")) {
        Import-Certificate -FilePath $certPath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
    }
    $rule = Get-NetFirewallRule -Name "RoomScope-Teacher-$Port" -ErrorAction SilentlyContinue
    if (-not $rule -or $rule.Enabled -ne 'True') {
        Write-Host '即将申请防火墙权限，请在 Windows 管理员提示中选择允许。'
        try {
            $helper = Join-Path $PSScriptRoot 'open-firewall.ps1'
            $process = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $helper + '" -Port ' + $Port) -Wait -PassThru
            if ($process.ExitCode -ne 0) { throw '防火墙配置失败。' }
        } catch {
            $cause = $_.Exception
            while ($cause.InnerException) { $cause = $cause.InnerException }
            if ($cause -is [ComponentModel.Win32Exception] -and $cause.NativeErrorCode -eq 1223) { $startupExitCode = 1223; throw '管理员授权已取消，准备好后可以重新尝试。' }
            throw '机房连接需要防火墙放行。请允许管理员授权，程序将自动重试。'
        }
    }
    $url = "https://${ServerIp}:${Port}"
    $startupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $startupKey -Force | Out-Null
    $startupCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $PSScriptRoot 'launch-ui.ps1') + '" -Target teacher -AtLogin'
    New-ItemProperty -Path $startupKey -Name RoomScopeTeacher -Value $startupCommand -PropertyType String -Force | Out-Null
    Write-Host '教师页面点「学生机」→「生成学生端」，设置维护密码并下载安装包。'
    # The local dashboard needs no password; student connections still use HTTPS.
    if (-not $AtLogin -and -not $FromLauncher) { $browserJob = Start-Job -ArgumentList 'http://127.0.0.1:5080',$PSScriptRoot -ScriptBlock {
        param($url, $directory)
        for ($i = 0; $i -lt 60; $i++) {
            try {
                Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2 | Out-Null
                Start-Process $url
                break
            } catch { Start-Sleep -Seconds 1 }
        }
    } }
    & (Join-Path $PSScriptRoot 'start-lan.ps1')
    if ($LASTEXITCODE -ne 0) { throw '教师服务异常停止，请检查端口占用、证书及数据目录权限。' }
} catch {
    Write-StartupError $_
    if ($_.Exception -is [OperationCanceledException]) { $startupExitCode = 1223 }
    exit $startupExitCode
}
finally {
    if ($instance) { if ($ownsInstance) { $instance.ReleaseMutex() }; $instance.Dispose() }
    if ($browserJob) { Stop-Job $browserJob; Remove-Job $browserJob }
}

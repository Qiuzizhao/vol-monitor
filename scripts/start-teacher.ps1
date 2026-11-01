param([string]$ServerIp, [ValidateRange(1024,65535)][int]$Port = 5081, [switch]$AtLogin, [switch]$FromLauncher)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console.ps1')
trap { Write-StartupError $_; exit 1 }
Set-Location $PSScriptRoot
$browserJob = $null
$staging = $null
$instance = $null
$startupExitCode = 1
try {
    if (-not (Test-Path './Noise.Server.exe')) { throw '请先完整解压教师端运行包，再双击启动入口。' }
    $settingsPath = Join-Path $PSScriptRoot 'data/https.json'
    $created = $false
    $instance = [Threading.Mutex]::new($true, ('Local\RoomScope.Teacher.' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value), [ref]$created)
    if (-not $created) {
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
    $template = Join-Path $PSScriptRoot 'student-template.zip'
    if (-not (Test-Path $template)) { throw '缺少 student-template.zip，请重新完整解压教师端运行包。' }
    $staging = Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $template -DestinationPath $staging
    @{ Server = $url; AutoEnroll = $true; ManagedMode = $true; EnableLoginStartup = $true } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $staging '程序/agent-deployment.json')
    Copy-Item -LiteralPath $certPath -Destination (Join-Path $staging '程序')
    $studentZip = Join-Path (Split-Path $PSScriptRoot -Parent) '学生端安装包.zip'
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $studentZip -Force
    Write-Host "课堂专用学生包已生成：$studentZip" -ForegroundColor Green
    Write-Host '学生机解压后，双击“启动学生端.cmd”。'
    Write-Host '教师页面点“学生机”，核对座位后确认接入。'
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
    if ($instance) { $instance.Dispose() }
    if ($browserJob) { Stop-Job $browserJob; Remove-Job $browserJob }
    if ($staging -and (Test-Path $staging)) {
        $resolved = [IO.Path]::GetFullPath($staging)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'RoomScope-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}

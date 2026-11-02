param([switch]$FromLauncher)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console.ps1')
trap { Write-StartupError $_; exit 1 }
try {
    $certificatePath = Join-Path $PSScriptRoot 'roomscope-teacher.cer'
    if (-not (Test-Path $certificatePath)) { throw '缺少教师公开证书，请向教师获取课堂专用学生包。' }
    if (-not (Test-Path (Join-Path $PSScriptRoot 'agent-deployment.json'))) { throw '缺少课堂连接地址，请向教师获取课堂专用学生包。' }
    $rootPath = Join-Path $PSScriptRoot 'roomscope-root.cer'
    if (Test-Path -LiteralPath $rootPath) { $certificatePath = $rootPath }
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certificatePath)
    if ($certificate.NotAfter -le (Get-Date)) { throw '教师证书已过期，请让教师重新生成并分发学生包。' }
    $target = Join-Path $env:ProgramFiles 'RoomScope/Student'
    $installed = Join-Path $target 'Noise.Agent.exe'
    $profile = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'agent-deployment.json') -Raw | ConvertFrom-Json
    $service = Get-Service RoomScopeStudentGuard -ErrorAction SilentlyContinue
    $current = $false
    if ($service -and $service.Status -eq 'Running' -and (Test-Path -LiteralPath $installed)) {
        $saved = Get-Content -LiteralPath (Join-Path $target 'agent-deployment.json') -Raw | ConvertFrom-Json
        $current = $saved.SystemGuard -and $saved.Server -eq $profile.Server -and
            (Get-FileHash -LiteralPath $installed).Hash -eq (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Noise.Agent.exe')).Hash
        foreach ($leaf in @('roomscope-teacher.cer','roomscope-root.cer')) {
            if (Test-Path -LiteralPath (Join-Path $PSScriptRoot $leaf)) { $current = $current -and (Test-Path -LiteralPath (Join-Path $target $leaf)) -and (Get-FileHash -LiteralPath (Join-Path $target $leaf)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $leaf)).Hash }
        }
    }
    if (-not $current) {
        Write-Host '正在安装或升级学生端，需要一次管理员授权。'
        $result = Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-install-' + [guid]::NewGuid().ToString('N') + '.txt')
        $helper = Join-Path $PSScriptRoot 'install-student.ps1'
        if (-not (Test-Path -LiteralPath $helper)) { throw '缺少安装组件，请使用完整的新版学生端包。' }
        try {
            $process = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $helper + '" -SourceDirectory "' + $PSScriptRoot + '" -ResultPath "' + $result + '"') -Wait -PassThru
            $message = if (Test-Path -LiteralPath $result) { [IO.File]::ReadAllText($result) } else { '学生端安装未完成，请重新运行并允许管理员授权。' }
            if ($process.ExitCode -ne 0 -or $message -ne 'OK') { throw $message }
        } catch {
            $cause=$_.Exception; while ($cause.InnerException) { $cause=$cause.InnerException }
            if ($cause -is [ComponentModel.Win32Exception] -and $cause.NativeErrorCode -eq 1223) { Write-Host '管理员授权已取消。'; exit 1223 }
            throw
        } finally { Remove-Item -LiteralPath $result -ErrorAction SilentlyContinue }
    }
    Start-Process -FilePath $installed -WorkingDirectory $target
} catch { Write-StartupError $_; exit 1 }

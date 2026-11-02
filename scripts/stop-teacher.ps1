param([switch]$ForStartup, [string]$CurrentDirectory)
$ErrorActionPreference = 'Stop'
function Test-TeacherDirectory([string]$Directory) {
    if (-not $Directory) { return $false }
    $script = Join-Path $Directory 'start-teacher.ps1'
    return (Test-Path -LiteralPath $script) -and [bool](Select-String -LiteralPath $script -Pattern 'RoomScopeTeacher|RoomScope\.Teacher' -Quiet)
}
function Get-TeacherScript([string]$CommandLine) {
    if ($CommandLine -notmatch '(?i)-File\s+(?:"([^"]+)"|([^\s]+))') { return $null }
    $path = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
    $leaf = [IO.Path]::GetFileName($path)
    if ($leaf -notin @('start-teacher.ps1','start-lan.ps1','launch-ui.ps1','startup-worker.ps1')) { return $null }
    if ($leaf -in @('launch-ui.ps1','startup-worker.ps1') -and $CommandLine -notmatch '(?i)-Target\s+teacher\b') { return $null }
    if (-not (Test-TeacherDirectory ([IO.Path]::GetDirectoryName($path)))) { return $null }
    return $path
}
try {
    Write-Host '正在检查旧版教师端。'
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $processes = @(Get-CimInstance Win32_Process | Where-Object { $_.SessionId -eq $session })
    $protected = New-Object 'Collections.Generic.HashSet[int]'
    $ancestor = $PID
    while ($ancestor -and $protected.Add([int]$ancestor)) {
        $parent = $processes | Where-Object ProcessId -eq $ancestor | Select-Object -First 1
        $ancestor = if ($parent) { $parent.ParentProcessId } else { 0 }
    }
    $workers = @($processes | Where-Object { $_.Name -ieq 'powershell.exe' -and (Get-TeacherScript $_.CommandLine) })
    $servers = @($processes | Where-Object { $_.Name -ieq 'Noise.Server.exe' -and $_.ExecutablePath -and (Test-TeacherDirectory ([IO.Path]::GetDirectoryName($_.ExecutablePath))) })
    $unreadableServers = @($processes | Where-Object { $_.Name -ieq 'Noise.Server.exe' -and -not $_.ExecutablePath })
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $startup = Get-ItemProperty -LiteralPath $key -Name RoomScopeTeacher -ErrorAction SilentlyContinue
    $oldDirectories = @($servers | ForEach-Object { [IO.Path]::GetDirectoryName($_.ExecutablePath) })
    if ($startup) {
        $previousScript = Get-TeacherScript $startup.RoomScopeTeacher
        if ($previousScript) { $oldDirectories += [IO.Path]::GetDirectoryName($previousScript) }
    }
    $oldDirectories += @($workers | ForEach-Object { [IO.Path]::GetDirectoryName((Get-TeacherScript $_.CommandLine)) })
    if ($ForStartup) {
        if (-not $CurrentDirectory) { throw '缺少新版程序目录。' }
        $currentFull = [IO.Path]::GetFullPath($CurrentDirectory).TrimEnd('\')
        $sameServer = @($servers | Where-Object { [IO.Path]::GetDirectoryName($_.ExecutablePath).TrimEnd('\') -ieq $currentFull })
        if ($sameServer.Count) {
            try { $health = Invoke-RestMethod 'http://127.0.0.1:5080/api/health' -TimeoutSec 2 } catch { $health = $null }
            if ($health -and $health.status -eq 'ok') { Write-Host '当前目录的教师服务已就绪，无需重新启动。'; return }
        }
        $migrationPointer = Join-Path $currentFull 'previous-teacher.txt'
        if (Test-Path -LiteralPath $migrationPointer) {
            $previousDirectory = [IO.File]::ReadAllText($migrationPointer).Trim()
            if (Test-TeacherDirectory $previousDirectory) { $oldDirectories = @($previousDirectory) + $oldDirectories }
        }
        # Remember the source before stopping it, so a failed copy can resume on retry.
        foreach ($oldDirectory in $oldDirectories | Select-Object -Unique) {
            if ([IO.Path]::GetFullPath($oldDirectory).TrimEnd('\') -ine $currentFull -and (Test-Path -LiteralPath (Join-Path $oldDirectory 'data/roomscope.db'))) {
                [IO.File]::WriteAllText($migrationPointer, $oldDirectory, [Text.UTF8Encoding]::new($true))
                break
            }
        }
    }
    $workers = @($workers | Where-Object { -not $protected.Contains([int]$_.ProcessId) })
    # Stop the launchers first so they cannot restart a worker while it is closing.
    $workers = @($workers | Sort-Object @{Expression={ if ($_.CommandLine -match 'launch-ui\.ps1') { 0 } else { 1 } }})
    $stopped = 0
    foreach ($item in @($workers) + @($servers)) {
        $process = Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue
        if ($process) {
            $null = $process.Handle
            try { Stop-Process -Id $item.ProcessId -ErrorAction Stop }
            catch { if (Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue) { throw } }
            if (-not $process.WaitForExit(10000)) { throw '旧版进程尚未结束，稍后将自动重试。' }
            $stopped++
        }
    }
    if ($unreadableServers.Count) { throw '另有同名教师服务无法读取程序路径，请以管理员身份重新运行本工具。' }
    if ($ForStartup) {
        $destination = Join-Path $currentFull 'data'
        $hasData = (Test-Path -LiteralPath $destination) -and @(Get-ChildItem -LiteralPath $destination -Force).Count -gt 0
        if (-not $hasData) {
            foreach ($oldDirectory in $oldDirectories | Select-Object -Unique) {
                if ([IO.Path]::GetFullPath($oldDirectory).TrimEnd('\') -ieq $currentFull) { continue }
                $source = Join-Path $oldDirectory 'data'
                if (-not (Test-Path -LiteralPath (Join-Path $source 'roomscope.db'))) { continue }
                if ($currentFull.StartsWith([IO.Path]::GetFullPath($source).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '请把新版解压到旧版 data 目录之外。' }
                # Finish copying before making the migrated data visible to startup/retry.
                $staging = Join-Path $currentFull ('data-upgrade-' + [guid]::NewGuid().ToString('N'))
                try {
                    New-Item -ItemType Directory -Path $staging | Out-Null
                    Get-ChildItem -LiteralPath $source -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $staging -Recurse -Force }
                    $certificate = Join-Path $oldDirectory 'roomscope-teacher.cer'
                    if ((Test-Path -LiteralPath $certificate) -and -not (Test-Path -LiteralPath (Join-Path $currentFull 'roomscope-teacher.cer'))) { Copy-Item -LiteralPath $certificate -Destination $currentFull }
                    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
                    Move-Item -LiteralPath $staging -Destination $destination
                } finally {
                    if ([IO.Path]::GetFullPath($staging).StartsWith($currentFull + '\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $staging)) { Remove-Item -LiteralPath $staging -Recurse -Force }
                }
                Write-Host '已沿用旧版的机房配置、座位、设备和维护密码。'
                break
            }
        }
        Remove-Item -LiteralPath $migrationPointer -ErrorAction SilentlyContinue
        Write-Host '旧版检查完成，正在启动当前版本。'
    } else { Write-Host "已关闭 $stopped 个教师端相关进程。现在可以移动或删除旧目录，或启动新版。" }
    if ($startup -and (Get-TeacherScript $startup.RoomScopeTeacher)) {
        Remove-ItemProperty -LiteralPath $key -Name RoomScopeTeacher
        Write-Host '旧的教师端登录自启已取消。新版成功启动后会重新登记。'
    }
} catch {
    if ($ForStartup) { throw ('自动接管旧版未完成：' + $_.Exception.Message + ' 如提示拒绝访问，请右键「启动教师端.cmd」以管理员身份运行。') }
    Write-Host ('关闭未完成：' + $_.Exception.Message)
    Write-Host '如果提示拒绝访问，请右键「关闭旧版教师端.cmd」，选择「以管理员身份运行」。'
    exit 1
}

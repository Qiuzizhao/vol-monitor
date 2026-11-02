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
    Write-Host '正在关闭旧版教师端，不会删除数据、密码或学生端。'
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $processes = @(Get-CimInstance Win32_Process | Where-Object { $_.SessionId -eq $session })
    $workers = @($processes | Where-Object { $_.Name -ieq 'powershell.exe' -and (Get-TeacherScript $_.CommandLine) })
    $servers = @($processes | Where-Object { $_.Name -ieq 'Noise.Server.exe' -and $_.ExecutablePath -and (Test-TeacherDirectory ([IO.Path]::GetDirectoryName($_.ExecutablePath))) })
    $unreadableServers = @($processes | Where-Object { $_.Name -ieq 'Noise.Server.exe' -and -not $_.ExecutablePath })
    # Stop the launchers first so they cannot restart a worker while it is closing.
    $workers = @($workers | Sort-Object @{Expression={ if ($_.CommandLine -match 'launch-ui\.ps1') { 0 } else { 1 } }})
    $stopped = 0
    foreach ($item in @($workers) + @($servers)) {
        if (Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue) {
            try { Stop-Process -Id $item.ProcessId -ErrorAction Stop }
            catch { if (Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue) { throw } }
            $stopped++
        }
    }
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $startup = Get-ItemProperty -LiteralPath $key -Name RoomScopeTeacher -ErrorAction SilentlyContinue
    if ($startup -and (Get-TeacherScript $startup.RoomScopeTeacher)) {
        Remove-ItemProperty -LiteralPath $key -Name RoomScopeTeacher
        Write-Host '旧的教师端登录自启已取消。新版成功启动后会重新登记。'
    }
    if ($unreadableServers.Count) { throw '另有同名教师服务无法读取程序路径，请以管理员身份重新运行本工具。' }
    Write-Host "已关闭 $stopped 个教师端相关进程。现在可以移动或删除旧目录，或启动新版。"
} catch {
    Write-Host ('关闭未完成：' + $_.Exception.Message)
    Write-Host '如果提示拒绝访问，请右键「关闭旧版教师端.cmd」，选择「以管理员身份运行」。'
    exit 1
}

param([ValidateSet('teacher','student')][string]$Target = 'teacher', [switch]$AtLogin)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console.ps1')
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Net.Http
[Windows.Forms.Application]::EnableVisualStyles()
$form = New-Object Windows.Forms.Form
$form.Text = if ($Target -eq 'teacher') { '正在启动教师端' } else { '正在启动学生端' }
$form.ClientSize = New-Object Drawing.Size(490, 235)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.Font = New-Object Drawing.Font('Microsoft YaHei UI', 10)
$heading = New-Object Windows.Forms.Label
$heading.Text = '正在准备，请稍候……'
$heading.SetBounds(24, 20, 440, 30)
$status = New-Object Windows.Forms.Label
$status.Text = '请完成弹出的证书确认或管理员授权，程序会自动继续。'
$status.SetBounds(24, 59, 440, 77)
$progress = New-Object Windows.Forms.ProgressBar
$progress.SetBounds(24, 145, 440, 10)
$progress.Style = 'Marquee'
$retry = New-Object Windows.Forms.Button
$retry.Text = '重新尝试'
$retry.SetBounds(339, 180, 125, 32)
$retry.Visible = $false
$form.Controls.AddRange(@($heading, $status, $progress, $retry))
if ($AtLogin) { $form.Opacity = 0; $form.ShowInTaskbar = $false }
$state = @{ Attempt = 0; Worker = $null; NextTry = $null; Probe = $null; Done = $false; ExitedAt = $null; LastError = '' }
$logRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RoomScope/Startup'
New-Item -ItemType Directory -Force $logRoot | Out-Null
$runId = [guid]::NewGuid().ToString('N')
$outputPath = Join-Path $logRoot "$Target-$runId.log"
$errorPath = Join-Path $logRoot "$Target-$runId-error.log"
$eventsPath = Join-Path $logRoot "$Target-$runId-events.log"
$http = New-Object Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds(2)
$timer = New-Object Windows.Forms.Timer
$timer.Interval = 500

function Show-Launcher {
    $form.Opacity = 1; $form.ShowInTaskbar = $true; $form.WindowState = 'Normal'; $form.Activate()
}
function Start-Worker {
    try {
    if ($state.Worker -and -not $state.Worker.HasExited) { $retry.Visible = $false; return }
    $state.Attempt++
    Add-Content -LiteralPath $eventsPath -Encoding UTF8 -Value "第 $($state.Attempt) 次启动。"
    $state.NextTry = $null; $state.Probe = $null; $state.ExitedAt = $null
    $heading.Text = '正在启动，请完成系统授权……'
    $status.Text = '证书和管理员授权确认后会自动继续，无需关闭窗口或重新运行。'
    $progress.Visible = $true; $retry.Visible = $false
    $scriptPath = Join-Path $PSScriptRoot "start-$Target.ps1"
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $scriptPath + '" -FromLauncher'
    if ($AtLogin) { $arguments += ' -AtLogin' }
    $state.Worker = Start-Process powershell.exe -WindowStyle Hidden -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath -PassThru
    # Retain the process handle on Windows PowerShell so ExitCode remains
    # available after the child exits (otherwise it can incorrectly be null).
    $null = $state.Worker.Handle
    } catch {
        Show-Launcher
        $heading.Text = '启动进程未能创建'
        $status.Text = '请检查程序目录是否可访问，然后点击“重新尝试”。'
        $retry.Visible = $true; $progress.Visible = $false
    }
}
function Finish-Launch {
    $state.Done = $true; $timer.Stop()
    if ($Target -eq 'teacher' -and -not $AtLogin) { Start-Process 'http://127.0.0.1:5080' }
    $form.Close()
}
function Schedule-Retry([int]$Code) {
    Add-Content -LiteralPath $eventsPath -Encoding UTF8 -Value "启动未完成（退出码 $Code），正在处理重试。"
    $delay = Get-StartupRetryDelay -Attempt $state.Attempt -ExitCode $Code
    Show-Launcher
    if ($delay -lt 0) {
        $heading.Text = if ($Code -eq 1223) { '尚未完成授权' } else { '暂时未能启动' }
        $status.Text = if ($Code -eq 1223) { '系统授权已取消。点击“重新尝试”，在系统提示中允许即可继续。' } else { $state.LastError + "`r`n" + '已自动重试，完成权限设置后可点击“重新尝试”。' }
        $retry.Visible = $true; $progress.Visible = $false
    } else {
        $heading.Text = '正在自动重试'
        $status.Text = "刚完成的权限设置可能需要一点时间，$delay 秒后自动继续（第 $($state.Attempt + 1) 次）。"
        $state.NextTry = (Get-Date).AddSeconds($delay)
    }
}
$retry.Add_Click({ $state.Attempt = 0; Start-Worker })
$timer.Add_Tick({
    try {
        if ($state.Done) { return }
        if ($state.NextTry) { if ((Get-Date) -ge $state.NextTry) { Start-Worker }; return }
        if ($retry.Visible -or -not $state.Worker) { return }
        $state.Worker.Refresh()
        if ($state.Worker.HasExited) { $state.Worker.WaitForExit() }
        if ($state.Worker.HasExited -and $state.Worker.ExitCode -ne 0) {
            $state.LastError = @(Get-Content -LiteralPath $outputPath -Encoding UTF8 -Tail 1 -ErrorAction SilentlyContinue) -join ''
            Schedule-Retry $state.Worker.ExitCode; return
        }
        if ($Target -eq 'student') { if ($state.Worker.HasExited) { Finish-Launch }; return }
        if (-not $state.Probe) { $state.Probe = $http.GetAsync('http://127.0.0.1:5080/api/health') }
        if ($state.Probe.IsCompleted) {
            if ($state.Probe.Status -eq 'RanToCompletion') {
                $response = $state.Probe.Result
                $ready = $response.IsSuccessStatusCode
                $response.Dispose()
                if ($ready) { Finish-Launch; return }
            }
            $state.Probe = $null
        }
        if ($state.Worker.HasExited) {
            if (-not $state.ExitedAt) { $state.ExitedAt = Get-Date }
            if (((Get-Date) - $state.ExitedAt).TotalSeconds -gt 5) { Schedule-Retry 1 }
        } elseif (Test-Path $outputPath) {
            $lines = @(Get-Content -LiteralPath $outputPath -Encoding UTF8 -Tail 4 -ErrorAction SilentlyContinue)
            if ($lines.Count) { $status.Text = $lines[-1] }
        }
    } catch {
        $heading.Text = '启动检查暂未完成'
        $status.Text = '请完成系统授权；也可稍后点击“重新尝试”。'
        $retry.Visible = $true; Show-Launcher
    }
})
$form.Add_FormClosing({ param($sender, $eventArgs)
    if (-not $state.Done -and $state.Worker -and -not $state.Worker.HasExited) { $eventArgs.Cancel = $true; $form.WindowState = 'Minimized' }
})
$form.Add_Shown({ Start-Worker; $timer.Start() })
try { [Windows.Forms.Application]::Run($form) }
finally { $timer.Dispose(); $http.Dispose(); $form.Dispose() }

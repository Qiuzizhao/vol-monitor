param([ValidateSet('teacher','student')][string]$Target = 'teacher', [switch]$AtLogin)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console.ps1')
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Net.Http
[Windows.Forms.Application]::EnableVisualStyles()
$form = New-Object Windows.Forms.Form
$form.Text = if ($Target -eq 'teacher') { '正在启动教师端' } else { '正在启动学生端' }
$form.ClientSize = New-Object Drawing.Size(490, 265)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.Font = New-Object Drawing.Font('Microsoft YaHei UI', 10)
$heading = New-Object Windows.Forms.Label
$heading.Text = '正在准备，请稍候……'
$heading.SetBounds(24, 20, 440, 30)
$status = New-Object Windows.Forms.Label
$status.Text = '正在检查程序文件、机房网络和运行环境。'
$status.SetBounds(24, 59, 440, 115)
$progress = New-Object Windows.Forms.ProgressBar
$progress.SetBounds(24, 181, 440, 10)
$progress.Style = 'Marquee'
$retry = New-Object Windows.Forms.Button
$retry.Text = '重新尝试'
$retry.SetBounds(339, 210, 125, 32)
$retry.Visible = $false
$details = New-Object Windows.Forms.Button
$details.Text = '查看启动记录'
$details.SetBounds(24, 210, 130, 32)
$details.Visible = $false
$form.Controls.AddRange(@($heading, $status, $progress, $retry, $details))
if ($AtLogin) { $form.Opacity = 0; $form.ShowInTaskbar = $false }
$state = @{ Attempt = 0; Worker = $null; NextTry = $null; Probe = $null; Done = $false; ExitedAt = $null; LastError = '' }
$logRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RoomScope/Startup'
New-Item -ItemType Directory -Force $logRoot | Out-Null
$runId = [guid]::NewGuid().ToString('N')
$outputPath = Join-Path $logRoot "$Target-$runId.log"
$errorPath = Join-Path $logRoot "$Target-$runId-error.log"
$eventsPath = Join-Path $logRoot "$Target-$runId-events.log"
$reportPath = Join-Path $logRoot "$Target-$runId-details.txt"
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
    $heading.Text = '正在检查并启动……'
    $status.Text = '正在检查程序文件、网络和证书；需要系统授权时会弹出提示。'
    $progress.Visible = $true; $retry.Visible = $false
    $scriptPath = Join-Path $PSScriptRoot "start-$Target.ps1"
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $scriptPath + '" -FromLauncher'
    if ($AtLogin) { $arguments += ' -AtLogin' }
    $state.Worker = Start-Process powershell.exe -WindowStyle Hidden -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath -PassThru
    # Retain the process handle on Windows PowerShell so ExitCode remains
    # available after the child exits (otherwise it can incorrectly be null).
    $null = $state.Worker.Handle
    } catch {
        $state.LastError = '无法创建启动进程，请检查程序是否完整解压、目录是否可访问。'
        Add-Content -LiteralPath $eventsPath -Encoding UTF8 -Value $_.Exception.ToString()
        Show-Launcher
        $heading.Text = '启动进程未能创建'
        $status.Text = $state.LastError
        $details.Visible = $true
        $retry.Visible = $true; $progress.Visible = $false
    }
}
function Finish-Launch {
    $state.Done = $true; $timer.Stop()
    if ($Target -eq 'teacher' -and -not $AtLogin) { Start-Process 'http://127.0.0.1:5080' }
    $form.Close()
}
function Schedule-Retry([int]$Code) {
    $state.LastError = Get-StartupFailureMessage -OutputPath $outputPath -ErrorPath $errorPath -ExitCode $Code
    Add-Content -LiteralPath $eventsPath -Encoding UTF8 -Value "启动未完成（退出码 $Code），正在处理重试。"
    Add-Content -LiteralPath $eventsPath -Encoding UTF8 -Value $state.LastError
    $delay = Get-StartupRetryDelay -Attempt $state.Attempt -ExitCode $Code
    Show-Launcher
    $details.Visible = $true
    if ($delay -lt 0) {
        $heading.Text = if ($Code -eq 1223) { '尚未完成授权' } else { '暂时未能启动' }
        $status.Text = if ($Code -eq 1223) { '系统授权已取消。点击“重新尝试”，在系统提示中允许即可继续。' } else { $state.LastError + "`r`n" + '请处理上述问题后点击「重新尝试」。' }
        $retry.Visible = $true; $progress.Visible = $false
    } else {
        $heading.Text = '正在自动重试'
        $status.Text = $state.LastError + "`r`n$delay 秒后自动重试（第 $($state.Attempt + 1) 次）。"
        $state.NextTry = (Get-Date).AddSeconds($delay)
    }
}
$retry.Add_Click({ $state.Attempt = 0; Start-Worker })
$details.Add_Click({
    $report = @(('启动原因：' + $state.LastError), ('程序目录：' + $PSScriptRoot), '', '启动过程：')
    $report += @(Get-Content -LiteralPath $eventsPath -Encoding UTF8 -ErrorAction SilentlyContinue)
    $report += @('', '程序输出：')
    $report += @(Get-Content -LiteralPath $outputPath -Encoding UTF8 -ErrorAction SilentlyContinue)
    $report += @('', '错误记录：')
    $report += @(Get-Content -LiteralPath $errorPath -Encoding UTF8 -ErrorAction SilentlyContinue)
    $report | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Start-Process notepad.exe -ArgumentList ('"' + $reportPath + '"')
})
$timer.Add_Tick({
    try {
        if ($state.Done) { return }
        if ($state.NextTry) { if ((Get-Date) -ge $state.NextTry) { Start-Worker }; return }
        if ($retry.Visible -or -not $state.Worker) { return }
        $state.Worker.Refresh()
        if ($state.Worker.HasExited) { $state.Worker.WaitForExit() }
        if ($state.Worker.HasExited -and $state.Worker.ExitCode -ne 0) {
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
        Add-Content -LiteralPath $eventsPath -Encoding UTF8 -Value $_.Exception.ToString()
        $heading.Text = '启动检查暂未完成'
        $state.LastError = '启动状态检查失败，请点「查看启动记录」查看具体原因。'
        $status.Text = $state.LastError
        $details.Visible = $true
        $retry.Visible = $true; Show-Launcher
    }
})
$form.Add_FormClosing({ param($sender, $eventArgs)
    if (-not $state.Done -and $state.Worker -and -not $state.Worker.HasExited) { $eventArgs.Cancel = $true; $form.WindowState = 'Minimized' }
})
$form.Add_Shown({ Start-Worker; $timer.Start() })
try { [Windows.Forms.Application]::Run($form) }
finally { $timer.Dispose(); $http.Dispose(); $form.Dispose() }

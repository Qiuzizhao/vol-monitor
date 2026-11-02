# UTF-8 BOM on this file and all translated scripts supports Windows PowerShell 5.1.
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
# Hidden processes on some Windows installations have no usable console handle.
# Console encoding is optional and must not abort startup before error handlers exist.
try { [Console]::OutputEncoding = $OutputEncoding } catch { }
$ProgressPreference = 'SilentlyContinue'
function Write-StartupError($Record) {
    if ($env:RoomScopeStartupTrace) {
        try { [IO.File]::AppendAllText($env:RoomScopeStartupTrace, ($Record | Out-String) + "`r`n" + $Record.ScriptStackTrace + "`r`n", [Text.UTF8Encoding]::new($true)) } catch { }
    }
    $message = $Record.Exception.Message
    if ($message -match '^[\u4e00-\u9fff]') { Write-Host $message -ForegroundColor Red; return }
    $friendly = switch -Regex ($Record.Exception.GetType().Name) {
        'UnauthorizedAccess|Security' { '权限不足，请检查当前账户对程序目录的访问权限，以及管理员授权是否完成。'; break }
        'Cryptographic' { '证书无法读取，请使用原来配置教师端的 Windows 账户运行，或重新配置连接证书。'; break }
        'IOException|FileNotFound|DirectoryNotFound' { '文件无法读取或写入，请完整解压运行包，并检查目录权限及文件占用。'; break }
        default { '操作未完成，请检查运行包是否完整、网络是否连通，以及证书和目录权限是否正确。' }
    }
    Write-Host $friendly -ForegroundColor Red
}

function Select-ClassroomNetwork($Addresses) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object Windows.Forms.Form
    $dialog.Text = '选择机房网络'
    $dialog.ClientSize = New-Object Drawing.Size(430, 155)
    $dialog.StartPosition = 'CenterScreen'
    $dialog.FormBorderStyle = 'FixedDialog'
    $dialog.MaximizeBox = $false
    $label = New-Object Windows.Forms.Label
    $label.Text = '请选择学生机所在的网络：'
    $label.SetBounds(20, 18, 390, 25)
    $list = New-Object Windows.Forms.ComboBox
    $list.DropDownStyle = 'DropDownList'
    $list.SetBounds(20, 50, 390, 28)
    foreach ($address in $Addresses) { [void]$list.Items.Add("$($address.Adapter)（$($address.Address)）") }
    $list.SelectedIndex = 0
    $confirm = New-Object Windows.Forms.Button
    $confirm.Text = '使用此网络'
    $confirm.SetBounds(280, 103, 130, 32)
    $confirm.DialogResult = 'OK'
    $dialog.Controls.AddRange(@($label, $list, $confirm))
    $dialog.AcceptButton = $confirm
    try {
        if ($dialog.ShowDialog() -ne 'OK') { throw [OperationCanceledException]::new('已取消选择网络，请准备好后点击重新尝试。') }
        return $Addresses[$list.SelectedIndex].Address
    } finally { $dialog.Dispose() }
}

function Get-StartupRetryDelay([int]$Attempt, [int]$ExitCode) {
    if ($ExitCode -eq 1223 -or $Attempt -ge 3) { return -1 }
    return [int][Math]::Pow(2, $Attempt)
}

function Get-StartupFailureMessage([string]$OutputPath, [string]$ErrorPath, [int]$ExitCode) {
    $outputLines = @(Get-Content -LiteralPath $OutputPath -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $friendly = @($outputLines | ForEach-Object { $_ -replace '^\d{2}:\d{2}:\d{2} ', '' } | Where-Object { $_.Trim() -match '^[\u4e00-\u9fff]' -and $_ -notmatch '^(启动入口已执行|开始执行：|启动脚本结束)' })
    if ($friendly.Count) { return $friendly[-1].Trim() }
    $errors = @(Get-Content -LiteralPath $ErrorPath -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"
    if ($errors -match 'ParserError|Unexpected token|解析错误|无法解析|意外的标记') { return '启动脚本无法解析，请完整解压新版运行包，不要混用不同版本的程序文件。' }
    if ($errors -match 'UnauthorizedAccess|Access.*denied|拒绝访问|权限不足') { return '无法访问程序文件或设置，请检查解压目录的读写权限。' }
    if ($errors -match 'not recognized|CommandNotFound|无法将|找不到') { return '启动所需文件或系统组件未找到，请完整解压运行包，并点「查看启动记录」核对缺失项目。' }
    return "启动未完成（退出码 $ExitCode），请点「查看启动记录」查看具体原因。"
}

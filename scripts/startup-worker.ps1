param([ValidateSet('teacher','student')][string]$Target, [string]$TracePath, [switch]$AtLogin)
$ErrorActionPreference = 'Stop'
function Trace([string]$Message) {
    [IO.File]::AppendAllText($TracePath, ((Get-Date).ToString('HH:mm:ss') + ' ' + $Message + "`r`n"), [Text.UTF8Encoding]::new($true))
}
$env:RoomScopeStartupTrace = $TracePath
try {
    Trace '启动入口已执行。'
    Trace ("PowerShell：" + $PSVersionTable.PSVersion + '；64 位进程：' + [Environment]::Is64BitProcess)
    $scriptPath = Join-Path $PSScriptRoot "start-$Target.ps1"
    if (-not (Test-Path -LiteralPath $scriptPath)) { throw '启动脚本缺失，请完整解压运行包。' }
    $tokens = $null; $parseErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) {
        foreach ($item in $parseErrors) { Trace ("脚本第 $($item.Extent.StartLineNumber) 行：" + $item.Message) }
        throw '启动脚本无法解析，请完整解压新版运行包。'
    }
    Trace ('开始执行：' + $scriptPath)
    $parameters = @{ FromLauncher = $true }
    if ($AtLogin -and $Target -eq 'teacher') { $parameters.AtLogin = $true }
    $global:LASTEXITCODE = 0
    & $scriptPath @parameters *>&1 | ForEach-Object {
        [IO.File]::AppendAllText($TracePath, ($_ | Out-String), [Text.UTF8Encoding]::new($true))
    }
    $code = $LASTEXITCODE
    Trace ("启动脚本结束，退出码：$code")
    exit $code
} catch {
    Trace ('启动异常：' + $_.Exception.Message)
    Trace $_.InvocationInfo.PositionMessage
    Trace $_.ScriptStackTrace
    exit 1
}

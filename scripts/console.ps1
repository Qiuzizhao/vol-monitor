# UTF-8 BOM on this file and all translated scripts supports Windows PowerShell 5.1.
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $OutputEncoding
$ProgressPreference = 'SilentlyContinue'
function Write-StartupError($Record) {
    $message = $Record.Exception.Message
    if ($message -match '[\u4e00-\u9fff]') { Write-Host $message -ForegroundColor Red; return }
    $friendly = switch -Regex ($Record.Exception.GetType().Name) {
        'UnauthorizedAccess|Security' { '权限不足，请检查当前账户对程序目录的访问权限，以及管理员授权是否完成。'; break }
        'Cryptographic' { '证书无法读取，请使用原来配置教师端的 Windows 账户运行，或重新配置连接证书。'; break }
        'IOException|FileNotFound|DirectoryNotFound' { '文件无法读取或写入，请完整解压运行包，并检查目录权限及文件占用。'; break }
        default { '操作未完成，请检查运行包是否完整、网络是否连通，以及证书和目录权限是否正确。' }
    }
    Write-Host $friendly -ForegroundColor Red
}

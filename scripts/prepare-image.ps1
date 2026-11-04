param(
    [Parameter(Mandatory=$true)][string]$TeacherServer,
    [string]$IdentityDirectory,
    [switch]$EnableLoginStartup
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console.ps1')
trap { Write-StartupError $_; exit 1 }
$agentPath = Join-Path $PSScriptRoot 'Noise.Agent.exe'
if (-not (Test-Path $agentPath)) { throw '请在完整解压后的学生端目录中运行此脚本。' }
$service = Get-Service RoomScopeStudentGuard -ErrorAction SilentlyContinue
if (-not $service) { throw '请先双击课堂专用学生包的「启动学生端.cmd」安装系统守护，再从已安装目录运行镜像准备。' }
$registered = Get-CimInstance Win32_Service -Filter "Name='RoomScopeStudentGuard'"
if ($registered.PathName -notlike ('*"'+$agentPath+'"*')) { throw '请在 C:\Program Files\RoomScope\Student 目录执行镜像准备。' }
Stop-Service RoomScopeStudentGuard -Force
$service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
foreach ($process in Get-CimInstance Win32_Process -Filter "Name='Noise.Agent.exe'") {
    if ($process.ExecutablePath -ieq $agentPath) { Stop-Process -Id $process.ProcessId -ErrorAction SilentlyContinue }
}
$serverUri = $null
if (-not [Uri]::TryCreate($TeacherServer, [UriKind]::Absolute, [ref]$serverUri) -or
    $serverUri.Scheme -ne 'https' -or $serverUri.AbsolutePath -ne '/' -or
    $serverUri.UserInfo -or $serverUri.Query -or $serverUri.Fragment) {
    throw '教师地址必须是安全连接根地址，例如 https://192.168.1.10:5081。'
}
if ($IdentityDirectory -and [Environment]::ExpandEnvironmentVariables($IdentityDirectory) -notmatch '^[A-Za-z]:[\\/]') {
    throw '身份目录必须是不参与还原的本地分区上的完整路径。'
}
function Clear-CurrentIdentity {
    $process = Start-Process -FilePath $agentPath -ArgumentList '--prepare-image' -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw '身份清理失败，请检查文件权限和现有部署配置。' }
}
# Clean both the old configured location and the new target, for this Windows account.
Clear-CurrentIdentity
$deploymentProfile = @{ Server = $serverUri.GetLeftPart([UriPartial]::Authority); AutoEnroll = $true; IdentityDirectory = $IdentityDirectory; ManagedMode = $true; EnableLoginStartup = $false; SystemGuard = $true }
$deploymentProfile | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $PSScriptRoot 'agent-deployment.json')
Clear-CurrentIdentity
Write-Host '镜像配置已准备完成，不含设备编号或设备凭证。'
Write-Host ('教师服务地址：' + $deploymentProfile.Server)
Write-Host '封装镜像前，请勿再次启动母盘客户端或申请接入。'
Write-Host '请让克隆后的电脑名称对应实际座位（如 PC01、PC02），或在教师端核对座位。'
Write-Host '部署完成后，学生机按局域网 IP 自动加入教师端，无需手动批准。'
Write-Host '系统守护保留自动启动，下次开机登录后自动运行；设备获批并绑定后持续监测音量。'

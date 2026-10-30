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
if (Get-Process -Name Noise.Agent -ErrorAction SilentlyContinue) { throw '制作镜像前，请先通过教师维护出口停止所有学生客户端和守护程序。' }
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
$deploymentProfile = @{ Server = $serverUri.GetLeftPart([UriPartial]::Authority); AutoEnroll = $true; IdentityDirectory = $IdentityDirectory; ManagedMode = $true; EnableLoginStartup = [bool]$EnableLoginStartup }
$deploymentProfile | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $PSScriptRoot 'agent-deployment.json')
Clear-CurrentIdentity
if ($EnableLoginStartup) {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name RoomScopeAgent -Value ('"' + $agentPath + '" --minimized') -PropertyType String -Force | Out-Null
}
Write-Host '镜像配置已准备完成，不含设备编号或设备凭证。'
Write-Host ('教师服务地址：' + $deploymentProfile.Server)
Write-Host '封装镜像前，请勿再次启动母盘客户端或申请接入。'
Write-Host '请让克隆后的电脑名称对应实际座位（如 PC01、PC02），或在教师端核对座位。'
Write-Host '部署完成后，在教师端开启批量接入窗口，并批准待接入电脑。'
Write-Host '登录自启对当前 Windows 账户生效；设备获批并绑定后，由教师实验统一控制音量采集。'

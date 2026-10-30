param([switch]$FromLauncher)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console.ps1')
trap { Write-StartupError $_; exit 1 }
try {
    $certificatePath = Join-Path $PSScriptRoot 'roomscope-teacher.cer'
    if (-not (Test-Path $certificatePath)) { throw '缺少教师公开证书，请向教师获取课堂专用学生包。' }
    if (-not (Test-Path (Join-Path $PSScriptRoot 'agent-deployment.json'))) { throw '缺少课堂连接地址，请向教师获取课堂专用学生包。' }
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certificatePath)
    if ($certificate.NotAfter -le (Get-Date)) { throw '教师证书已过期，请让教师重新生成并分发学生包。' }
    if (-not (Test-Path "Cert:\CurrentUser\Root\$($certificate.Thumbprint)")) {
        Write-Host '正在为当前 Windows 账户安装教师公开证书，请确认系统证书提示。'
        Import-Certificate -FilePath $certificatePath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
    }
    Start-Process -FilePath (Join-Path $PSScriptRoot 'Noise.Agent.exe') -WorkingDirectory $PSScriptRoot
} catch { Write-StartupError $_; exit 1 }

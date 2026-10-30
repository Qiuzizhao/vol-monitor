$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console.ps1')
trap { Write-StartupError $_; exit 1 }
Set-Location $PSScriptRoot
$dataDirectory = Join-Path $PSScriptRoot 'data'
$configPath = Join-Path $dataDirectory 'https.json'
if (-not (Test-Path $configPath)) { throw '尚未配置连接，请双击 start-teacher.cmd 自动配置，或先运行 configure-https.ps1。' }
$settings = Get-Content $configPath -Raw | ConvertFrom-Json
$secret = (Get-Content (Join-Path $dataDirectory 'https-password.dpapi') -Raw).Trim() | ConvertTo-SecureString
$pointer = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
try {
    $env:ASPNETCORE_URLS = "https://0.0.0.0:$($settings.Port);http://127.0.0.1:5080"
    $env:ClassroomServerUrl = "https://$($settings.ServerIp):$($settings.Port)"
    $env:ASPNETCORE_Kestrel__Certificates__Default__Path = Join-Path $dataDirectory 'teacher.pfx'
    $env:ASPNETCORE_Kestrel__Certificates__Default__Password = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $env:DataDirectory = $dataDirectory
    Write-Host '声域教师看板：http://127.0.0.1:5080（本机直接进入，无需密码）。'
    Write-Host "学生机连接地址：$env:ClassroomServerUrl"
    & (Join-Path $PSScriptRoot 'Noise.Server.exe')
} finally {
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    Remove-Item Env:ASPNETCORE_Kestrel__Certificates__Default__Password -ErrorAction SilentlyContinue
}

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$dataDirectory = Join-Path $PSScriptRoot 'data'
$configPath = Join-Path $dataDirectory 'https.json'
if (-not (Test-Path $configPath)) { throw 'Run configure-https.ps1 -ServerIp <teacher-IP> first.' }
$settings = Get-Content $configPath -Raw | ConvertFrom-Json
$secret = (Get-Content (Join-Path $dataDirectory 'https-password.dpapi') -Raw).Trim() | ConvertTo-SecureString
$pointer = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
try {
    $env:ASPNETCORE_URLS = "https://0.0.0.0:$($settings.Port)"
    $env:ASPNETCORE_Kestrel__Certificates__Default__Path = Join-Path $dataDirectory 'teacher.pfx'
    $env:ASPNETCORE_Kestrel__Certificates__Default__Password = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $env:DataDirectory = $dataDirectory
    Write-Host "RoomScope: https://$($settings.ServerIp):$($settings.Port)"
    Write-Host 'First-run teacher password: data\initial-password.txt'
    & (Join-Path $PSScriptRoot 'Noise.Server.exe')
} finally {
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    Remove-Item Env:ASPNETCORE_Kestrel__Certificates__Default__Password -ErrorAction SilentlyContinue
}

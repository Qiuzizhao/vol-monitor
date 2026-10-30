param(
    [Parameter(Mandatory=$true)][string]$ServerIp,
    [ValidateRange(1024,65535)][int]$Port = 5081
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console.ps1')
trap { Write-StartupError $_; exit 1 }
$parsedIp = $null
if (-not [System.Net.IPAddress]::TryParse($ServerIp, [ref]$parsedIp) -or $parsedIp.AddressFamily -ne 'InterNetwork') {
    throw '教师地址必须是本机的 IPv4 地址。'
}
$teacherDirectory = $PSScriptRoot
if (-not (Test-Path (Join-Path $teacherDirectory 'Noise.Server.exe'))) { throw '请在完整解压后的教师端目录中运行此脚本。' }
$dataDirectory = Join-Path $teacherDirectory 'data'
New-Item -ItemType Directory -Force $dataDirectory | Out-Null
# Restrict the credential and private-key directory to the account running the teacher service.
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner($identity)
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($rule)
Set-Acl -Path $dataDirectory -AclObject $acl
$certPath = Join-Path $dataDirectory 'teacher.pfx'
if (Test-Path $certPath) { throw '已配置连接证书；如需手动重新配置，请先备份并移走现有 teacher.pfx。' }
$certificate = New-SelfSignedCertificate -Type Custom -Subject "CN=RoomScope Teacher $ServerIp" `
    -TextExtension @("2.5.29.17={text}IPAddress=$ServerIp", '2.5.29.37={text}1.3.6.1.5.5.7.3.1') `
    -KeyAlgorithm RSA -KeyLength 2048 -KeyUsage DigitalSignature,KeyEncipherment `
    -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(1)
$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$secret = ConvertTo-SecureString ([Convert]::ToBase64String($bytes)) -AsPlainText -Force
Export-PfxCertificate -Cert $certificate -FilePath $certPath -Password $secret | Out-Null
ConvertFrom-SecureString $secret | Set-Content (Join-Path $dataDirectory 'https-password.dpapi')
Export-Certificate -Cert $certificate -FilePath (Join-Path $teacherDirectory 'roomscope-teacher.cer') | Out-Null
@{ ServerIp = $ServerIp; Port = $Port } | ConvertTo-Json | Set-Content (Join-Path $dataDirectory 'https.json')
Write-Host '连接已配置。'

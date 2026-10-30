param(
    [Parameter(Mandatory=$true)][string]$ServerIp,
    [ValidateRange(1024,65535)][int]$Port = 5081
)
$ErrorActionPreference = 'Stop'
$parsedIp = $null
if (-not [System.Net.IPAddress]::TryParse($ServerIp, [ref]$parsedIp) -or $parsedIp.AddressFamily -ne 'InterNetwork') {
    throw 'ServerIp must be the teacher computer IPv4 address.'
}
$teacherDirectory = $PSScriptRoot
if (-not (Test-Path (Join-Path $teacherDirectory 'Noise.Server.exe'))) { throw 'Run this script from the extracted teacher package.' }
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
if (Test-Path $certPath) { throw 'HTTPS is already configured. Back up and remove the existing teacher.pfx before reconfiguring.' }
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
Write-Host "HTTPS configured for https://${ServerIp}:${Port}"
Write-Host 'Distribute ONLY roomscope-teacher.cer to student computers and import it into CurrentUser\Root.'
Write-Host 'Keep the data directory and teacher.pfx private. Start with .\start-lan.ps1 under this Windows account.'
Write-Host "Allow inbound TCP $Port on the teacher computer Windows firewall for your classroom subnet."

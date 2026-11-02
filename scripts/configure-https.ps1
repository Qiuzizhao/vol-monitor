param(
    [Parameter(Mandatory=$true)][string]$ServerIp,
    [ValidateRange(1024,65535)][int]$Port = 5081,
    [switch]$AuthorityOnly
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
function New-CertificateSecret {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return ConvertTo-SecureString ([Convert]::ToBase64String($bytes)) -AsPlainText -Force
}
$authorityPath = Join-Path $dataDirectory 'authority.pfx'
$authorityPassword = Join-Path $dataDirectory 'authority-password.dpapi'
if (Test-Path -LiteralPath $authorityPath) {
    if (-not (Test-Path -LiteralPath $authorityPassword)) { throw '机房根证书密码文件丢失，请从备份恢复，避免更换学生端信任。' }
    $authoritySecret = (Get-Content -LiteralPath $authorityPassword -Raw).Trim() | ConvertTo-SecureString
    $authority = New-Object Security.Cryptography.X509Certificates.X509Certificate2($authorityPath,$authoritySecret,[Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet)
} else {
    if (Test-Path -LiteralPath $authorityPassword) { throw '机房根证书私钥丢失，请从备份恢复。' }
    $authority = New-SelfSignedCertificate -Type Custom -Subject ('CN=RoomScope Classroom ' + [guid]::NewGuid().ToString('N')) -KeyAlgorithm RSA -KeyLength 2048 `
        -KeyUsage CertSign,CRLSign -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0') -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(10)
    $authoritySecret = New-CertificateSecret
    Export-PfxCertificate -Cert $authority -FilePath $authorityPath -Password $authoritySecret | Out-Null
    ConvertFrom-SecureString $authoritySecret | Set-Content -LiteralPath $authorityPassword
}
if ($authority.NotAfter -lt (Get-Date).AddDays(32)) { throw '机房根证书即将到期，请先更新学生端信任后再更换根证书。' }
Export-Certificate -Cert $authority -FilePath (Join-Path $teacherDirectory 'roomscope-root.cer') -Force | Out-Null
if ($AuthorityOnly) { Write-Host '机房信任证书已准备，现有连接保持不变。'; return }
$certPath = Join-Path $dataDirectory 'teacher.pfx'
if (Test-Path $certPath) { throw '已配置连接证书；请通过正常启动入口更新。' }
$until = (Get-Date).AddYears(1)
if ($until -ge $authority.NotAfter) { $until=$authority.NotAfter.AddDays(-1) }
$certificate = New-SelfSignedCertificate -Type Custom -Subject "CN=RoomScope Teacher $ServerIp" `
    -TextExtension @("2.5.29.17={text}IPAddress=$ServerIp", '2.5.29.37={text}1.3.6.1.5.5.7.3.1') `
    -KeyAlgorithm RSA -KeyLength 2048 -KeyUsage DigitalSignature,KeyEncipherment `
    -CertStoreLocation 'Cert:\CurrentUser\My' -Signer $authority -NotAfter $until
$secret = New-CertificateSecret
Export-PfxCertificate -Cert $certificate -FilePath $certPath -Password $secret | Out-Null
ConvertFrom-SecureString $secret | Set-Content (Join-Path $dataDirectory 'https-password.dpapi')
Export-Certificate -Cert $certificate -FilePath (Join-Path $teacherDirectory 'roomscope-teacher.cer') | Out-Null
@{ ServerIp = $ServerIp; Port = $Port } | ConvertTo-Json | Set-Content (Join-Path $dataDirectory 'https.json')
Write-Host '连接已配置。'

$ErrorActionPreference = 'Stop'
try {
    $certificatePath = Join-Path $PSScriptRoot 'roomscope-teacher.cer'
    if (-not (Test-Path $certificatePath)) { throw 'Missing teacher certificate. Ask the teacher for the classroom student ZIP.' }
    if (-not (Test-Path (Join-Path $PSScriptRoot 'agent-deployment.json'))) { throw 'Missing classroom address. Ask the teacher for the classroom student ZIP.' }
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certificatePath)
    if ($certificate.NotAfter -le (Get-Date)) { throw 'Teacher certificate expired. Ask the teacher to renew the classroom package.' }
    if (-not (Test-Path "Cert:\CurrentUser\Root\$($certificate.Thumbprint)")) {
        Write-Host 'Installing the classroom teacher certificate for this Windows account. Confirm the Windows certificate prompt.'
        Import-Certificate -FilePath $certificatePath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
    }
    Start-Process -FilePath (Join-Path $PSScriptRoot 'Noise.Agent.exe') -WorkingDirectory $PSScriptRoot
} catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }

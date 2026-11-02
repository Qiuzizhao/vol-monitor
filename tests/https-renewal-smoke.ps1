# Exercise the real renewal script with simulated certificate-store cmdlets.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-cert-test-'+[guid]::NewGuid().ToString('N'))
$global:certificateFixture=@{ Roots=0; Leaves=0; Files=@{}; Root=$null }
$previousTrace=$env:RoomScopeStartupTrace
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    $env:RoomScopeStartupTrace=Join-Path $fixture 'trace.log'
    Copy-Item (Join-Path $repo 'scripts/configure-https.ps1'),(Join-Path $repo 'scripts/console.ps1') $fixture
    Set-Content (Join-Path $fixture 'Noise.Server.exe') 'fixture'
    function Set-Acl { param($Path,$AclObject) }
    function New-SelfSignedCertificate {
        param($Type,$Subject,$TextExtension,$KeyAlgorithm,$KeyLength,$KeyUsage,$CertStoreLocation,$NotAfter,$Signer)
        if ($Signer) {
            if ($Signer.Thumbprint -ne $global:certificateFixture.Root.Thumbprint) { throw 'Renewal changed the classroom authority.' }
            $global:certificateFixture.Leaves++
        } else { $global:certificateFixture.Roots++ }
        $cert=[pscustomobject]@{ Thumbprint=[guid]::NewGuid().ToString('N'); NotAfter=$NotAfter }
        if (-not $Signer) { $global:certificateFixture.Root=$cert }
        return $cert
    }
    function Export-PfxCertificate { param($Cert,$FilePath,$Password) Set-Content $FilePath $Cert.Thumbprint; $global:certificateFixture.Files[$FilePath]=$Cert }
    function Export-Certificate { param($Cert,$FilePath,[switch]$Force) Set-Content $FilePath $Cert.Thumbprint }
    function New-Object {
        param($TypeName,$ArgumentList)
        if ($TypeName -like '*X509Certificate2*') { return $global:certificateFixture.Files[$ArgumentList[0]] }
        Microsoft.PowerShell.Utility\New-Object @PSBoundParameters
    }
    $configure=Join-Path $fixture 'configure-https.ps1'
    & $configure -ServerIp '192.168.1.20' -AuthorityOnly
    if ($global:certificateFixture.Roots -ne 1 -or $global:certificateFixture.Leaves -ne 0) { throw 'Authority-only setup replaced a live certificate.' }
    & $configure -ServerIp '192.168.1.20'
    if ($LASTEXITCODE -eq 1) { throw ([IO.File]::ReadAllText($env:RoomScopeStartupTrace)) }
    $rootHash=(Get-FileHash (Join-Path $fixture 'roomscope-root.cer')).Hash
    $leafHash=(Get-FileHash (Join-Path $fixture 'data/teacher.pfx')).Hash
    & $configure -ServerIp '192.168.1.20' -AuthorityOnly
    if ((Get-FileHash (Join-Path $fixture 'data/teacher.pfx')).Hash -ne $leafHash) { throw 'An existing server certificate was replaced too early.' }
    Remove-Item -LiteralPath (Join-Path $fixture 'data/teacher.pfx')
    & $configure -ServerIp '192.168.1.20'
    if ($global:certificateFixture.Roots -ne 1 -or $global:certificateFixture.Leaves -ne 2 -or (Get-FileHash (Join-Path $fixture 'roomscope-root.cer')).Hash -ne $rootHash) { throw 'Renewal did not retain the classroom root.' }
    Write-Host '通过：首次创建机房根证书，准备信任不替换现有连接；两次连接证书使用同一根签发，未修改真实证书存储。'
} finally {
    $env:RoomScopeStartupTrace=$previousTrace
    Remove-Variable -Name certificateFixture -Scope Global -ErrorAction SilentlyContinue
    $resolved=[IO.Path]::GetFullPath($fixture)
    if ($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'RoomScope-cert-test-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

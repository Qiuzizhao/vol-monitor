param([string]$ServerIp, [ValidateRange(1024,65535)][int]$Port = 5081, [switch]$AtLogin)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$browserJob = $null
$staging = $null
$instance = $null
try {
    if (-not (Test-Path './Noise.Server.exe')) { throw 'Extract the teacher ZIP before starting.' }
    $settingsPath = Join-Path $PSScriptRoot 'data/https.json'
    $created = $false
    $instance = [Threading.Mutex]::new($true, ('Local\RoomScope.Teacher.' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value), [ref]$created)
    if (-not $created) {
        if (-not $AtLogin -and (Test-Path $settingsPath)) {
            $current = Get-Content $settingsPath -Raw | ConvertFrom-Json
            Start-Process "https://$($current.ServerIp):$($current.Port)"
        }
        return
    }
    $addresses = @(Get-NetIPConfiguration | Where-Object { $_.NetAdapter.Status -eq 'Up' } |
        ForEach-Object { foreach ($ip in $_.IPv4Address) {
            if ($ip.IPAddress -and $ip.IPAddress -notlike '169.254.*' -and $ip.IPAddress -ne '127.0.0.1') {
                [pscustomobject]@{ Address = $ip.IPAddress; Adapter = $_.InterfaceAlias; Gateway = [bool]$_.IPv4DefaultGateway }
            }
        } } | Sort-Object -Property @{Expression='Gateway';Descending=$true},Address -Unique)
    $settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { $null }
    $activeIps = @($addresses | ForEach-Object { $_.Address })
    if (-not $ServerIp -and $settings -and $settings.ServerIp -in $activeIps) { $ServerIp = $settings.ServerIp; $Port = [int]$settings.Port }
    if (-not $ServerIp) {
        if ($addresses.Count -eq 0) { throw 'Connect the teacher computer to the classroom network, then try again.' }
        if ($addresses.Count -eq 1) { $ServerIp = $addresses[0].Address }
        else {
            if ($AtLogin) { throw 'Network changed. Double-click start-teacher.cmd and choose the classroom network again.' }
            Write-Host 'Choose the classroom network:'
            for ($i = 0; $i -lt $addresses.Count; $i++) { Write-Host "[$($i + 1)] $($addresses[$i].Adapter) - $($addresses[$i].Address)" }
            $choice = 0
            if (-not [int]::TryParse((Read-Host 'Network number'), [ref]$choice) -or $choice -lt 1 -or $choice -gt $addresses.Count) { throw 'Invalid network number. Start again and choose a listed network.' }
            $ServerIp = $addresses[$choice - 1].Address
        }
    }
    if ($ServerIp -notin $activeIps) { throw 'The selected address does not belong to an active network on this computer.' }
    $certPath = Join-Path $PSScriptRoot 'roomscope-teacher.cer'
    $renew = -not $settings -or $settings.ServerIp -ne $ServerIp -or $settings.Port -ne $Port -or -not (Test-Path $certPath) -or
        -not (Test-Path (Join-Path $PSScriptRoot 'data/teacher.pfx')) -or -not (Test-Path (Join-Path $PSScriptRoot 'data/https-password.dpapi'))
    if (-not $renew) {
        $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certPath)
        $renew = $certificate.NotAfter -lt (Get-Date).AddDays(7)
    }
    if ($renew) {
        if (Get-Process -Name Noise.Server -ErrorAction SilentlyContinue) { throw 'Close the running teacher service before updating its network configuration.' }
        # Keep earlier private configuration for recovery; never include it in the student ZIP.
        $data = Join-Path $PSScriptRoot 'data'
        $backup = Join-Path $data ('https-backup-' + [guid]::NewGuid().ToString('N'))
        foreach ($file in @('teacher.pfx', 'https-password.dpapi', 'https.json')) {
            $path = Join-Path $data $file
            if (Test-Path $path) { New-Item -ItemType Directory -Force $backup | Out-Null; Move-Item -LiteralPath $path -Destination $backup }
        }
        & (Join-Path $PSScriptRoot 'configure-https.ps1') -ServerIp $ServerIp -Port $Port
    }
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certPath)
    if (-not (Test-Path "Cert:\CurrentUser\Root\$($certificate.Thumbprint)")) {
        Import-Certificate -FilePath $certPath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
    }
    $rule = Get-NetFirewallRule -Name "RoomScope-Teacher-$Port" -ErrorAction SilentlyContinue
    if (-not $rule -or $rule.Enabled -ne 'True') {
        Write-Host 'Windows will ask permission to allow classroom connections through the firewall.'
        try {
            $helper = Join-Path $PSScriptRoot 'open-firewall.ps1'
            $process = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $helper + '" -Port ' + $Port) -Wait -PassThru
            if ($process.ExitCode -ne 0) { throw 'Firewall setup failed.' }
        } catch { throw 'Classroom connections need a firewall rule. Restart and allow the Windows administrator prompt, or ask your school administrator to allow inbound TCP on the teacher port.' }
    }
    $url = "https://${ServerIp}:${Port}"
    $startupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $startupKey -Force | Out-Null
    $startupCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $PSScriptRoot 'start-teacher.ps1') + '" -AtLogin'
    New-ItemProperty -Path $startupKey -Name RoomScopeTeacher -Value $startupCommand -PropertyType String -Force | Out-Null
    $template = Join-Path $PSScriptRoot 'student-template.zip'
    if (-not (Test-Path $template)) { throw 'Missing student-template.zip. Re-extract the complete teacher package.' }
    $staging = Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $template -DestinationPath $staging
    @{ Server = $url; AutoEnroll = $true; ManagedMode = $true; EnableLoginStartup = $true } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $staging 'agent-deployment.json')
    Copy-Item -LiteralPath $certPath -Destination $staging
    $studentZip = Join-Path $PSScriptRoot 'RoomScope-classroom-student.zip'
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $studentZip -Force
    Write-Host "Student package ready: $studentZip" -ForegroundColor Green
    Write-Host 'Copy this ZIP to students. Extract it and double-click start-student.cmd.'
    Write-Host 'In the dashboard: Device management -> bulk enrollment -> open, then review seats and approve.'
    Write-Host 'Students must allow the microphone locally. Start a real experiment, then calibrate.'
    # Open the browser only after HTTPS responds. Password stays on this teacher computer.
    if (-not $AtLogin) { $browserJob = Start-Job -ArgumentList $url,$PSScriptRoot -ScriptBlock {
        param($url, $directory)
        for ($i = 0; $i -lt 60; $i++) {
            try {
                Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2 | Out-Null
                $passwordPath = Join-Path $directory 'data/initial-password.txt'
                if (Test-Path $passwordPath) { Start-Process notepad.exe -ArgumentList ('"' + $passwordPath + '"') }
                Start-Process $url
                break
            } catch { Start-Sleep -Seconds 1 }
        }
    } }
    & (Join-Path $PSScriptRoot 'start-lan.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Teacher service stopped with an error.' }
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    if ($AtLogin) { Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'RoomScope teacher startup failed') | Out-Null }
    exit 1
}
finally {
    if ($instance) { $instance.Dispose() }
    if ($browserJob) { Stop-Job $browserJob; Remove-Job $browserJob }
    if ($staging -and (Test-Path $staging)) {
        $resolved = [IO.Path]::GetFullPath($staging)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'RoomScope-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}

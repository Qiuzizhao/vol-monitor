# Simulate a setup step that fails once, then becomes available after permission
# takes effect. No certificates, firewall rules or startup entries are changed.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-launch-test-' + [guid]::NewGuid().ToString('N'))
$launcher = $null
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    Copy-Item (Join-Path $repo 'scripts/console.ps1') $fixture
    Copy-Item (Join-Path $repo 'scripts/startup-worker.ps1') $fixture
    $source = [IO.File]::ReadAllText((Join-Path $repo 'scripts/launch-ui.ps1'))
    $source = $source.Replace("`$logRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RoomScope/Startup'", "`$logRoot = Join-Path `$PSScriptRoot 'logs'")
    $source = $source.Replace('127.0.0.1:5080', '127.0.0.1:15981')
    $source = $source.Replace("Start-Process 'http://127.0.0.1:15981'", "[IO.File]::WriteAllText((Join-Path `$PSScriptRoot 'ready.txt'), 'ready')")
    [IO.File]::WriteAllText((Join-Path $fixture 'launch-ui.ps1'), $source, [Text.UTF8Encoding]::new($true))
    $worker = @'
$ErrorActionPreference = 'Stop'
Set-Content (Join-Path $PSScriptRoot 'worker.pid') $PID
$countFile = Join-Path $PSScriptRoot 'attempts.txt'
$count = if (Test-Path $countFile) { [int](Get-Content $countFile) + 1 } else { 1 }
Set-Content $countFile $count
if ($count -eq 1) { exit 1 }
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 15981)
$listener.Start()
try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        try {
            $bytes = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: 2`r`nConnection: close`r`n`r`n{}")
            $client.GetStream().Write($bytes, 0, $bytes.Length)
        } finally { $client.Dispose() }
    }
} finally { $listener.Stop() }
'@
    [IO.File]::WriteAllText((Join-Path $fixture 'start-teacher.ps1'), $worker, [Text.UTF8Encoding]::new($true))
    $launcher = Start-Process powershell.exe -WindowStyle Hidden -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $fixture 'launch-ui.ps1') + '" -Target teacher') -PassThru
    $null = $launcher.Handle
    if (-not $launcher.WaitForExit(25000)) { throw 'Launcher did not resume after the setup permission became available.' }
    if ($launcher.ExitCode -ne 0 -or -not (Test-Path (Join-Path $fixture 'ready.txt'))) { throw 'Launcher did not finish successfully.' }
    if ([int](Get-Content (Join-Path $fixture 'attempts.txt')) -ne 2) { throw 'Expected exactly one automatic retry.' }
    . (Join-Path $repo 'scripts/console.ps1')
    if ((Get-StartupRetryDelay 1 1223) -ne -1 -or (Get-StartupRetryDelay 3 1) -ne -1) { throw 'Cancellation or retry limit is not respected.' }
    Write-Host '通过：首次失败后自动重试，服务就绪后自动继续，取消授权不连续弹窗。'
} finally {
    if ($launcher -and -not $launcher.HasExited) { Stop-Process -Id $launcher.Id -ErrorAction SilentlyContinue }
    $workerPidFile = Join-Path $fixture 'worker.pid'
    if (Test-Path $workerPidFile) { Stop-Process -Id ([int](Get-Content $workerPidFile)) -ErrorAction SilentlyContinue }
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'RoomScope-launch-test-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

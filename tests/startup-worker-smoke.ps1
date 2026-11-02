$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-worker-test-' + [guid]::NewGuid().ToString('N') + ' 中文 空格')
New-Item -ItemType Directory -Path $fixture | Out-Null
Copy-Item (Join-Path $repo 'scripts/startup-worker.ps1') $fixture
try {
    $cases = @(
        @{ Script = "param([switch]`$FromLauncher)`r`nexit 1"; Code = 1; Expected = '启动脚本结束，退出码：1' },
        @{ Script = "param([switch]`$FromLauncher)`r`nthrow '模拟初始化失败'"; Code = 1; Expected = '模拟初始化失败' },
        @{ Script = "param([switch]`$FromLauncher)`r`nif ("; Code = 1; Expected = '启动脚本无法解析' },
        @{ Script = "param([switch]`$FromLauncher)`r`nWrite-Host '模拟启动成功'"; Code = 0; Expected = '模拟启动成功' }
    )
    foreach ($case in $cases) {
        [IO.File]::WriteAllText((Join-Path $fixture 'start-teacher.ps1'), $case.Script, [Text.UTF8Encoding]::new($true))
        $trace = Join-Path $fixture ('记录-' + [guid]::NewGuid().ToString('N') + '.log')
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $fixture 'startup-worker.ps1') + '" -Target teacher -TracePath "' + $trace + '"'
        $child = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -WindowStyle Hidden -ArgumentList $arguments -PassThru
        $null = $child.Handle
        if (-not $child.WaitForExit(15000)) { Stop-Process -Id $child.Id; throw '启动入口检查超时。' }
        if ($child.ExitCode -ne $case.Code) { throw '启动入口没有保留实际退出码。' }
        $content = Get-Content -LiteralPath $trace -Encoding UTF8 -Raw
        if ($content -notlike '*启动入口已执行*' -or $content -notlike ('*' + $case.Expected + '*')) { throw '启动入口丢失了关键错误记录。' }
    }
    Write-Host '通过：中文空格路径、静默退出、初始化异常和脚本语法错误均写入启动记录。'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'RoomScope-worker-test-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts/console.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-errors-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$outputPath = Join-Path $fixture 'output.log'
$errorPath = Join-Path $fixture 'error.log'
try {
    Set-Content -LiteralPath $outputPath -Encoding UTF8 -Value @('请先完整解压教师端运行包，再双击启动入口。', '', '')
    Set-Content -LiteralPath $errorPath -Encoding UTF8 -Value ''
    if ((Get-StartupFailureMessage $outputPath $errorPath 1) -notlike '请先完整解压*') { throw '末尾空行掩盖了实际错误。' }
    Set-Content -LiteralPath $outputPath -Encoding UTF8 -Value ''
    Set-Content -LiteralPath $errorPath -Encoding UTF8 -Value 'ParserError: Unexpected token'
    if ((Get-StartupFailureMessage $outputPath $errorPath 1) -notlike '启动脚本无法解析*') { throw '没有读取标准错误记录。' }
    Set-Content -LiteralPath $errorPath -Encoding UTF8 -Value 'UnauthorizedAccessException: Access is denied'
    if ((Get-StartupFailureMessage $outputPath $errorPath 1) -notlike '无法访问程序文件*') { throw '没有正确识别目录访问错误。' }
    Set-Content -LiteralPath $errorPath -Encoding UTF8 -Value ''
    if ((Get-StartupFailureMessage $outputPath $errorPath 7) -notlike '*退出码 7*') { throw '空记录时没有给出有效提示。' }
    Write-Host '通过：末尾空行、标准错误、文件权限和空记录均有明确提示，不误报为待授权。'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'RoomScope-errors-test-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

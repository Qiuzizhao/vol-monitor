$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts/stop-teacher.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw '关闭工具语法错误。' }
# Load only the process-selection functions; never run the actual shutdown code.
foreach ($function in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($function.Extent.Text))
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('RoomScope-stop-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    Set-Content -LiteralPath (Join-Path $fixture 'start-teacher.ps1') -Value 'RoomScopeTeacher'
    $teacher = 'powershell.exe -File "' + (Join-Path $fixture 'launch-ui.ps1') + '" -Target teacher -AtLogin'
    if (-not (Get-TeacherScript $teacher)) { throw '没有识别教师启动器。' }
    if (Get-TeacherScript ($teacher.Replace('-Target teacher', '-Target student'))) { throw '错误选中了学生端。' }
    if (Get-TeacherScript 'powershell.exe -NoProfile -Command Get-Date') { throw '错误选中了无关 PowerShell。' }
    if (Get-TeacherScript ('powershell.exe -File "' + (Join-Path $fixture 'unrelated.ps1') + '"')) { throw '错误选中了其他脚本。' }
    if (Test-TeacherDirectory ([IO.Path]::GetTempPath())) { throw '错误选中了其他程序目录。' }
    Write-Host '通过：关闭工具只识别本软件教师端，排除学生端和无关 PowerShell；未实际结束进程或修改自启。'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'RoomScope-stop-test-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

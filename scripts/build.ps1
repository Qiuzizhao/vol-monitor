param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console.ps1')
trap { Write-StartupError $_; exit 1 }
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$logDirectory = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $logDirectory | Out-Null
$buildLog = Join-Path $logDirectory 'build-output.log'
Set-Content -Encoding UTF8 -Path $buildLog -Value '声域运行包构建记录'
function Invoke-Checked([scriptblock]$Command) {
    Write-Host '正在执行构建步骤，请稍候……'
    & $Command *>> $buildLog
    if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE），详细记录：$buildLog" }
    Write-Host '步骤已完成。' -ForegroundColor Green
}
Invoke-Checked { dotnet test RoomScope.slnx -c Release --nologo }
if (Test-Path web/package-lock.json) { Invoke-Checked { npm ci --prefix web } }
else { Invoke-Checked { npm install --prefix web --package-lock=false } }
Invoke-Checked { npm run build --prefix web }
# Publish into a fresh directory so an earlier classroom configuration or device
# credential can never leak into the reusable student template.
$buildDirectory = Join-Path $root ('artifacts/build-' + [guid]::NewGuid().ToString('N'))
$teacher = Join-Path $buildDirectory 'teacher'
$student = Join-Path $buildDirectory 'student'
Invoke-Checked { dotnet publish src/Noise.Server -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $teacher }
Invoke-Checked { dotnet publish src/Noise.Agent -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $student }
Copy-Item scripts/start-local.cmd,scripts/start-lan.ps1,scripts/configure-https.ps1,scripts/start-teacher.cmd,scripts/start-teacher.ps1,scripts/open-firewall.ps1 $teacher
Copy-Item scripts/console.ps1 $teacher
Copy-Item scripts/start-student.cmd,scripts/start-student.ps1,scripts/console.ps1 $student
Copy-Item README.md $teacher
Copy-Item README.md $student
Copy-Item scripts/prepare-image.ps1,scripts/agent-deployment.example.json,IMAGE-DEPLOYMENT.md $student
Copy-Item IMAGE-DEPLOYMENT.md $teacher
Compress-Archive -Path "$student/*" -DestinationPath "artifacts/RoomScope-student-v0.3.2-$Runtime.zip" -Force
Copy-Item "artifacts/RoomScope-student-v0.3.2-$Runtime.zip" (Join-Path $teacher 'student-template.zip')
Compress-Archive -Path "$teacher/*" -DestinationPath "artifacts/RoomScope-teacher-v0.3.2-$Runtime.zip" -Force
Write-Host '运行包已生成，保存在 artifacts 目录。'

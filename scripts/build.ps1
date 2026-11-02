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
$teacherRoot = Join-Path $buildDirectory 'teacher'
$studentRoot = Join-Path $buildDirectory 'student'
$teacher = Join-Path $teacherRoot '程序'
$student = Join-Path $studentRoot '程序'
Invoke-Checked { dotnet publish src/Noise.Server -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $teacher }
Invoke-Checked { dotnet publish src/Noise.Agent -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $student }
Copy-Item scripts/start-lan.ps1,scripts/configure-https.ps1,scripts/start-teacher.ps1,scripts/open-firewall.ps1 $teacher
Copy-Item scripts/console.ps1,scripts/launch-ui.ps1,scripts/startup-worker.ps1 $teacher
Copy-Item scripts/start-student.ps1,scripts/console.ps1,scripts/launch-ui.ps1,scripts/startup-worker.ps1 $student
Copy-Item scripts/start-teacher.cmd (Join-Path $teacherRoot '启动教师端.cmd')
Copy-Item scripts/start-student.cmd (Join-Path $studentRoot '启动学生端.cmd')
Set-Content -Encoding UTF8 -Path (Join-Path $teacherRoot '使用说明.txt') -Value "1. 双击「启动教师端.cmd」，完成系统授权后自动打开管理页面。`r`n2. 页面点「学生机」→「生成学生端」，首次设置维护密码后下载安装包，发到学生机。`r`n3. 页面点「学生机」，核对自动填好的座位，点击「确认接入」。`r`n4. 接入后自动持续监测，首次使用保持安静约 10 秒完成自动校准。`r`n`r`n以后可在「学生机」→「维护密码」修改退出密码，对所有学生端立即生效。`r`n「程序」文件夹无需手动打开。请保留完整目录。"
Set-Content -Encoding UTF8 -Path (Join-Path $studentRoot '使用说明.txt') -Value '双击「启动学生端.cmd」，完成系统授权，等待教师批准。以后登录后自动收进右下角托盘。退出请右键托盘图标，选择退出并输入维护密码。「程序」文件夹无需手动打开，请保留完整目录。'
Copy-Item scripts/prepare-image.ps1,scripts/agent-deployment.example.json,IMAGE-DEPLOYMENT.md $student
Compress-Archive -Path "$studentRoot/*" -DestinationPath "artifacts/机房音量管理-学生端-v0.6.5-$Runtime.zip" -Force
Copy-Item "artifacts/机房音量管理-学生端-v0.6.5-$Runtime.zip" (Join-Path $teacher 'student-template.zip')
Compress-Archive -Path "$teacherRoot/*" -DestinationPath "artifacts/机房音量管理-教师端-v0.6.5-$Runtime.zip" -Force
Write-Host '运行包已生成，保存在 artifacts 目录。'

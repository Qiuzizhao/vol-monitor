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
Invoke-Checked { npm test --prefix web }
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
Copy-Item scripts/start-lan.ps1,scripts/configure-https.ps1,scripts/start-teacher.ps1,scripts/open-firewall.ps1,scripts/stop-teacher.ps1 $teacher
Copy-Item scripts/console.ps1,scripts/launch-ui.ps1,scripts/startup-worker.ps1 $teacher
Copy-Item scripts/start-student.ps1,scripts/install-student.ps1,scripts/console.ps1,scripts/launch-ui.ps1,scripts/startup-worker.ps1 $student
Copy-Item scripts/start-teacher.cmd (Join-Path $teacherRoot '启动教师端.cmd')
Copy-Item scripts/start-student.cmd (Join-Path $studentRoot '启动学生端.cmd')
Set-Content -Encoding UTF8 -Path (Join-Path $teacherRoot '使用说明.txt') -Value "1. 双击「启动教师端.cmd」，自动关闭旧版并打开管理页面；需要系统授权时点击允许。`r`n2. 页面点「学生机管理」→「生成学生端」，首次设置维护密码后下载安装包，发到学生机。`r`n3. 学生机按局域网 IP 自动加入，可在「学生机管理」调整座位，无需批准。`r`n4. 接入后自动持续监测，直接显示麦克风输入音量，无需校准。`r`n`r`n升级时解压到新文件夹，再双击同一启动入口。新版尚无配置时，沿用检测到的旧版数据和密码；旧目录保留，已有新版配置不会覆盖。`r`n以后可在「学生机管理」→「维护密码」修改退出密码，对所有学生端立即生效。`r`n「程序」文件夹无需手动打开。请保留完整目录。"
Set-Content -Encoding UTF8 -Path (Join-Path $studentRoot '使用说明.txt') -Value '双击「启动学生端.cmd」，允许管理员安装或升级，自动连接教师机。之后开机登录自动收进右下角托盘。真正退出请右键托盘图标，输入维护密码；连接不需要密码。安装完成后可移除解压目录。再次双击安装入口可重新运行。'
Copy-Item scripts/prepare-image.ps1,scripts/agent-deployment.example.json,IMAGE-DEPLOYMENT.md $student
Compress-Archive -Path "$studentRoot/*" -DestinationPath "artifacts/机房音量管理-学生端-v0.8.5-$Runtime.zip" -Force
Copy-Item "artifacts/机房音量管理-学生端-v0.8.5-$Runtime.zip" (Join-Path $teacher 'student-template.zip')
Compress-Archive -Path "$teacherRoot/*" -DestinationPath "artifacts/机房音量管理-教师端-v0.8.5-$Runtime.zip" -Force
Write-Host '运行包已生成，保存在 artifacts 目录。'

param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
function Invoke-Checked([scriptblock]$Command) { & $Command; if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE)." } }
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
Copy-Item scripts/start-student.cmd,scripts/start-student.ps1 $student
Copy-Item README.md $teacher
Copy-Item README.md $student
Copy-Item scripts/prepare-image.ps1,scripts/agent-deployment.example.json,IMAGE-DEPLOYMENT.md $student
Copy-Item IMAGE-DEPLOYMENT.md $teacher
Compress-Archive -Path "$student/*" -DestinationPath "artifacts/RoomScope-student-v0.3-$Runtime.zip" -Force
Copy-Item "artifacts/RoomScope-student-v0.3-$Runtime.zip" (Join-Path $teacher 'student-template.zip')
Compress-Archive -Path "$teacher/*" -DestinationPath "artifacts/RoomScope-teacher-v0.3-$Runtime.zip" -Force
Write-Host 'Packages created in artifacts/.'

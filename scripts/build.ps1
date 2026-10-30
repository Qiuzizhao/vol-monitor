param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
function Invoke-Checked([scriptblock]$Command) { & $Command; if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE)." } }
Invoke-Checked { dotnet test RoomScope.slnx -c Release --nologo }
Invoke-Checked { npm ci --prefix web }
Invoke-Checked { npm run build --prefix web }
$teacher = Join-Path $root 'artifacts/teacher'
$student = Join-Path $root 'artifacts/student'
Invoke-Checked { dotnet publish src/Noise.Server -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $teacher }
Invoke-Checked { dotnet publish src/Noise.Agent -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $student }
Copy-Item scripts/start-local.cmd,scripts/start-lan.ps1,scripts/configure-https.ps1 $teacher
Copy-Item README.md $teacher
Copy-Item README.md $student
Copy-Item scripts/prepare-image.ps1,scripts/agent-deployment.example.json,IMAGE-DEPLOYMENT.md $student
Copy-Item IMAGE-DEPLOYMENT.md $teacher
Compress-Archive -Path "$teacher/*" -DestinationPath "artifacts/RoomScope-teacher-v0.2-$Runtime.zip" -Force
Compress-Archive -Path "$student/*" -DestinationPath "artifacts/RoomScope-student-v0.2-$Runtime.zip" -Force
Write-Host 'Packages created in artifacts/.'

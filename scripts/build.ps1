param([string]$Dotnet = 'dotnet', [string]$Python = 'python', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
& $Python (Join-Path $PSScriptRoot 'generate_languages.py') --check
if ($LASTEXITCODE -ne 0) { throw 'Language catalog validation failed' }
$payload = Join-Path $repo 'payload'
$archive = Join-Path $repo '.downloads/esptool.zip'
$tool = Join-Path $payload 'tools/esptool.exe'
if (-not (Test-Path -LiteralPath $tool)) {
    New-Item -ItemType Directory -Force (Split-Path $archive), (Split-Path $tool) | Out-Null
    Invoke-WebRequest 'https://github.com/espressif/esptool/releases/download/v4.11.0/esptool-v4.11.0-windows-amd64.zip' -OutFile $archive
    if ((Get-FileHash $archive).Hash -ne '0EDAB659CCA62DF69C91A80CC685C1C3BBDEFB7EFF53ABF43C2B902A735AD2F9') { throw 'Espressif archive checksum mismatch' }
    Expand-Archive $archive (Join-Path $repo '.downloads/esptool') -Force
    Copy-Item (Join-Path $repo '.downloads/esptool/esptool-windows-amd64/esptool.exe') $tool
}
$presentmon = Join-Path $payload 'tools/PresentMon/PresentMon.exe'
if (-not (Test-Path -LiteralPath $presentmon)) {
    New-Item -ItemType Directory -Force (Split-Path $presentmon) | Out-Null
    Invoke-WebRequest 'https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe' -OutFile $presentmon
}
if ((Get-FileHash -LiteralPath $presentmon).Hash -ne 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF') { throw 'PresentMon checksum mismatch' }
& $Python (Join-Path $PSScriptRoot 'prepare-licenses.py')
if ($LASTEXITCODE -ne 0) { throw 'License bundle preparation failed' }
$board = Get-Content (Join-Path $payload 'board.json') -Raw | ConvertFrom-Json
if ((Get-FileHash $tool).Hash -ne $board.ToolSha256) { throw 'Flashing tool checksum mismatch' }
$catalog=Get-Content -LiteralPath (Join-Path $repo 'boards/catalog.json') -Raw | ConvertFrom-Json
foreach($model in $catalog.Boards){
    $profile=Get-Content -LiteralPath (Join-Path $payload ('boards/'+$model.Id+'.json')) -Raw | ConvertFrom-Json
    if($profile.Id -ne $model.Id -or $profile.Version -ne $board.Version -or $profile.FlashBytes -ne $model.FlashBytes){throw 'Incomplete or mixed-version board bundle; build firmware with -All'}
    if($profile.ToolSha256 -ne $board.ToolSha256){throw 'Inconsistent flash tool checksum'}
    foreach($part in $profile.Parts){if((Get-FileHash -LiteralPath (Join-Path $payload $part.File)).Hash -ne $part.Sha256){throw ('Firmware checksum mismatch: '+$part.File)}}
}
Compress-Archive -Path "$payload/*" -DestinationPath (Join-Path $repo 'companion/Payload.zip') -Force
if (-not $SkipTests) {
    & $Python (Join-Path $repo "tests/test_local_release.py")
    if ($LASTEXITCODE -ne 0) { throw "Release safety checks failed" }
    & $Python (Join-Path $repo "tests/test_languages.py")
    if ($LASTEXITCODE -ne 0) { throw "Language checks failed" }
    & $Dotnet run --project (Join-Path $repo 'companion/tests/ClawPal.Protocol.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Protocol checks failed' }
    & $Dotnet run --project (Join-Path $repo 'tests/ClawPal.Installer.Tests.csproj') -c Release -- (Join-Path $repo '.test-output')
    if ($LASTEXITCODE -ne 0) { throw 'Installer checks failed' }
}
& $Dotnet publish (Join-Path $repo 'companion/ClawPal.Companion.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o (Join-Path $repo 'dist')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item (Join-Path $repo 'dist/ClawPal.Companion.exe') (Join-Path $repo 'dist/ClawPal-Setup.exe') -Force
Write-Host 'Ready: dist/ClawPal-Setup.exe'

& (Join-Path $PSScriptRoot 'package-release.ps1')

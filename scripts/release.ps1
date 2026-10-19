# Build locally by default. -Publish explicitly uploads the complete release.
param(
    [switch]$Publish,
    [switch]$RepairCurrent,
    [switch]$UploadOnly,
    [string]$Python = 'python',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
# Optional machine-specific tool paths. This file is ignored by Git.
$localEnvironment = Join-Path $repo '.tools/release-env.ps1'
if (Test-Path -LiteralPath $localEnvironment) { . $localEnvironment }
$helper = Join-Path $PSScriptRoot 'local-release.py'
$env:PYTHONUTF8 = '1'
Push-Location $repo
try {
    if ($RepairCurrent) {
        if ($UploadOnly) { throw 'Choose RepairCurrent or UploadOnly, not both.' }
        $arguments = @($helper, 'repair')
        if ($Publish) { $arguments += '--publish' }
        & $Python @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Release repair did not finish.' }
        return
    }
    if (-not $UploadOnly) {
        $preflightArguments = @($helper, 'preflight')
        if ($Publish) { $preflightArguments += '--publish' }
        & $Python @preflightArguments
        if ($LASTEXITCODE -ne 0) { throw 'Release preflight failed.' }
        if (-not $env:IDF_PATH) { throw 'Open an ESP-IDF 5.5.2 shell first (see docs/UPDATES.md).' }
        # The default profile is a tracked generated file. Preserve its original
        # bytes so a build does not dirty an otherwise clean source checkout.
        $profile = Join-Path $repo 'payload/board.json'
        $profileBefore = [IO.File]::ReadAllBytes($profile)
        try {
            & $Python (Join-Path $PSScriptRoot 'build-firmware.py') --all
            if ($LASTEXITCODE -ne 0) { throw 'Firmware build failed.' }
            & $Python -m pip install --require-hashes -r (Join-Path $repo 'tests/simulator/requirements.txt')
            if ($LASTEXITCODE -ne 0) { throw 'Simulator compiler setup failed.' }
            & $Python (Join-Path $repo 'tests/firmware_host/run.py')
            if ($LASTEXITCODE -ne 0) { throw 'Firmware parser checks failed.' }
            & (Join-Path $PSScriptRoot 'test-ui.ps1') -Python $Python
            & (Join-Path $PSScriptRoot 'build.ps1') -Python $Python -Dotnet $Dotnet
        }
        finally { [IO.File]::WriteAllBytes($profile, $profileBefore) }
        & $Python $helper prepare
        if ($LASTEXITCODE -ne 0) { throw 'Release packaging failed.' }
    }
    if ($Publish -or $UploadOnly) {
        & $Python $helper publish
        if ($LASTEXITCODE -ne 0) { throw 'Upload stopped. Your local package is intact; retry with -UploadOnly.' }
    }
}
finally { Pop-Location }

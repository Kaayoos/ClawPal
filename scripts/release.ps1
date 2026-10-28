# Build and package locally by default. -Publish pushes the release tag; GitHub Actions
# (.github/workflows/release.yml) then builds, signs and publishes it.
param(
    [switch]$Publish,
    [switch]$RepairCurrent,
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
        $arguments = @($helper, 'repair')
        if ($Publish) { $arguments += '--publish' }
        & $Python @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Release repair did not finish.' }
        return
    }
    if ($Publish) {
        & $Python $helper tag
        if ($LASTEXITCODE -ne 0) { throw 'Release tag was not pushed.' }
        return
    }
    & $Python $helper preflight
    if ($LASTEXITCODE -ne 0) { throw 'Release preflight failed.' }
    if (-not $env:IDF_PATH) { throw 'Open an ESP-IDF 5.5.2 shell first (see docs/UPDATES.md).' }
    # The default profile is a tracked generated file. Preserve its original
    # bytes so a build does not dirty an otherwise clean source checkout.
    $profile = Join-Path $repo 'payload/board.json'
    $profileBefore = [IO.File]::ReadAllBytes($profile)
    try {
        # Signs for Wi-Fi updates only when CLAWPAL_SIGNING_KEY is set; GitHub signs releases.
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
finally { Pop-Location }

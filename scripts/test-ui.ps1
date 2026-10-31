# Run from an ESP-IDF 5.5.2 shell. -All is required for a release installer.
param([string]$Python='python')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
foreach($profile in @(0,4,6)){
    & $Python (Join-Path $repo 'tests/simulator/build.py') $profile
    if($LASTEXITCODE -ne 0){throw "Simulator build failed: $profile"}
    # The look is chosen at runtime, so one build renders every theme.
    foreach($theme in @('crab','studio')){
        $frames=Join-Path $repo ".sim-output/$profile/frames/$theme"
        New-Item -ItemType Directory -Force -Path $frames | Out-Null
        & (Join-Path $repo ".sim-output/$profile/clawpal_sim.exe") $frames $theme
        if($LASTEXITCODE -ne 0){throw "Display interaction check failed: $profile $theme"}
    }
}
& $Python (Join-Path $repo 'tests/simulator/contact_sheet.py')
if($LASTEXITCODE -ne 0){throw 'Contact sheet failed'}

# Run from an ESP-IDF 5.5.2 shell. -All is required for a release installer.
param([string]$Python='python')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
foreach($profile in @(0,4,6)){
    & $Python (Join-Path $repo 'tests/simulator/build.py') $profile
    if($LASTEXITCODE -ne 0){throw "Simulator build failed: $profile"}
    # The look is chosen at runtime, so one build renders every theme.
    foreach($theme in @('crab','studio','crabv2','crastudio')){
        $frames=Join-Path $repo ".sim-output/$profile/frames/$theme"
        New-Item -ItemType Directory -Force -Path $frames | Out-Null
        & (Join-Path $repo ".sim-output/$profile/clawpal_sim.exe") $frames $theme
        if($LASTEXITCODE -ne 0){throw "Display interaction check failed: $profile $theme"}
        # The Printer app runs on its own so the frames above stay comparable with older builds.
        $printer=Join-Path $repo ".sim-output/$profile/frames/$theme-printer"
        New-Item -ItemType Directory -Force -Path $printer | Out-Null
        & (Join-Path $repo ".sim-output/$profile/clawpal_sim.exe") $printer $theme - printer
        if($LASTEXITCODE -ne 0){throw "Printer app check failed: $profile $theme"}
        # The Lyrics view runs on its own, so the main run's frames keep their timing.
        $lyrics=Join-Path $repo ".sim-output/$profile/frames-lyrics/$theme"
        New-Item -ItemType Directory -Force -Path $lyrics | Out-Null
        & (Join-Path $repo ".sim-output/$profile/clawpal_sim.exe") $lyrics $theme - lyrics
        if($LASTEXITCODE -ne 0){throw "Lyrics check failed: $profile $theme"}
    }
    # The cased looks again with their case left off.
    foreach($theme in @('crabv2','crastudio')){
        $frames=Join-Path $repo ".sim-output/$profile/frames/$theme-noframe"
        New-Item -ItemType Directory -Force -Path $frames | Out-Null
        & (Join-Path $repo ".sim-output/$profile/clawpal_sim.exe") $frames $theme champagne noframe
        if($LASTEXITCODE -ne 0){throw "Display interaction check failed: $profile $theme without its case"}
    }
}
& $Python (Join-Path $repo 'tests/simulator/contact_sheet.py')
if($LASTEXITCODE -ne 0){throw 'Contact sheet failed'}

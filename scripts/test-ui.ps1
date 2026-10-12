param([string]$Python='python')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
foreach($profile in @(0,4,6)){
    & $Python (Join-Path $repo 'tests/simulator/build.py') $profile
    if($LASTEXITCODE -ne 0){throw "Simulator build failed: $profile"}
    $frames=Join-Path $repo ".sim-output/$profile/frames"
    New-Item -ItemType Directory -Force -Path $frames | Out-Null
    & (Join-Path $repo ".sim-output/$profile/clawpal_sim.exe") $frames
    if($LASTEXITCODE -ne 0){throw "Display interaction check failed: $profile"}
}

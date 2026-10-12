# Run from an ESP-IDF 5.5.2 shell. -All is required for a release installer.
param([string]$Board='waveshare-esp32-s3-touch-lcd-4.3b',[switch]$All)
$ErrorActionPreference='Stop'
$arguments=@((Join-Path $PSScriptRoot 'build-firmware.py'))
if($All){$arguments+='--all'}else{$arguments+=@('--board',$Board)}
python @arguments
if($LASTEXITCODE -ne 0){throw 'Firmware build or packaging failed'}

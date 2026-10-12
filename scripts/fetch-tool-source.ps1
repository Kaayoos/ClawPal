# Distribute these exact upstream source archives alongside the binary release.
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$out=Join-Path $repo 'dist/sources'
New-Item -ItemType Directory -Force $out | Out-Null
Invoke-WebRequest 'https://github.com/espressif/esptool/archive/refs/tags/v4.11.0.tar.gz' -OutFile (Join-Path $out 'esptool-v4.11.0.tar.gz')
Invoke-WebRequest 'https://github.com/GameTechDev/PresentMon/archive/refs/tags/v2.6.0.tar.gz' -OutFile (Join-Path $out 'PresentMon-v2.6.0.tar.gz')
Invoke-WebRequest 'https://github.com/espressif/esptool-legacy-flasher-stub/archive/refs/tags/v1.11.1.tar.gz' -OutFile (Join-Path $out 'esptool-legacy-flasher-stub-v1.11.1.tar.gz')
Invoke-WebRequest 'https://github.com/esp-rs/esp-flasher-stub/archive/refs/tags/v0.3.0.tar.gz' -OutFile (Join-Path $out 'esp-flasher-stub-v0.3.0.tar.gz')
Get-ChildItem -LiteralPath $out -File | Where-Object Name -Like '*.tar.gz' | ForEach-Object {
    ((Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()+'  '+$_.Name) | Set-Content -LiteralPath ($_.FullName+'.sha256') -Encoding ascii
}
Write-Host 'Matching tool source archives prepared.'

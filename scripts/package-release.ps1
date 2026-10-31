param([string]$Version)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$catalog=Get-Content -LiteralPath (Join-Path $repo 'boards/catalog.json') -Raw | ConvertFrom-Json
$default=Get-Content -LiteralPath (Join-Path $repo 'payload/board.json') -Raw | ConvertFrom-Json
if($Version -and $Version.TrimStart('v') -ne $default.Version){throw 'Release tag does not match the bundled firmware version'}
$dist=Join-Path $repo 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
# Signed LAN requests and signed firmware images started with 0.10.0: a display running
# them refuses older companions, so those update first. Later releases share that floor.
# Themes moved display settings to schema 7 in 0.11.0; older companions cannot read them.
$minCompanion=if([version]$default.Version -ge [version]'0.11.0'){'0.11.0'}elseif([version]$default.Version -ge [version]'0.10.0'){'0.10.0'}else{'0.9.0'}
$releases=@()
foreach($model in $catalog.Boards){
 $board=Get-Content -LiteralPath (Join-Path $repo ('payload/boards/'+$model.Id+'.json')) -Raw | ConvertFrom-Json
 if($board.Id -ne $model.Id -or $board.Version -ne $default.Version){throw 'Missing or stale board firmware'}
 $part=@($board.Parts | Where-Object { $_.File.EndsWith('/clawpal.bin') })
 if($part.Count -ne 1){throw 'Expected exactly one application image'}
 $image=Join-Path $repo ('payload/'+$part[0].File)
 $hash=(Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash.ToLowerInvariant()
 if($hash -ne $part[0].Sha256.ToLowerInvariant()){throw 'Firmware hash does not match the board manifest'}
 $length=(Get-Item -LiteralPath $image).Length
 if($length -gt $model.MaxBytes){throw 'Firmware exceeds its OTA slot'}
 Copy-Item -LiteralPath $image -Destination (Join-Path $dist $model.AssetName) -Force
 $releases+= [ordered]@{schema=1;version=$board.Version;board=$board.Id;layout=$model.Layout;min_companion=$minCompanion;firmware=[ordered]@{file=$model.AssetName;bytes=$length;sha256=$hash}}
}
$manifest=[ordered]@{schema=2;version=$default.Version;boards=$releases}
$manifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $dist 'clawpal-release.json') -Encoding utf8
$setup=Join-Path $dist 'ClawPal-Setup.exe'
if(-not(Test-Path -LiteralPath $setup)){throw 'Build ClawPal-Setup.exe before packaging a release'}
if([Version](Get-Item -LiteralPath $setup).VersionInfo.FileVersion -ne [Version]($default.Version+'.0')){throw 'Installer and firmware versions differ'}
Compress-Archive -Path (Join-Path $repo 'payload/licenses/*') -DestinationPath (Join-Path $dist 'ClawPal-licenses.zip') -Force
((Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant()+'  ClawPal-Setup.exe') | Set-Content -LiteralPath (Join-Path $dist 'ClawPal-Setup.exe.sha256') -Encoding ascii
Write-Host ('Complete multi-board release prepared: '+$default.Version)

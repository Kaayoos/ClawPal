# Authenticode-sign a Windows executable when a code-signing identity is configured.
# Without one, the file stays unsigned and Windows SmartScreen warns on first launch.
#   CLAWPAL_SIGN_THUMBPRINT   SHA-1 thumbprint of a code-signing certificate in the
#                             CurrentUser\My store (works with EV hardware tokens), or
#   CLAWPAL_SIGN_DLIB + CLAWPAL_SIGN_METADATA
#                             Azure Trusted Signing dlib and its metadata.json.
#   CLAWPAL_SIGNTOOL          optional signtool.exe path (default: newest Windows SDK copy)
#   CLAWPAL_SIGN_TIMESTAMP    optional RFC 3161 server (default matches the identity type)
# -Require fails instead of leaving the file unsigned.
param([Parameter(Mandatory)][string]$Path, [switch]$Require)
$ErrorActionPreference = 'Stop'
$thumbprint = $env:CLAWPAL_SIGN_THUMBPRINT
$dlib = $env:CLAWPAL_SIGN_DLIB
if (-not $thumbprint -and -not $dlib) {
    if ($Require) { throw 'Code signing is required: set CLAWPAL_SIGN_THUMBPRINT or CLAWPAL_SIGN_DLIB (see docs/UPDATES.md).' }
    Write-Warning "No code-signing identity configured; $(Split-Path -Leaf $Path) stays unsigned."
    return
}
if ($dlib) {
    if (-not $env:CLAWPAL_SIGN_METADATA) { throw 'CLAWPAL_SIGN_METADATA must point to the Trusted Signing metadata.json.' }
    $timestamp = if ($env:CLAWPAL_SIGN_TIMESTAMP) { $env:CLAWPAL_SIGN_TIMESTAMP } else { 'http://timestamp.acs.microsoft.com' }
    $identity = @('/dlib', $dlib, '/dmdf', $env:CLAWPAL_SIGN_METADATA)
} else {
    if ($thumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'CLAWPAL_SIGN_THUMBPRINT must be a 40-character SHA-1 thumbprint.' }
    $timestamp = if ($env:CLAWPAL_SIGN_TIMESTAMP) { $env:CLAWPAL_SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }
    $identity = @('/sha1', $thumbprint, '/s', 'My')
}
$signtool = $env:CLAWPAL_SIGNTOOL
if (-not $signtool) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $signtool = Get-ChildItem -LiteralPath $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool -or -not (Test-Path -LiteralPath $signtool)) { throw 'signtool.exe not found; install the Windows SDK or set CLAWPAL_SIGNTOOL.' }
& $signtool sign /fd SHA256 /tr $timestamp /td SHA256 /d 'ClawPal' /du 'https://github.com/Kaayoos/clawpal' @identity $Path
if ($LASTEXITCODE -ne 0) { throw "Code signing failed: $Path" }
& $signtool verify /pa /q $Path
if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $Path" }
Write-Host "Signed $(Split-Path -Leaf $Path)"

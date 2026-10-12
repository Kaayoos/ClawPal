param([string]$SourceSheet = (Join-Path $PSScriptRoot 'crab_idle_sheet.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Preserve the supplied pixel art. Only transparent padding is recentered;
# every painted pixel comes directly from the first 48 x 48 idle frame.
$source = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $SourceSheet).Path)
try {
    if ($source.Width -ne 384 -or $source.Height -ne 288) { throw 'Expected the original 384 x 288 crab idle sprite sheet.' }
    $left = 48; $top = 48; $right = -1; $bottom = -1
    for ($y = 0; $y -lt 48; $y++) {
        for ($x = 0; $x -lt 48; $x++) {
            if ($source.GetPixel($x, $y).A -gt 0) {
                $left = [Math]::Min($left, $x); $top = [Math]::Min($top, $y)
                $right = [Math]::Max($right, $x); $bottom = [Math]::Max($bottom, $y)
            }
        }
    }
    if ($right -lt $left) { throw 'The first idle frame is empty.' }
    $width = $right - $left + 1; $height = $bottom - $top + 1
    $offsetX = [int][Math]::Floor((48 - $width) / 2)
    $offsetY = [int][Math]::Floor((48 - $height) / 2)
    $frame = [System.Drawing.Bitmap]::new(48, 48, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        for ($y = 0; $y -lt $height; $y++) {
            for ($x = 0; $x -lt $width; $x++) {
                $frame.SetPixel($x + $offsetX, $y + $offsetY, $source.GetPixel($x + $left, $y + $top))
            }
        }
        $frame.Save((Join-Path $PSScriptRoot 'crab-tray.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        $images = @()
        foreach ($size in @(16, 24, 32, 48)) {
            $scaled = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $memory = [IO.MemoryStream]::new()
            try {
                for ($y = 0; $y -lt $size; $y++) {
                    for ($x = 0; $x -lt $size; $x++) {
                        $sampleX = [int][Math]::Floor(($x + 0.5) * 48 / $size)
                        $sampleY = [int][Math]::Floor(($y + 0.5) * 48 / $size)
                        $scaled.SetPixel($x, $y, $frame.GetPixel($sampleX, $sampleY))
                    }
                }
                $scaled.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
                $images += [pscustomobject]@{ Size=$size; Bytes=$memory.ToArray() }
            }
            finally { $memory.Dispose(); $scaled.Dispose() }
        }
        $stream = [IO.File]::Create((Join-Path $PSScriptRoot 'BuddyOS.ico'))
        $writer = [IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
            $offset = 6 + 16 * $images.Count
            foreach ($entry in $images) {
                $writer.Write([byte]$entry.Size); $writer.Write([byte]$entry.Size)
                $writer.Write([byte]0); $writer.Write([byte]0)
                $writer.Write([uint16]1); $writer.Write([uint16]32)
                $writer.Write([uint32]$entry.Bytes.Length); $writer.Write([uint32]$offset)
                $offset += $entry.Bytes.Length
            }
            foreach ($entry in $images) { $writer.Write([byte[]]$entry.Bytes) }
        }
        finally { $writer.Dispose(); $stream.Dispose() }
    }
    finally { $frame.Dispose() }
}
finally { $source.Dispose() }
Write-Output 'Created BuddyOS.ico with transparent 16, 24, 32 and 48 px nearest-neighbor crab icons.'

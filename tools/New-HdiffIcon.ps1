[CmdletBinding()]
param(
    [string]$Source,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSCommandPath
if ([string]::IsNullOrWhiteSpace($Source)) { $Source = Join-Path $scriptRoot '..\assets\hdiff-icon-source.png' }
if ([string]::IsNullOrWhiteSpace($Destination)) { $Destination = Join-Path $scriptRoot '..\assets\Hdiff.ico' }
Add-Type -AssemblyName System.Drawing

function Draw-CompactHd([System.Drawing.Graphics]$graphics, [int]$size) {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
    $unit = $size / 16.0
    $navy = [System.Drawing.Color]::FromArgb(255, 20, 33, 50)
    $white = [System.Drawing.Color]::White
    $blue = [System.Drawing.Color]::FromArgb(255, 49, 166, 213)
    $background = New-Object System.Drawing.SolidBrush($navy)
    $hBrush = New-Object System.Drawing.SolidBrush($white)
    $dBrush = New-Object System.Drawing.SolidBrush($blue)
    try {
        $graphics.FillRectangle($background, 0, 0, $size, $size)
        $rect = {
            param([double]$x, [double]$y, [double]$width, [double]$height)
            [System.Drawing.Rectangle]::FromLTRB(
                [int][Math]::Floor($x * $unit),
                [int][Math]::Floor($y * $unit),
                [int][Math]::Ceiling(($x + $width) * $unit),
                [int][Math]::Ceiling(($y + $height) * $unit))
        }

        # H: three simple strokes, deliberately readable at 16px.
        $graphics.FillRectangle($hBrush, (& $rect 2 3 2 10))
        $graphics.FillRectangle($hBrush, (& $rect 6 3 2 10))
        $graphics.FillRectangle($hBrush, (& $rect 2 7 6 2))
        # D: cyan outline on the right-hand document panel.
        $graphics.FillRectangle($dBrush, (& $rect 10 3 2 10))
        $graphics.FillRectangle($dBrush, (& $rect 11 3 3 2))
        $graphics.FillRectangle($dBrush, (& $rect 11 11 3 2))
        $graphics.FillRectangle($dBrush, (& $rect 13 5 1 6))
    }
    finally {
        $background.Dispose()
        $hBrush.Dispose()
        $dBrush.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $Source)) {
    throw "Icon source image not found: $Source"
}

$sourceImage = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source))
try {
    $frames = foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            if ($size -le 32) {
                # The title bar normally requests 16px. Use hand-drawn, high-contrast glyphs
                # rather than shrinking the detailed logo into an unreadable shape.
                Draw-CompactHd $graphics $size
            }
            else {
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $radius = [Math]::Max(2, [int]($size * 0.12))
                $diameter = $radius * 2
                $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
                $path.AddArc($size - $diameter, 0, $diameter, $diameter, 270, 90)
                $path.AddArc($size - $diameter, $size - $diameter, $diameter, $diameter, 0, 90)
                $path.AddArc(0, $size - $diameter, $diameter, $diameter, 90, 90)
                $path.CloseFigure()
                $graphics.SetClip($path)
                $graphics.DrawImage($sourceImage, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
            }

            $memory = New-Object System.IO.MemoryStream
            try {
                $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
                [PSCustomObject]@{ Size = $size; Data = $memory.ToArray() }
            }
            finally {
                $memory.Dispose()
            }
        }
        finally {
            $path.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }

    $destinationDirectory = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    $stream = [System.IO.File]::Open($Destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$frames.Count)
        $offset = 6 + (16 * $frames.Count)
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$frame.Data.Length)
            $writer.Write([UInt32]$offset)
            $offset += $frame.Data.Length
        }
        foreach ($frame in $frames) { $writer.Write($frame.Data) }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}
finally {
    $sourceImage.Dispose()
}

Write-Host "Created $Destination"

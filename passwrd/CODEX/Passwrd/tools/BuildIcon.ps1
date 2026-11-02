$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $projectRoot 'Assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc(
        $Rectangle.Right - $diameter,
        $Rectangle.Bottom - $diameter,
        $diameter,
        $diameter,
        0,
        90)
    $path.AddArc($Rectangle.X, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-MasterBitmap {
    $bitmap = [System.Drawing.Bitmap]::new(
        1024,
        1024,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

        $outerRect = [System.Drawing.RectangleF]::new(48, 48, 928, 928)
        $outerPath = New-RoundedRectanglePath -Rectangle $outerRect -Radius 224
        $outerBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            $outerRect,
            [System.Drawing.Color]::FromArgb(255, 20, 43, 53),
            [System.Drawing.Color]::FromArgb(255, 8, 23, 30),
            45)
        $graphics.FillPath($outerBrush, $outerPath)
        $outerBorder = [System.Drawing.Pen]::new(
            [System.Drawing.Color]::FromArgb(255, 54, 87, 101),
            10)
        $graphics.DrawPath($outerBorder, $outerPath)

        $keyRect = [System.Drawing.RectangleF]::new(144, 144, 736, 736)
        $keyPath = New-RoundedRectanglePath -Rectangle $keyRect -Radius 184
        $keyBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            $keyRect,
            [System.Drawing.Color]::FromArgb(255, 22, 180, 179),
            [System.Drawing.Color]::FromArgb(255, 8, 120, 134),
            45)
        $graphics.FillPath($keyBrush, $keyPath)
        $keyBorder = [System.Drawing.Pen]::new(
            [System.Drawing.Color]::FromArgb(70, 214, 255, 255),
            6)
        $graphics.DrawPath($keyBorder, $keyPath)

        $highlight = [System.Drawing.Pen]::new(
            [System.Drawing.Color]::FromArgb(82, 220, 255, 255),
            20)
        $highlight.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $highlight.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawBezier($highlight, 196, 216, 236, 182, 284, 168, 332, 168)

        $font = [System.Drawing.Font]::new(
            'Segoe UI',
            660,
            [System.Drawing.FontStyle]::Bold,
            [System.Drawing.GraphicsUnit]::Pixel)
        $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $textFormat = [System.Drawing.StringFormat]::new()
        $textFormat.Alignment = [System.Drawing.StringAlignment]::Center
        $textFormat.LineAlignment = [System.Drawing.StringAlignment]::Center
        $textFormat.FormatFlags = [System.Drawing.StringFormatFlags]::NoClip
        $textRect = [System.Drawing.RectangleF]::new(198, 96, 570, 652)
        $graphics.DrawString('P', $font, $textBrush, $textRect, $textFormat)

        $caret = [System.Drawing.Pen]::new(
            [System.Drawing.Color]::FromArgb(255, 216, 255, 255),
            56)
        $caret.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $caret.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($caret, 816, 552, 816, 772)
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Save-ScaledPng {
    param(
        [System.Drawing.Bitmap]$Master,
        [int]$Size,
        [string]$Path
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.DrawImage($Master, 0, 0, $Size, $Size)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Write-Ico {
    param(
        [System.Drawing.Bitmap]$Master,
        [string]$Path
    )

    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $images = @()

    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($Master, 0, 0, $size, $size)
            $stream = [System.IO.MemoryStream]::new()
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $images += [pscustomobject]@{
                Size = $size
                Bytes = $stream.ToArray()
            }
            $stream.Dispose()
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }

    $fileStream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write)
    $writer = [System.IO.BinaryWriter]::new($fileStream)

    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$images.Count)

        [uint32]$offset = 6 + (16 * $images.Count)
        foreach ($image in $images) {
            $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$image.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $image.Bytes.Length
        }

        foreach ($image in $images) {
            $writer.Write($image.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $fileStream.Dispose()
    }
}

$master = New-MasterBitmap

try {
    $master.Save(
        (Join-Path $assetDirectory 'Passwrd-1024.png'),
        [System.Drawing.Imaging.ImageFormat]::Png)
    Save-ScaledPng -Master $master -Size 256 -Path (Join-Path $assetDirectory 'Passwrd.png')
    Write-Ico -Master $master -Path (Join-Path $assetDirectory 'Passwrd.ico')
}
finally {
    $master.Dispose()
}

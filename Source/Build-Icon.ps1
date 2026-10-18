# Build-time only. The app loads the embedded icon once, with no drawing timer.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = @()
$rows = @('10001','11011','10101','10001','10001')
foreach ($size in $sizes) {
    $bmp = New-Object Drawing.Bitmap ($size, $size)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([Drawing.Color]::Transparent)
    $g.ScaleTransform(($size/64.0), ($size/64.0))
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(1,1,22,22,180,90)
    $path.AddArc(41,1,22,22,270,90)
    $path.AddArc(41,41,22,22,0,90)
    $path.AddArc(1,41,22,22,90,90)
    $path.CloseFigure()
    $tile = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255,20,20,21))
    $white = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255,241,241,237))
    $red = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255,239,52,55))
    $edge = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255,85,85,86)), 1
    $g.FillPath($tile,$path)
    $g.DrawPath($edge,$path)
    for ($row=0; $row -lt 5; $row++) {
        for ($col=0; $col -lt 5; $col++) {
            if ($rows[$row][$col] -eq '1') {
                $brush = $white
                if ($row -eq 2 -and $col -eq 2) { $brush = $red }
                $g.FillEllipse($brush, [single](8+10*$col), [single](8+10*$row), [single]8, [single]8)
            }
        }
    }
    $stream = New-Object IO.MemoryStream
    if ($size -le 64) {
        # Uncompressed DIB frames work with the .NET Framework small-icon
        # decoder as well as Explorer; reserve PNG compression for big icons.
        $frameWriter = New-Object IO.BinaryWriter ($stream)
        $maskStride = [int]([Math]::Ceiling($size/32.0)*4)
        $frameWriter.Write([uint32]40); $frameWriter.Write([int32]$size); $frameWriter.Write([int32]($size*2))
        $frameWriter.Write([uint16]1); $frameWriter.Write([uint16]32); $frameWriter.Write([uint32]0)
        $frameWriter.Write([uint32]($size*$size*4+$maskStride*$size))
        1..4 | ForEach-Object { $frameWriter.Write([uint32]0) }
        for ($y=$size-1; $y -ge 0; $y--) {
            for ($x=0; $x -lt $size; $x++) {
                $pixel = $bmp.GetPixel($x,$y)
                $frameWriter.Write([byte]$pixel.B); $frameWriter.Write([byte]$pixel.G)
                $frameWriter.Write([byte]$pixel.R); $frameWriter.Write([byte]$pixel.A)
            }
        }
        for ($y=$size-1; $y -ge 0; $y--) {
            $mask = New-Object byte[] $maskStride
            for ($x=0; $x -lt $size; $x++) {
                if ($bmp.GetPixel($x,$y).A -eq 0) {
                    $index = [int][Math]::Floor($x/8.0)
                    $mask[$index] = [byte]($mask[$index] -bor (128 -shr ($x%8)))
                }
            }
            $frameWriter.Write([byte[]]$mask)
        }
        $frameWriter.Flush()
    } else {
        $bmp.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    }
    $frames += ,$stream.ToArray()
    if ($size -eq 256) {
        $previewRoot = Join-Path $PSScriptRoot '..\Pratinjau'
        New-Item -ItemType Directory -Path $previewRoot -Force | Out-Null
        $bmp.Save((Join-Path $previewRoot 'MiniMonitor-logo.png'),[Drawing.Imaging.ImageFormat]::Png)
    }
    if ($size -le 64) { $frameWriter.Dispose() }
    $stream.Dispose(); $edge.Dispose(); $tile.Dispose(); $white.Dispose(); $red.Dispose(); $path.Dispose(); $g.Dispose(); $bmp.Dispose()
}
$file = [IO.File]::Create((Join-Path $PSScriptRoot 'MiniMonitor.ico'))
$writer = New-Object IO.BinaryWriter ($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16*$sizes.Count
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $dimension = $sizes[$i] % 256
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $file.Dispose() }

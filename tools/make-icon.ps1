Add-Type -AssemblyName PresentationCore, WindowsBase

$dir = Join-Path $PSScriptRoot "..\src\TiaMc.App\Assets"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

function New-TiaIconBitmap([int]$size) {
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()

    $petrol = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x0C, 0x5C, 0x8C))
    $dark = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x08, 0x45, 0x6A))
    $orange = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0xE8, 0xA3, 0x3D))
    $white = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0xFF, 0xFF, 0xFF))
    $white2 = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(0xD0, 0xFF, 0xFF, 0xFF))
    $white3 = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(0xA0, 0xFF, 0xFF, 0xFF))
    $cyan = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x7E, 0xC8, 0xF0))

    $k = [double]$size / 256.0

    function Rect1([double]$x, [double]$y, [double]$w, [double]$h) {
        New-Object System.Windows.Rect(($x * $script:k), ($y * $script:k), ($w * $script:k), ($h * $script:k))
    }
    $script:k = $k

    $dc.DrawRectangle($petrol, $null, (Rect1 0 0 256 256))
    $dc.DrawRectangle($dark, $null, (Rect1 0 0 256 26))
    $dc.DrawRectangle($orange, $null, (Rect1 14 14 70 228))
    $dc.DrawRectangle($white, $null, (Rect1 100 58 140 30))
    $dc.DrawRectangle($white2, $null, (Rect1 100 102 108 22))
    $dc.DrawRectangle($white3, $null, (Rect1 100 138 124 22))
    # isometric "instance" cube
    $p1 = New-Object System.Windows.Point((112 * $k), (196 * $k))
    $p2 = New-Object System.Windows.Point((148 * $k), (178 * $k))
    $p3 = New-Object System.Windows.Point((184 * $k), (196 * $k))
    $p4 = New-Object System.Windows.Point((148 * $k), (214 * $k))
    $fig = New-Object System.Windows.Media.PathFigure
    $fig.StartPoint = $p1
    $fig.Segments.Add((New-Object System.Windows.Media.LineSegment($p2, $true)))
    $fig.Segments.Add((New-Object System.Windows.Media.LineSegment($p3, $true)))
    $fig.Segments.Add((New-Object System.Windows.Media.LineSegment($p4, $true)))
    $fig.IsClosed = $true
    $geo = New-Object System.Windows.Media.PathGeometry
    $geo.Figures.Add($fig)
    $dc.DrawGeometry($cyan, $null, $geo)
    $dc.DrawRectangle($cyan, $null, (Rect1 176 180 16 42))
    $dc.DrawRectangle($white2, $null, (Rect1 200 168 16 54))
    $dc.Close()
    $bmp.Render($dv)
    return $bmp
}

$sizes = [int[]](16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($s in $sizes) {
    $frames += , @($s, (New-TiaIconBitmap $s))
}

# Save a PNG preview so the artwork can be inspected.
$preview = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$preview.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($frames[-1][1]))
$fs = [System.IO.File]::Create((Join-Path $dir "TiaMC-preview.png"))
$preview.Save($fs)
$fs.Dispose()
Write-Host "preview written"

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count

$pngs = @()
foreach ($frame in $frames) {
    $s = [int]$frame[0]
    $bmp = $frame[1]
    $pms = New-Object System.IO.MemoryStream
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $enc.Save($pms)
    $bytes = $pms.ToArray()
    $pngs += , $bytes

    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
    Write-Host ("  frame {0}x{0} -> {1} bytes" -f $s, $bytes.Length)
}
foreach ($bytes in $pngs) { $bw.Write($bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $dir "TiaMC.ico"), $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
Write-Host ("ico written: {0} bytes" -f (Get-Item (Join-Path $dir "TiaMC.ico")).Length)


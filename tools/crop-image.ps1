# Crops a region out of a screenshot and saves it enlarged, so UI details of the
# TIA Portal window inside the VMware guest can be inspected.
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [Parameter(Mandatory = $true)][int]$Width,
    [Parameter(Mandatory = $true)][int]$Height,
    [double]$Scale = 1.0
)

Add-Type -AssemblyName System.Drawing

$image = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
try {
    $rect = New-Object System.Drawing.Rectangle($X, $Y, $Width, $Height)
    $crop = New-Object System.Drawing.Bitmap($rect.Width, $rect.Height)
    $g = [System.Drawing.Graphics]::FromImage($crop)
    $g.DrawImage($image, (New-Object System.Drawing.Rectangle(0, 0, $rect.Width, $rect.Height)), $rect, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()

    if ($Scale -ne 1.0) {
        $big = New-Object System.Drawing.Bitmap([int]($rect.Width * $Scale), [int]($rect.Height * $Scale))
        $g2 = [System.Drawing.Graphics]::FromImage($big)
        $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        $g2.DrawImage($crop, 0, 0, $big.Width, $big.Height)
        $g2.Dispose()
        $crop.Dispose()
        $crop = $big
    }

    $crop.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose()
    Write-Host "saved $Out ($($rect.Width)x$($rect.Height) -> scale $Scale)"
}
finally {
    $image.Dispose()
}

Add-Type -AssemblyName System.Drawing

$stagingDir = "installer\staging_png"
$installerDir = "installer"

$slideNames = @(
    "slide_dashboard",
    "slide_login",
    "slide_employees",
    "slide_devices",
    "slide_shifts",
    "slide_attendance",
    "slide_leaves",
    "slide_reports",
    "slide_settings",
    "slide_setup",
    "slide_access"
)

Write-Host "Converting staging PNGs to 620x295 24-bit BMPs..."

foreach ($name in $slideNames) {
    $pngPath = Join-Path $stagingDir "$name.png"
    $bmpPath = Join-Path $installerDir "$name.bmp"

    if (-not (Test-Path $pngPath)) {
        Write-Warning "Missing: $pngPath"
        continue
    }

    $src = [System.Drawing.Image]::FromFile((Resolve-Path $pngPath).Path)
    $dest = New-Object System.Drawing.Bitmap 620, 295, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($dest)

    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    $g.DrawImage($src, 0, 0, 620, 295)

    $dest.Save($bmpPath, [System.Drawing.Imaging.ImageFormat]::Bmp)

    $g.Dispose()
    $src.Dispose()
    $dest.Dispose()

    $fi = Get-Item $bmpPath
    Write-Host "[OK] Generated $($fi.Name) - $($fi.Length) bytes (620x295 24bpp)"
}

Write-Host "All slides converted successfully!"

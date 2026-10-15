Add-Type -AssemblyName System.Drawing

$assetDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null

$icons = @(
    @{ Name = 'StoreLogo.png'; Size = 50 },
    @{ Name = 'Square44x44Logo.png'; Size = 44 },
    @{ Name = 'Square150x150Logo.png'; Size = 150 }
)

foreach ($icon in $icons) {
    $size = [int]$icon.Size
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#0B0D17'))

    $center = $size / 2.0
    $extent = $size * 0.37
    $diamond = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($center, $center - $extent),
        [System.Drawing.PointF]::new($center + $extent, $center),
        [System.Drawing.PointF]::new($center, $center + $extent),
        [System.Drawing.PointF]::new($center - $extent, $center)
    )
    $purple = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#6C5CE7'))
    $graphics.FillPolygon($purple, $diamond)

    $play = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($center - $size * 0.08, $center - $size * 0.16),
        [System.Drawing.PointF]::new($center + $size * 0.18, $center),
        [System.Drawing.PointF]::new($center - $size * 0.08, $center + $size * 0.16)
    )
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $graphics.FillPolygon($white, $play)

    $bitmap.Save((Join-Path $assetDirectory $icon.Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $white.Dispose()
    $purple.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

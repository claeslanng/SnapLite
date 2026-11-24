# Regenerates the README screenshots in docs\ (needs a Release build: dotnet build -c Release).
# Uses only the demo pages in docs\demo, so nothing personal ends up in the pictures.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'src\SnapLite\bin\Release\net10.0-windows\SnapLite.exe'
$docs = $PSScriptRoot
$work = Join-Path $env:TEMP 'snaplite-docs'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory $work | Out-Null
Add-Type -AssemblyName System.Drawing

function Invoke-Capture([string[]]$CaptureArgs) {
    $p = Start-Process $exe -ArgumentList $CaptureArgs -PassThru -Wait -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "SnapLite $($CaptureArgs -join ' ') failed ($($p.ExitCode))" }
}

$article = 'file:///' + ((Join-Path $docs 'demo\demo-article.html') -replace '\\', '/')
$popup = 'file:///' + ((Join-Path $docs 'demo\popup-preview.html') -replace '\\', '/')

# 1. Full-page web capture of the demo article, with and without clean-up; and in steps.
Invoke-Capture @('--capture-url', $article, '--out', "$work\clean.png", '--width', '1280')
Invoke-Capture @('--capture-url', $article, '--out', "$work\raw.png", '--width', '1280', '--no-cleanup')
Invoke-Capture @('--capture-url', $article, '--out', "$work\steps", '--width', '1280', '--steps', 'smart')
Invoke-Capture @('--capture-url', $popup, '--out', "$work\popup.png", '--width', '340')

# 2. Notification and settings window (rendered off-screen).
Invoke-Capture @('--render-docs', $docs, "$work\clean.png")

function Save-Scaled([System.Drawing.Image]$img, [double]$scale, [string]$path) {
    $w = [int]($img.Width * $scale); $h = [int]($img.Height * $scale)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.DrawImage($img, 0, 0, $w, $h); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

function New-Sheet([string[]]$files, [double]$scale, [string[]]$labels, [string]$path) {
    $imgs = $files | ForEach-Object { [System.Drawing.Image]::FromFile($_) }
    $gap = 28; $top = 44; $pad = 28
    $w = [int](($imgs | Measure-Object Width -Maximum).Maximum * $scale)
    $h = [int](($imgs | Measure-Object Height -Maximum).Maximum * $scale)
    $sheet = New-Object System.Drawing.Bitmap ($pad * 2 + ($w + $gap) * $imgs.Count - $gap), ($top + $h + $pad)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(243, 244, 247))
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.TextRenderingHint = 'ClearTypeGridFit'
    $font = New-Object System.Drawing.Font('Segoe UI Semibold', 13)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(205, 209, 218))
    for ($i = 0; $i -lt $imgs.Count; $i++) {
        $x = $pad + ($w + $gap) * $i
        $ih = [int]($imgs[$i].Height * $scale)
        $g.DrawString($labels[$i], $font, [System.Drawing.Brushes]::DimGray, $x, 12)
        $g.DrawImage($imgs[$i], $x, $top, $w, $ih)
        $g.DrawRectangle($pen, $x - 1, $top - 1, $w + 1, $ih + 1)
    }
    $g.Dispose(); $sheet.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $sheet.Dispose()
    $imgs | ForEach-Object { $_.Dispose() }
}

# 3. Steps: all step images side by side, with their numbers and headings.
$stepDir = Get-ChildItem "$work\steps" -Directory | Select-Object -First 1
$stepFiles = Get-ChildItem $stepDir.FullName -Filter *.png | Sort-Object Name
$titles = Select-String (Join-Path $stepDir.FullName 'steps.md') -Pattern '^## (\d+)\. (.*)$' | ForEach-Object { "$($_.Matches[0].Groups[1].Value). $($_.Matches[0].Groups[2].Value)" }
New-Sheet ($stepFiles.FullName) 0.3 $titles (Join-Path $docs 'steps-example.png')

# 4. Clean-up: the first screen without and with clean-up (cookie banner and chat bubble gone).
foreach ($n in 'raw', 'clean') {
    $img = [System.Drawing.Image]::FromFile("$work\$n.png")
    $crop = ([System.Drawing.Bitmap]$img).Clone((New-Object System.Drawing.Rectangle 0, 0, $img.Width, 900), $img.PixelFormat)
    $crop.Save("$work\$n-top.png"); $crop.Dispose(); $img.Dispose()
}
New-Sheet @("$work\raw-top.png", "$work\clean-top.png") 0.5 @('Without clean-up', 'With clean-up (default)') (Join-Path $docs 'cleanup.png')

# 5. Full page, scaled down.
$full = [System.Drawing.Image]::FromFile("$work\clean.png"); Save-Scaled $full 0.35 (Join-Path $docs 'full-page.png'); $full.Dispose()

# 6. Extension popup, cropped to the popup (plus its shadow).
$pop = [System.Drawing.Bitmap]::FromFile("$work\popup.png")
$bg = $pop.GetPixel(5, $pop.Height - 5).ToArgb()
$bottom = $pop.Height - 1
while ($bottom -gt 0 -and $pop.GetPixel(160, $bottom).ToArgb() -eq $bg) { $bottom-- }
$cropped = $pop.Clone((New-Object System.Drawing.Rectangle 0, 0, $pop.Width, ([Math]::Min($pop.Height, $bottom + 16))), $pop.PixelFormat)
$cropped.Save((Join-Path $docs 'extension-popup.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$cropped.Dispose(); $pop.Dispose()

Get-ChildItem $docs -Filter *.png | ForEach-Object { '{0,-24} {1,6:N0} KB' -f $_.Name, ($_.Length / 1KB) }

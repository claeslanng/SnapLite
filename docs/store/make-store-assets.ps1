# Makes the Chrome Web Store images in docs\store\ (icon, 1280x800 screenshots, 440x280 promo tile).
# Run docs\make-screenshots.ps1 first (it creates the pictures these are composed from).
# Each image is a small HTML page photographed by SnapLite's own web capture.
$ErrorActionPreference = 'Stop'
$store = $PSScriptRoot
$docs = Split-Path $store -Parent
$root = Split-Path $docs -Parent
$exe = Join-Path $root 'src\SnapLite\bin\Release\net10.0-windows\SnapLite.exe'
$work = Join-Path $env:TEMP 'snaplite-store'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory $work | Out-Null
Add-Type -AssemblyName System.Drawing

function Invoke-SnapLite([string[]]$a) {
    $p = Start-Process $exe -ArgumentList $a -PassThru -Wait -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "SnapLite $($a -join ' ') failed ($($p.ExitCode))" }
}

# Source pictures: the README screenshots, three steps of the demo article, the extension icon.
Copy-Item (Join-Path $docs '*.png') $work
$article = 'file:///' + ((Join-Path $docs 'demo\demo-article.html') -replace '\\', '/')
Invoke-SnapLite @('--capture-url', $article, '--out', "$work\steps", '--width', '1280', '--steps', 'smart')
$stepFiles = Get-ChildItem (Get-ChildItem "$work\steps" -Directory)[0].FullName -Filter *.png | Sort-Object Name
1..4 | ForEach-Object { Copy-Item $stepFiles[$_].FullName "$work\step$_.png" }
Invoke-SnapLite @('--export-extension', "$work\ext", '--store')
Copy-Item "$work\ext\icons\icon128.png" "$work\logo.png"

$css = @'
* { box-sizing: border-box; margin: 0; }
html, body { width: 1280px; height: 800px; overflow: hidden; }
body { font-family: "Segoe UI", system-ui, sans-serif; color: #fff;
       background: linear-gradient(135deg, #0b5cad 0%, #1677d2 55%, #2b8ae6 100%); padding: 56px 64px; }
.brand { display: flex; align-items: center; gap: 12px; font-size: 22px; font-weight: 600; opacity: .95; }
.brand img { width: 34px; height: 34px; }
h1 { font-size: 46px; line-height: 1.15; margin: 26px 0 10px; font-weight: 700; letter-spacing: -.5px; }
p.sub { font-size: 22px; opacity: .9; margin-bottom: 34px; }
.card { background: #fff; border-radius: 12px; box-shadow: 0 18px 50px rgba(0,0,0,.28); overflow: hidden; }
.card img { display: block; width: 100%; }
.label { color: #1d2433; font-size: 15px; font-weight: 600; padding: 10px 14px; border-bottom: 1px solid #e3e6ee; }
.label b { color: #1677d2; margin-right: 6px; }
'@

function New-Shot([string]$name, [string]$body, [int]$width = 1280, [int]$height = 800, [string]$extraCss = '') {
    $html = "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width'><style>$css $extraCss</style></head><body>$body</body></html>"
    $file = Join-Path $work "$name.html"
    Set-Content $file $html -Encoding utf8
    $png = Join-Path $work "$name-raw.png"
    Invoke-SnapLite @('--capture-url', ('file:///' + ($file -replace '\\', '/')), '--out', $png, '--width', "$width", '--no-cleanup')
    $img = [System.Drawing.Bitmap]::FromFile($png)
    $crop = $img.Clone((New-Object System.Drawing.Rectangle 0, 0, $width, ([Math]::Min($height, $img.Height))), $img.PixelFormat)
    $crop.Save((Join-Path $store "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose(); $img.Dispose()
}

$brand = "<div class='brand'><img src='logo.png'>SnapLite</div>"

# The popup is drawn from the same static preview page as the README picture, on the blue background.
$preview = Get-Content (Join-Path $docs 'demo\popup-preview.html') -Raw -Encoding UTF8
$popupCss = ([regex]::Match($preview, '<style>([\s\S]*?)</style>').Groups[1].Value -split "`n" | Where-Object { $_ -notmatch '^\s*(html|body)\b' }) -join "`n"
$popupCss = $popupCss.Replace(':root', '.popup').Replace('box-shadow: 0 8px 24px rgba(0,0,0,.18)', 'box-shadow: 0 18px 50px rgba(0,0,0,.3); color: #1b1b1b; font: 13px/1.4 "Segoe UI", system-ui, sans-serif')
$popupMarkup = "<style>$popupCss</style>" + [regex]::Match($preview, '<div class="popup">[\s\S]*</div>\s*</body>').Value.Replace('</body>', '')

New-Shot 'screenshot-1-steps' @"
$brand<h1>Turn any page into numbered steps</h1><p class='sub'>One image per section, each starting at its heading, plus a steps.md index.</p>
<div style='display:grid;grid-template-columns:repeat(2,1fr);gap:22px 28px'>
<div class='card'><div class='label'><b>1</b>Install the app</div><img src='step1.png' style='height:195px;object-fit:cover;object-position:top'></div>
<div class='card'><div class='label'><b>2</b>Create your first note</div><img src='step2.png' style='height:195px;object-fit:cover;object-position:top'></div>
<div class='card'><div class='label'><b>3</b>Organise with tags</div><img src='step3.png' style='height:195px;object-fit:cover;object-position:top'></div>
<div class='card'><div class='label'><b>4</b>Sync across devices</div><img src='step4.png' style='height:195px;object-fit:cover;object-position:top'></div></div>
"@

New-Shot 'screenshot-2-clean' @"
$brand<h1>Clean, full-page captures</h1><p class='sub'>Cookie banners, pop-ups and chat bubbles are hidden while capturing, then the page is put back.</p>
<div class='card' style='padding:18px;background:#f3f4f7'><img src='cleanup.png'></div>
"@

New-Shot 'screenshot-3-popup' @"
<div style='display:grid;grid-template-columns:1fr 360px;gap:56px;align-items:start'>
<div>$brand<h1>One click, or a shortcut</h1><p class='sub'>Full page, page in steps, or just the visible part, on any tab, even pages you're logged in to.</p>
<div style='font-size:20px;line-height:2;opacity:.95'>Alt+Shift+F &nbsp;Full page<br>Alt+Shift+S &nbsp;Page in steps<br>Alt+Shift+V &nbsp;Visible part</div></div>
<div style='zoom:1.2;margin-top:10px'>$popupMarkup</div></div>
"@

New-Shot 'screenshot-4-app' @"
$brand<h1>Saved straight to your folder and clipboard</h1><p class='sub'>With the free SnapLite app for Windows, which also captures regions, windows and the whole screen.</p>
<div style='display:grid;grid-template-columns:1fr 1fr;gap:40px;align-items:start'>
<div><img src='toast-light.png' style='width:100%;border-radius:8px;box-shadow:0 18px 50px rgba(0,0,0,.28)'>
<img src='toast-dark.png' style='width:100%;margin-top:22px;border-radius:8px;box-shadow:0 18px 50px rgba(0,0,0,.28)'></div>
<img src='settings-shortcuts.png' style='width:100%;border-radius:8px;box-shadow:0 18px 50px rgba(0,0,0,.28)'></div>
"@

New-Shot 'promo-tile-440x280' @"
<div style='display:flex;flex-direction:column;justify-content:center;height:100%;gap:14px'>
<div style='display:flex;align-items:center;gap:16px'><img src='logo.png' style='width:64px;height:64px'>
<span style='font-size:44px;font-weight:700;letter-spacing:-.5px'>SnapLite</span></div>
<div style='font-size:21px;line-height:1.35;opacity:.95'>Full-page and step-by-step<br>screenshots of any tab</div></div>
"@ -width 440 -height 280 -extraCss 'html, body { width: 440px; height: 280px; } body { padding: 0 34px; }'

# Store icon: 96 px of artwork centred in 128 px, as the Web Store asks.
$logo = [System.Drawing.Image]::FromFile("$work\logo.png")
$icon = New-Object System.Drawing.Bitmap 128, 128
$g = [System.Drawing.Graphics]::FromImage($icon)
$g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
$g.DrawImage($logo, 16, 16, 96, 96); $g.Dispose(); $logo.Dispose()
$icon.Save((Join-Path $store 'store-icon-128.png'), [System.Drawing.Imaging.ImageFormat]::Png); $icon.Dispose()

Get-ChildItem $store -Filter *.png | ForEach-Object {
    $i = [System.Drawing.Image]::FromFile($_.FullName); '{0,-26} {1}x{2}' -f $_.Name, $i.Width, $i.Height; $i.Dispose()
}
